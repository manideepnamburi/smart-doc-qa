using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

public interface IGraphRetriever
{
    Task<List<RetrievedChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default);
}