using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

/// <summary>No-op stand-in for ISparseRetriever, used when sparse retrieval is disabled.</summary>
public class NoOpSparseRetriever : ISparseRetriever
{
    public Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, string? documentIdFilter = null,
        CancellationToken ct = default)
        => Task.FromResult(new List<RetrievedChunk>());
}
