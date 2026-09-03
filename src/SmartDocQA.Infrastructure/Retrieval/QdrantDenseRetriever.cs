using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Qdrant Dense Retriever ───────────────────────────────────────────────────
// Thin adapter: embeds the query text, then delegates the actual vector
// search to IVectorStore (QdrantVectorStoreAdapter). Kept separate from
// the vector store itself so IDenseRetriever's contract stays symmetric
// with ISparseRetriever/IGraphRetriever -- "take a query string, get
// back ranked chunks" -- regardless of what embedding/storage happens
// underneath.

public class QdrantDenseRetriever : IDenseRetriever
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;

    public QdrantDenseRetriever(IVectorStore vectorStore, IEmbeddingService embeddingService)
    {
        _vectorStore      = vectorStore;
        _embeddingService = embeddingService;
    }

    public async Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, string? documentIdFilter = null,
        CancellationToken ct = default)
    {
        var queryVector = await _embeddingService.EmbedAsync(query, ct);
        return await _vectorStore.SearchAsync(queryVector, topK, documentIdFilter, ct);
    }
}