using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

/// <summary>No-op stand-in for IGraphStore, used when Neo4j/graph features are disabled.</summary>
public class NoOpGraphStore : IGraphStore
{
    public Task UpsertEntityAsync(GraphEntity entity, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task UpsertRelationshipAsync(GraphRelationship rel, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<List<RetrievedChunk>> SearchByEntityAsync(string query, int topK, CancellationToken ct = default)
        => Task.FromResult(new List<RetrievedChunk>());
    public Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default)
        => Task.CompletedTask;
}
