using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

public interface IDenseRetriever
{
    Task<List<RetrievedChunk>> RetrieveAsync(string query, int topK, string? documentIdFilter = null, CancellationToken ct = default);
}