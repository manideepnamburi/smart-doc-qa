using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Claude Reranker (Phase 3 — Real Batch Implementation) ───────────────────
//
// Design decisions:
// 1. PRE-FILTER: Takes only top 5 from RRF before scoring
//    (cost-conscious choice for personal project — see Phase 3 design notes)
// 2. BATCH CALL: Scores ALL chunks in ONE Claude API call
//    (avoids "lost in middle" problem by keeping prompt small)
// 3. FALLBACK: If Claude returns invalid JSON, falls back to RRF score
//    (resilient — never crashes the query pipeline)

public class ClaudeReranker : IReranker
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<ClaudeReranker> _logger;

    public ClaudeReranker(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        IOptions<RagOptions> ragOptions,
        ILogger<ClaudeReranker> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _prompts = prompts.Value;
        _ragOptions = ragOptions.Value;
        _logger = logger;
    }

    public async Task<List<RankedChunk>> RerankAsync(
        string query,
        List<FusedChunk> candidates,
        int topK,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return new List<RankedChunk>();

        // ── Step 1: Pre-filter top N before sending to Claude ─────────────────
        // Cost-conscious design: only send top 5 from RRF to Claude
        // This keeps the prompt small and focused
        var preFiltered = candidates
            .OrderByDescending(c => c.FusedScore)
            .Take(_ragOptions.TopKAfterRerank)
            .ToList();

        _logger.LogInformation(
            "Reranker: pre-filtered {Total} → {Filtered} chunks for batch scoring",
            candidates.Count, preFiltered.Count);

        // ── Step 2: Build ONE batch prompt with ALL pre-filtered chunks ────────
        var systemPrompt = _promptLoader.Load(_prompts.Rerank);

        var chunksText = new System.Text.StringBuilder();
        for (int i = 0; i < preFiltered.Count; i++)
        {
            var chunk = preFiltered[i];
            chunksText.AppendLine($"CHUNK_{i + 1} (id: {chunk.Chunk.ChunkId}):");
            chunksText.AppendLine(chunk.Chunk.Content.Length > 300
                ? chunk.Chunk.Content[..300] + "..."
                : chunk.Chunk.Content);
            chunksText.AppendLine();
        }

        // Build expected JSON format string for Claude
        var expectedFormat = "{" + string.Join(", ",
            preFiltered.Select((_, i) =>
                $"\"chunk_{i + 1}\": <score 1-10>")) + "}";

        var userPrompt =
            $"Question: {query}\n\n" +
            $"Score each chunk 1-10 for how well it answers the question.\n" +
            $"10 = directly answers the question\n" +
            $"1  = completely irrelevant\n\n" +
            $"{chunksText}\n" +
            $"Respond ONLY with valid JSON in this exact format:\n" +
            $"{expectedFormat}";

        // ── Step 3: ONE Claude API call scores ALL chunks ─────────────────────
        List<RankedChunk> ranked;
        try
        {
            var response = await _llmClient.CompleteAsync(
                systemPrompt, userPrompt, ct);

            _logger.LogDebug("Reranker raw response: {Response}", response);

            ranked = ParseBatchScores(response, preFiltered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Batch reranking failed — falling back to RRF scores");

            // Fallback: use RRF scores as reranker scores
            ranked = preFiltered
                .Select(c => new RankedChunk(c.Chunk, c.FusedScore, "rrf-fallback"))
                .ToList();
        }

        var result = ranked
            .OrderByDescending(r => r.RerankerScore)
            .Take(topK)
            .ToList();

        _logger.LogInformation(
            "Reranker complete: {Input} chunks → top {Output} selected | " +
            "Scores: [{Scores}]",
            preFiltered.Count,
            result.Count,
            string.Join(", ", result.Select(r => $"{r.RerankerScore:F1}")));

        return result;
    }

    // ── Parse Claude's batch JSON response ────────────────────────────────────

    private List<RankedChunk> ParseBatchScores(
        string response,
        List<FusedChunk> chunks)
    {
        var ranked = new List<RankedChunk>();

        try
        {
            // Strip any markdown code fences Claude might add
            var cleaned = response.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();
            }

            using var doc = JsonDocument.Parse(cleaned);

            for (int i = 0; i < chunks.Count; i++)
            {
                var key = $"chunk_{i + 1}";
                float score = chunks[i].FusedScore; // default = RRF score

                if (doc.RootElement.TryGetProperty(key, out var scoreElement))
                {
                    score = scoreElement.ValueKind == JsonValueKind.Number
                        ? scoreElement.GetSingle()
                        : float.TryParse(
                            scoreElement.GetString(),
                            out var parsed) ? parsed : chunks[i].FusedScore;
                }

                ranked.Add(new RankedChunk(chunks[i].Chunk, score, "batch-scored"));

                _logger.LogDebug(
                    "Chunk {Key}: score {Score:F1} | {Preview}",
                    key, score,
                    chunks[i].Chunk.Content.Length > 50
                        ? chunks[i].Chunk.Content[..50] + "..."
                        : chunks[i].Chunk.Content);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse reranker JSON response: {Response}",
                response);

            // Fallback: return all chunks with RRF scores
            ranked = chunks
                .Select(c => new RankedChunk(c.Chunk, c.FusedScore, "parse-failed"))
                .ToList();
        }

        return ranked;
    }
}
