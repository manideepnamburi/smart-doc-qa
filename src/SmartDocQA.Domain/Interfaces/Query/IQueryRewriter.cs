using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Rewrites ambiguous or vague queries for better retrieval. When
/// conversation history is provided, also resolves follow-up questions
/// ("what about women?") into fully self-contained search queries using
/// that context.
/// </summary>
public interface IQueryRewriter
{
    Task<string> RewriteAsync(
        string originalQuery,
        List<ConversationTurn>? history = null,
        CancellationToken ct = default);
}