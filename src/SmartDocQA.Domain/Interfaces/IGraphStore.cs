using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Graph store — Neo4j today, ArangoDB tomorrow.
/// </summary>
public interface IGraphStore
{
    Task UpsertEntityAsync(GraphEntity entity, CancellationToken ct = default);
    Task UpsertRelationshipAsync(GraphRelationship relationship, CancellationToken ct = default);
    Task<List<RetrievedChunk>> SearchByEntityAsync(string query, int topK, CancellationToken ct = default);
    Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default);
}