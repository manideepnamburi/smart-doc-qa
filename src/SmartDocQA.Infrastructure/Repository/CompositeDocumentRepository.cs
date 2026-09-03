using Microsoft.Extensions.Logging;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Composite Document Repository ───────────────────────────────────────────
// Coordinates writes across the vector store (Qdrant) and keyword index
// (BM25) as one unit of work -- implements IDocumentRepository, the
// Repository-pattern abstraction the rest of the app depends on rather
// than knowing about either storage backend individually.

public class CompositeDocumentRepository : IDocumentRepository
{
    private readonly IVectorStore _vectorStore;
    private readonly IKeywordIndex _keywordIndex;
    private readonly ILogger<CompositeDocumentRepository> _logger;

    public CompositeDocumentRepository(
        IVectorStore vectorStore, IKeywordIndex keywordIndex,
        ILogger<CompositeDocumentRepository> logger)
    {
        _vectorStore  = vectorStore;
        _keywordIndex = keywordIndex;
        _logger       = logger;
    }

    public async Task SaveAsync(
        List<DocumentChunk> chunks, DocumentMetadata metadata,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Saving {Count} chunks for {DocumentId}",
            chunks.Count, metadata.DocumentId);

        await Task.WhenAll(
            _vectorStore.UpsertBatchAsync(chunks, ct),
            _keywordIndex.IndexBatchAsync(chunks, ct));
    }

    public async Task DeleteAsync(string documentId, CancellationToken ct = default)
    {
        await Task.WhenAll(
            _vectorStore.DeleteByDocumentAsync(documentId, ct),
            _keywordIndex.DeleteByDocumentAsync(documentId, ct));
    }

    public Task<List<DocumentMetadata>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(new List<DocumentMetadata>());
}