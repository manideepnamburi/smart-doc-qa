using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Vector store — Qdrant today, swappable tomorrow.
/// SK's IVectorStore abstraction sits under this.
/// </summary>
public interface IVectorStore
{
    Task UpsertAsync(DocumentChunk chunk, CancellationToken ct = default);

    Task UpsertBatchAsync(List<DocumentChunk> chunks, CancellationToken ct = default);

    Task<List<RetrievedChunk>> SearchAsync(float[] queryVector, int topK,
        string? documentIdFilter = null, CancellationToken ct = default);

    Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Delete the entire collection and recreate it empty.
    /// Used during full system reset — wipes ALL vectors.
    /// </summary>
    Task ResetCollectionAsync(CancellationToken ct = default);
}