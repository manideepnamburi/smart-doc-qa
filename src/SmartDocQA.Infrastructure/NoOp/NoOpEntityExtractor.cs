using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

/// <summary>No-op stand-in for IEntityExtractor, used when graph/entity extraction is disabled.</summary>
public class NoOpEntityExtractor : IEntityExtractor
{
    public Task<(List<GraphEntity> Entities, List<GraphRelationship> Relationships)> ExtractAsync(
        DocumentChunk chunk, CancellationToken ct = default)
        => Task.FromResult((new List<GraphEntity>(), new List<GraphRelationship>()));
}
