using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Claude Answer Verifier (Phase 7.5) ──────────────────────────────────────
//
// Checks whether retrieved chunks for a single sub-question actually
// answer it, as part of the agentic query pipeline's retrieve -> verify
// -> retry loop. Deliberately built as its own LLM call rather than
// reusing the output grounding guardrail -- this runs PER sub-question,
// BEFORE synthesis, and its failure triggers a retry rather than
// blocking the whole response. See IAnswerVerifier's XML doc comment for
// the full design rationale.

public class ClaudeAnswerVerifier : IAnswerVerifier
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly ILogger<ClaudeAnswerVerifier> _logger;

    public ClaudeAnswerVerifier(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        ILogger<ClaudeAnswerVerifier> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _prompts = prompts.Value;
        _logger = logger;
    }

    public async Task<VerificationResult> VerifyAsync(
        string subQuestion,
        List<RankedChunk> rankedChunks,
        CancellationToken ct = default)
    {
        // No evidence at all is an immediate, cheap fail -- no need to
        // spend an LLM call asking "does nothing answer this question."
        if (rankedChunks.Count == 0)
        {
            _logger.LogDebug(
                "Verification skipped (no evidence) for sub-question: '{Question}'",
                subQuestion);
            return new VerificationResult(
                Verified: false,
                Reason: "No evidence was retrieved for this sub-question.");
        }

        var evidenceText = BuildEvidenceText(rankedChunks);

        var template = _promptLoader.Load(_prompts.VerifyAnswer);
        var userPrompt = template
            .Replace("{{subQuestion}}", subQuestion)
            .Replace("{{evidence}}", evidenceText);

        var rawResponse = await _llmClient.CompleteAsync(
            systemPrompt: "You are a strict, precise evidence-verification assistant. Return ONLY valid JSON.",
            userPrompt: userPrompt,
            ct: ct);

        var result = ParseVerificationResponse(rawResponse, subQuestion);

        _logger.LogInformation(
            "Verification for '{Question}': Verified={Verified} | Reason={Reason}",
            subQuestion, result.Verified, result.Reason);

        return result;
    }

    // Builds a readable, bounded-length evidence block from the ranked
    // chunks. Truncates each chunk's content to keep the verification
    // prompt small -- the verifier only needs enough text to judge
    // relevance, not the full chunk.
    private static string BuildEvidenceText(List<RankedChunk> rankedChunks)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < rankedChunks.Count; i++)
        {
            var content = rankedChunks[i].Chunk.Content;
            var truncated = content.Length > 400 ? content[..400] + "..." : content;
            sb.AppendLine($"EVIDENCE_{i + 1}: {truncated}");
        }
        return sb.ToString();
    }

    // Defensive parsing, same fail-soft philosophy as ClaudeQueryDecomposer
    // and ClaudeReranker elsewhere in this codebase: if Claude's response
    // can't be parsed, we don't crash the pipeline -- we fail the
    // verification conservatively (Verified=false), which simply causes
    // this sub-question to be retried rather than silently treated as
    // successful on bad data.
    private VerificationResult ParseVerificationResponse(string rawResponse, string subQuestion)
    {
        var cleaned = rawResponse.Trim();
        if (cleaned.StartsWith("```"))
        {
            cleaned = cleaned
                .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                .Replace("```", "")
                .Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var verified = doc.RootElement.GetProperty("verified").GetBoolean();
            var reason = doc.RootElement.GetProperty("reason").GetString()
                ?? "(no reason provided)";
            return new VerificationResult(verified, reason);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex,
                "Failed to parse verification response for sub-question '{Question}' — " +
                "conservatively treating as unverified. Raw response: {Raw}",
                subQuestion, rawResponse);

            return new VerificationResult(
                Verified: false,
                Reason: "Verification response could not be parsed; treated as unverified to be safe.");
        }
    }
}
