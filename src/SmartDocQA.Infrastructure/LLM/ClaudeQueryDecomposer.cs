using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure;

// ─── Claude Query Decomposer (Phase 7.5) ─────────────────────────────────────
//
// Splits a compound question into self-contained sub-questions before
// retrieval, so a question bundling multiple distinct asks (e.g. "compare
// X and Y, and which has a bigger Z") doesn't get flattened into one
// blended retrieval pass that partially misses several of its parts.
// A simple, single-fact question passes through unchanged as a one-item list.

public class ClaudeQueryDecomposer : IQueryDecomposer
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly ILogger<ClaudeQueryDecomposer> _logger;

    public ClaudeQueryDecomposer(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        ILogger<ClaudeQueryDecomposer> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _prompts = prompts.Value;
        _logger = logger;
    }

    public async Task<List<string>> DecomposeAsync(
        string question,
        int maxSubQuestions,
        CancellationToken ct = default)
    {
        var template = _promptLoader.Load(_prompts.DecomposeQuery);
        var userPrompt = template
            .Replace("{{question}}", question)
            .Replace("{{maxSubQuestions}}", maxSubQuestions.ToString());

        var rawResponse = await _llmClient.CompleteAsync(
            systemPrompt: "You are a precise query decomposition assistant. Return ONLY a JSON array of strings.",
            userPrompt: userPrompt,
            ct: ct);

        var subQuestions = TryParseSubQuestions(rawResponse, question, maxSubQuestions);

        _logger.LogInformation(
            "Query decomposition: '{Original}' → {Count} sub-question(s)",
            question, subQuestions.Count);

        return subQuestions;
    }

    // Defensive parsing: if Claude returns malformed JSON, extra prose, or
    // markdown fences despite the prompt saying not to, fall back to
    // treating the ORIGINAL question as a single unsplit item -- same
    // fail-soft philosophy as ClaudeReranker's ParseBatchScores fallback.
    // Decomposition failing should degrade to "act like decomposition
    // never happened," not break the whole agent pipeline.
    private List<string> TryParseSubQuestions(
        string rawResponse, string originalQuestion, int maxSubQuestions)
    {
        var cleaned = rawResponse.Trim();

        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            var lastFence = cleaned.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                cleaned = cleaned[(firstNewline + 1)..lastFence].Trim();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(cleaned);
            if (parsed is { Count: > 0 })
                return parsed.Take(maxSubQuestions).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Query decomposition returned unparseable response, falling back to " +
                "single-question passthrough. Raw response: {Raw}", rawResponse);
        }

        return new List<string> { originalQuestion };
    }
}
