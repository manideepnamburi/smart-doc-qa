using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Claude Agent Answer Synthesizer (Phase 7.5) ─────────────────────────────
//
// Combines every sub-question's verified (or best-effort) evidence into
// one final answer, with citations traceable back to which sub-question
// each fact came from. This is the Option B design deliberately chosen
// over pooling all chunks and reusing IAnswerSynthesizer: each fact in
// the final answer carries a real link back to its specific source
// evidence, not just a flat, unattributed citation list.
//
// HOW ATTRIBUTION WORKS:
// Every ranked chunk across every sub-question gets a unique evidence ID
// in the form "{subQuestionIndex}.{chunkIndex}" (e.g. "1.2" = sub-question
// 1's second chunk). The synthesis prompt instructs Claude to tag each
// fact it states with [EVIDENCE_evidenceId] inline. After the response
// comes back, this class extracts those markers via regex, looks up which
// RankedChunk each one refers to, and builds real Citation objects from
// them -- then strips the markers from the final prose so the user sees
// clean text, with citations surfaced separately (matching how the
// existing QAResult.Citations list already works).

public class ClaudeAgentAnswerSynthesizer : IAgentAnswerSynthesizer
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly ILogger<ClaudeAgentAnswerSynthesizer> _logger;

    // Matches evidence markers like "[EVIDENCE_1.2]" -- group 1 is the
    // sub-question index, group 2 is the chunk index within that
    // sub-question.
    private static readonly System.Text.RegularExpressions.Regex EvidenceMarkerPattern =
        new(@"\[EVIDENCE_(\d+)\.(\d+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ClaudeAgentAnswerSynthesizer(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        ILogger<ClaudeAgentAnswerSynthesizer> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _prompts = prompts.Value;
        _logger = logger;
    }

    public async Task<AgentQueryResponse> SynthesizeAsync(
        string originalQuestion,
        List<SubQuestionResult> subQuestionResults,
        CancellationToken ct = default)
    {
        // Build the evidence blocks AND a lookup dictionary in the same
        // pass -- the dictionary lets us resolve each [evidenceId] marker
        // back to its real RankedChunk once Claude's response comes back.
        var (evidenceBlocks, evidenceLookup) = BuildEvidenceBlocksAndLookup(subQuestionResults);

        var template = _promptLoader.Load(_prompts.AgentSynthesize);
        var userPrompt = template
            .Replace("{{originalQuestion}}", originalQuestion)
            .Replace("{{subQuestionBlocks}}", evidenceBlocks);

        var rawAnswer = await _llmClient.CompleteAsync(
            systemPrompt: "You are a precise answer synthesis assistant. Follow the citation format exactly as instructed.",
            userPrompt: userPrompt,
            ct: ct);

        var (cleanedAnswer, citations) = ExtractCitationsAndCleanAnswer(rawAnswer, evidenceLookup);

        _logger.LogInformation(
            "Agent synthesis complete: {SubQuestionCount} sub-questions → {CitationCount} citations",
            subQuestionResults.Count, citations.Count);

        return new AgentQueryResponse(
            Answer: cleanedAnswer,
            SubQuestions: subQuestionResults,
            Citations: citations,
            ProcessingTime: TimeSpan.Zero); // caller (AgentQueryUseCase) overrides this with the real elapsed time
    }

    // Builds the prompt's evidence section AND a dictionary mapping each
    // evidence ID (e.g. "1.2") to the RankedChunk it refers to, so
    // citations can be resolved after the LLM call without a second pass
    // over the sub-question results.
    private static (string Blocks, Dictionary<string, RankedChunk> Lookup) BuildEvidenceBlocksAndLookup(
        List<SubQuestionResult> subQuestionResults)
    {
        var sb = new System.Text.StringBuilder();
        var lookup = new Dictionary<string, RankedChunk>();

        for (int q = 0; q < subQuestionResults.Count; q++)
        {
            var subQuestion = subQuestionResults[q];
            var qIndex = q + 1;

            var statusLabel = subQuestion.Verified
                ? "VERIFIED"
                : $"UNVERIFIED — reason: {subQuestion.VerificationReason ?? "no evidence found after retries"}";

            sb.AppendLine($"Sub-question {qIndex}: {subQuestion.Question} [{statusLabel}]");

            if (subQuestion.RankedChunks.Count == 0)
            {
                sb.AppendLine("  (no evidence retrieved for this sub-question)");
            }
            else
            {
                for (int c = 0; c < subQuestion.RankedChunks.Count; c++)
                {
                    var chunk = subQuestion.RankedChunks[c];
                    var chunkIndex = c + 1;
                    var evidenceId = $"{qIndex}.{chunkIndex}";

                    lookup[evidenceId] = chunk;

                    var content = chunk.Chunk.Content;
                    var truncated = content.Length > 300 ? content[..300] + "..." : content;

                    sb.AppendLine($"  EVIDENCE_{evidenceId}: {truncated}");
                }
            }

            sb.AppendLine();
        }

        return (sb.ToString(), lookup);
    }

    // Finds every [evidenceId] marker in the raw answer, resolves each to
    // a real Citation via the lookup dictionary, deduplicates (the same
    // evidence might support multiple sentences), and returns the answer
    // text with markers stripped so the user sees clean prose.
    private (string CleanedAnswer, List<Citation> Citations) ExtractCitationsAndCleanAnswer(
        string rawAnswer,
        Dictionary<string, RankedChunk> evidenceLookup)
    {
        var citations = new List<Citation>();
        var seenChunkIds = new HashSet<string>();

        var matches = EvidenceMarkerPattern.Matches(rawAnswer);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var evidenceId = $"{match.Groups[1].Value}.{match.Groups[2].Value}";

            if (!evidenceLookup.TryGetValue(evidenceId, out var rankedChunk))
            {
                // Claude referenced an evidence ID that doesn't exist in
                // our lookup (e.g. a typo or hallucinated marker) -- log
                // and skip it rather than crashing synthesis over a
                // citation-formatting slip.
                _logger.LogWarning(
                    "Agent synthesis referenced unknown evidence ID '{Id}' — skipping citation",
                    evidenceId);
                continue;
            }

            var chunk = rankedChunk.Chunk;

            // Skip duplicates -- the same chunk may be cited multiple
            // times across the answer, but should only appear once in
            // the final citation list.
            if (!seenChunkIds.Add(chunk.ChunkId))
                continue;

            var excerpt = chunk.Content.Length > 200
                ? chunk.Content[..200] + "..."
                : chunk.Content;

            citations.Add(new Citation(
                ChunkId: chunk.ChunkId,
                FileName: chunk.FileName,
                PageNumber: chunk.PageNumber,
                ChunkType: chunk.ChunkType,
                RelevantExcerpt: excerpt));
        }

        // Strip all [EVIDENCE_n.n] markers from the visible answer text --
        // citations are surfaced separately via the Citations list,
        // matching how QAResult already presents citations apart from
        // the answer prose.
        var cleanedAnswer = EvidenceMarkerPattern.Replace(rawAnswer, "").Trim();

        // Collapse any double-spaces left behind where a marker used to
        // sit mid-sentence (e.g. "the rate was 12% [EVIDENCE_1.2] among
        // adults" becomes "the rate was 12%  among adults" after
        // stripping -- this tidies that to a single space).
        cleanedAnswer = System.Text.RegularExpressions.Regex.Replace(cleanedAnswer, @" {2,}", " ");

        return (cleanedAnswer, citations);
    }
}
