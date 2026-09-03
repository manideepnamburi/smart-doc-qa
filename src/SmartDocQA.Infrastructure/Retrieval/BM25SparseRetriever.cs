using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── BM25 Sparse Retriever ────────────────────────────────────────────────────
// Thin adapter over IKeywordIndex, matching the IDenseRetriever/
// IGraphRetriever shape so RRFFusionStrategy can treat all three
// retrieval sources identically. Real BM25 implementation lives in
// Retrieval/BM25KeywordIndex.cs (Phase 2).

public class BM25SparseRetriever : ISparseRetriever
{
    private readonly IKeywordIndex _index;
    public BM25SparseRetriever(IKeywordIndex index) => _index = index;

    public Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, string? documentIdFilter = null,
        CancellationToken ct = default)
        => _index.SearchAsync(query, topK, documentIdFilter, ct);
}