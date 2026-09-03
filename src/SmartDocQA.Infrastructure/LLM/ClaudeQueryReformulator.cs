using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure;

// ─── Claude Query Reformulator (Phase 7.5) ───────────────────────────────────
//
// Rewrites a sub-question that failed IAnswerVerifier's check, using the
// verifier's specific failure reason to target the gap on retry, rather
// than blindly re-running the exact same query and failing identically.

public class ClaudeQueryReformulator : IQueryReformulator
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly ILogger<ClaudeQueryReformulator> _logger;

    public ClaudeQueryReformulator(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        ILogger<ClaudeQueryReformulator> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _prompts = prompts.Value;
        _logger = logger;
    }

    public async Task<string> ReformulateAsync(
        string originalSubQuestion,
        string failureReason,
        CancellationToken ct = default)
    {
        var template = _promptLoader.Load(_prompts.ReformulateQuery);
        var userPrompt = template
            .Replace("{{originalSubQuestion}}", originalSubQuestion)
            .Replace("{{failureReason}}", failureReason);

        var reformulated = await _llmClient.CompleteAsync(
            systemPrompt: "You are a precise search query reformulation assistant. Return ONLY the reformulated question, no explanation.",
            userPrompt: userPrompt,
            ct: ct);

        var trimmed = reformulated.Trim();

        // Defensive fallback: if Claude returns an empty string for any
        // reason, fall back to the original sub-question unchanged rather
        // than sending an empty query into the retrieval pipeline -- same
        // fail-soft philosophy as every other LLM-call wrapper in this
        // codebase (ClaudeQueryDecomposer, ClaudeAnswerVerifier).
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            _logger.LogWarning(
                "Reformulation returned empty response for '{Original}' — keeping original sub-question",
                originalSubQuestion);
            return originalSubQuestion;
        }

        _logger.LogInformation(
            "Reformulated sub-question: '{Original}' → '{Reformulated}' (reason: {Reason})",
            originalSubQuestion, trimmed, failureReason);

        return trimmed;
    }
}