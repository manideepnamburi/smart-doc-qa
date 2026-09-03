using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

/// <summary>Passthrough stand-in for IQueryRewriter, used when query rewriting is disabled entirely.</summary>
public class PassthroughQueryRewriter : IQueryRewriter
{
    public Task<string> RewriteAsync(
        string originalQuery,
        List<ConversationTurn>? history = null,
        CancellationToken ct = default)
        => Task.FromResult(originalQuery); // still ignores history -- rewriting is disabled entirely
}