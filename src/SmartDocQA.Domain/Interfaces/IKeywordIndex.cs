using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// BM25 keyword index — SemanticKernel.Rankers.BM25 today, Lucene tomorrow.
/// </summary>
public interface IKeywordIndex
{
    Task IndexAsync(DocumentChunk chunk, CancellationToken ct = default);
    Task IndexBatchAsync(List<DocumentChunk> chunks, CancellationToken ct = default);
    Task<List<RetrievedChunk>> SearchAsync(string query, int topK, string? documentIdFilter = null, CancellationToken ct = default);
    Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);    
}