using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Neo4j Graph Retriever ────────────────────────────────────────────────────
//
// WHY THIS CLASS EXISTS SEPARATELY FROM Neo4jGraphStore:
// IGraphStore is the full read/write contract (upsert, delete, search) used
// during INGESTION. IGraphRetriever is a narrower, read-only contract
// (just RetrieveAsync) used during QUERY time, matching the shape of
// IDenseRetriever and ISparseRetriever so RRFFusionStrategy can treat all
// three retrieval sources identically without knowing which database is
// behind each one. This class is a thin adapter — it does no real work
// itself, it just forwards to the graph store's search method, translating
// between the two interface shapes so the query pipeline stays symmetric
// with its dense/sparse counterparts.
public class Neo4jGraphRetriever : IGraphRetriever
{
    private readonly IGraphStore _graphStore;
    public Neo4jGraphRetriever(IGraphStore graphStore) => _graphStore = graphStore;

    public Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, CancellationToken ct = default)
        => _graphStore.SearchByEntityAsync(query, topK, ct);
}
