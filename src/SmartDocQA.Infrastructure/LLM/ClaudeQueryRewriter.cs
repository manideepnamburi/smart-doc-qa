using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Claude Query Rewriter ────────────────────────────────────────────────────
// Optimizes queries for retrieval and (2) resolves follow-up questions
// using conversation history. Loads its prompt via IPromptLoader from
// query_rewrite.txt rather than a hardcoded inline prompt.

public class ClaudeQueryRewriter : IQueryRewriter
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<ClaudeQueryRewriter> _logger;

    public ClaudeQueryRewriter(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        IOptions<RagOptions> ragOptions,
        ILogger<ClaudeQueryRewriter> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _prompts = prompts.Value;
        _ragOptions = ragOptions.Value;
        _logger = logger;
    }

    public async Task<string> RewriteAsync(
        string originalQuery,
        List<ConversationTurn>? history = null,
        CancellationToken ct = default)
    {
        var template = _promptLoader.Load(_prompts.QueryRewrite);
        var historyText = FormatHistory(history);

        var userPrompt = template
            .Replace("{{history}}", historyText)
            .Replace("{{question}}", originalQuery);

        var rewritten = await _llmClient.CompleteAsync(
            systemPrompt: "You are a search query optimizer. Return ONLY the rewritten query, no explanation.",
            userPrompt: userPrompt,
            ct: ct);

        _logger.LogDebug("Query rewritten: '{Original}' → '{Rewritten}' (history turns used: {Count})",
            originalQuery, rewritten.Trim(), history?.Count ?? 0);

        return rewritten.Trim();
    }

    // Caps how much history is actually USED regardless of how much the
    // caller sends -- defensive against an unbounded/buggy client, keeps
    // prompt size and cost predictable. Only the LAST N turns (most
    // recent) matter for resolving a follow-up question; older turns add
    // token cost without adding resolution value.
    private string FormatHistory(List<ConversationTurn>? history)
    {
        if (history is null || history.Count == 0)
            return "(no prior conversation -- this is the first question)";

        var recentTurns = history
            .TakeLast(_ragOptions.MaxHistoryTurns)
            .ToList();

        var sb = new System.Text.StringBuilder();
        foreach (var turn in recentTurns)
        {
            sb.AppendLine($"User: {turn.Question}");
            // Truncate historical answers -- the rewriter only needs enough
            // of the answer to resolve pronouns/references, not the full
            // text, which keeps this prompt small even after many turns.
            var truncatedAnswer = turn.Answer.Length > 200
                ? turn.Answer[..200] + "..."
                : turn.Answer;
            sb.AppendLine($"Assistant: {truncatedAnswer}");
        }
        return sb.ToString();
    }
}
