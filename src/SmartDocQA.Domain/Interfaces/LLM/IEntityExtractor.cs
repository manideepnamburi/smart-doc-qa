using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Extracts named entities and relationships from chunk text for the Graph store.
/// </summary>
public interface IEntityExtractor
{
    Task<(List<GraphEntity> Entities, List<GraphRelationship> Relationships)> ExtractAsync(
        DocumentChunk chunk, CancellationToken ct = default);
}