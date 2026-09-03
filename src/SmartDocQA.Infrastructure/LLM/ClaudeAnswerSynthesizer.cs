using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure.LLM;

/// <summary>
/// Synthesizes a final answer from ranked chunks using Claude.
/// Supports three modes:
///   1. Document answer   — chunks found, answer from document with citations
///   2. LLM fallback      — no chunks found + FallbackToLLM=true → Claude general knowledge
///   3. Not found         — no chunks found + FallbackToLLM=false → explicit not found response
/// </summary>
public class ClaudeAnswerSynthesizer : IAnswerSynthesizer
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly ILogger<ClaudeAnswerSynthesizer> _logger;

    public ClaudeAnswerSynthesizer(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        ILogger<ClaudeAnswerSynthesizer> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _prompts = prompts.Value;
        _logger = logger;
    }

    public async Task<QAResult> SynthesizeAsync(
        QAQuery query,
        List<RankedChunk> rankedChunks,
        CancellationToken ct = default)
    {
        // ── No relevant chunks found ──────────────────────────────────────────
        if (rankedChunks.Count == 0)
        {
            if (query.FallbackToLLM)
            {
                _logger.LogInformation(
                    "No relevant chunks found — falling back to LLM general knowledge");
                return await SynthesizeFromLLMAsync(query, ct);
            }

            _logger.LogInformation(
                "No relevant chunks found — returning NotFound (FallbackToLLM=false)");

            return new QAResult(
                Answer: "I could not find relevant information in the " +
                        "provided documents to answer this question. " +
                        "If you would like an answer from general AI knowledge, " +
                        "set 'fallbackToLLM' to true in your request.",
                Citations: new List<Citation>(),
                Confidence: ConfidenceLevel.NotFound,
                RetrievalMode: RetrievalMode.Hybrid,
                AnswerSource: AnswerSource.NotFound,
                ChunksRetrieved: 0,
                ChunksAfterRerank: 0,
                ProcessingTime: TimeSpan.Zero);
        }

        // ── Chunks found → answer from document ──────────────────────────────
        return await SynthesizeFromDocumentAsync(query, rankedChunks, ct);
    }

    // ─── Answer from document chunks ─────────────────────────────────────────

    private async Task<QAResult> SynthesizeFromDocumentAsync(
        QAQuery query,
        List<RankedChunk> rankedChunks,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "Synthesizing answer from {Count} document chunks", rankedChunks.Count);

        // Build context block with chunk IDs so Claude can cite them
        var contextBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < rankedChunks.Count; i++)
        {
            var chunk = rankedChunks[i].Chunk;
            contextBuilder.AppendLine(
                $"[CHUNK:{chunk.ChunkId}] Page {chunk.PageNumber} ({chunk.ChunkType}):");
            contextBuilder.AppendLine(chunk.Content);
            contextBuilder.AppendLine();
        }

        var systemPrompt = _promptLoader.Load(_prompts.QASystem);
        var userPrompt = _promptLoader.LoadAndFill(_prompts.QAUser,
            new Dictionary<string, string>
            {
                ["context"] = contextBuilder.ToString(),
                ["question"] = query.Question
            });

        var rawAnswer = await _llmClient.CompleteAsync(systemPrompt, userPrompt, ct);
        var citations = ExtractCitations(rawAnswer, rankedChunks);
        var answer = StripCitationLine(rawAnswer);
        var confidence = DetermineConfidence(answer, rankedChunks.Count);

        // Claude said "not found" even though we had chunks
        // If user wants fallback → try LLM general knowledge
        if (confidence == ConfidenceLevel.NotFound && query.FallbackToLLM)
        {
            _logger.LogInformation(
                "Claude said not found in chunks — falling back to LLM general knowledge");
            return await SynthesizeFromLLMAsync(query, ct);
        }

        return new QAResult(
            Answer: answer,
            Citations: citations,
            Confidence: confidence,
            RetrievalMode: RetrievalMode.Hybrid,
            AnswerSource: AnswerSource.Document,
            ChunksRetrieved: rankedChunks.Count,
            ChunksAfterRerank: rankedChunks.Count,
            ProcessingTime: TimeSpan.Zero);
    }

    // ─── Answer from Claude's general knowledge (fallback) ────────────────────

    private async Task<QAResult> SynthesizeFromLLMAsync(
        QAQuery query, CancellationToken ct)
    {
        _logger.LogDebug(
            "Synthesizing answer from LLM general knowledge for: {Question}",
            query.Question);

        var systemPrompt =
            "You are a knowledgeable assistant. The user's question was not found " +
            "in their document collection, but they have explicitly requested an " +
            "answer from your general knowledge. Answer clearly and accurately. " +
            "Always start your response with exactly this prefix: " +
            "\"[This answer is based on general AI knowledge, not your documents.] \"";

        var answer = await _llmClient.CompleteAsync(
            systemPrompt, query.Question, ct);

        return new QAResult(
            Answer: answer,
            Citations: new List<Citation>(),   // no document citations
            Confidence: ConfidenceLevel.Medium,
            RetrievalMode: RetrievalMode.Hybrid,
            AnswerSource: AnswerSource.LLMFallback,
            ChunksRetrieved: 0,
            ChunksAfterRerank: 0,
            ProcessingTime: TimeSpan.Zero);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private List<Citation> ExtractCitations(
        string rawAnswer, List<RankedChunk> ranked)
    {
        var citations = new List<Citation>();
        var citationLine = rawAnswer.Split('\n')
            .FirstOrDefault(l => l.TrimStart()
                .StartsWith("CITATIONS:", StringComparison.OrdinalIgnoreCase));

        if (citationLine is null) return citations;

        var chunkIds = citationLine
            .Replace("CITATIONS:", "", StringComparison.OrdinalIgnoreCase)
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s));

        var chunkMap = ranked.ToDictionary(r => r.Chunk.ChunkId, r => r.Chunk);

        foreach (var chunkId in chunkIds)
        {
            if (!chunkMap.TryGetValue(chunkId, out var chunk)) continue;
            citations.Add(new Citation(
                ChunkId: chunk.ChunkId,
                FileName: chunk.FileName,
                PageNumber: chunk.PageNumber,
                ChunkType: chunk.ChunkType,
                RelevantExcerpt: chunk.Content.Length > 150
                    ? chunk.Content[..150] + "..."
                    : chunk.Content));
        }

        return citations;
    }

    private static string StripCitationLine(string answer)
    {
        var lines = answer.Split('\n');
        var filtered = lines.Where(l =>
            !l.TrimStart()
              .StartsWith("CITATIONS:", StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', filtered).Trim();
    }

    private static ConfidenceLevel DetermineConfidence(string answer, int chunkCount)
    {
        if (answer.Contains("could not find", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("not mentioned", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("no information", StringComparison.OrdinalIgnoreCase))
            return ConfidenceLevel.NotFound;

        return chunkCount >= 3 ? ConfidenceLevel.High
             : chunkCount >= 1 ? ConfidenceLevel.Medium
             : ConfidenceLevel.Low;
    }
}
