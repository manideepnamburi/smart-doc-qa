using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Adds metadata to chunks after chunking — timestamps, source type, permissions, etc.
/// </summary>
public interface IMetadataEnricher
{
    Task<List<DocumentChunk>> EnrichAsync(List<DocumentChunk> chunks, DocumentSource source, CancellationToken ct = default);
}
