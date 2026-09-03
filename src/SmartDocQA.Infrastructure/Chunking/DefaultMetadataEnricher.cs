using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Default Metadata Enricher ────────────────────────────────────────────────
// Runs right after chunking, adding ingestion-time metadata (source type,
// timestamp, file name) to every chunk before it's persisted.

public class DefaultMetadataEnricher : IMetadataEnricher
{
    public Task<List<DocumentChunk>> EnrichAsync(
        List<DocumentChunk> chunks, DocumentSource source, CancellationToken ct = default)
    {
        var enriched = chunks.Select(c =>
        {
            var metadata = new Dictionary<string, string>(c.Metadata)
            {
                ["source_type"] = source.SourceType.ToString(),
                ["ingested_at"] = DateTime.UtcNow.ToString("O"),
                ["file_name"]   = c.FileName
            };
            return c with { Metadata = metadata };
        }).ToList();
        return Task.FromResult(enriched);
    }
}