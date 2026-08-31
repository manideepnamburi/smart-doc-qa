using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Splits parsed content into DocumentChunks ready for embedding.
/// Strategy is selected from config (FixedSize or Semantic).
/// </summary>
public interface IChunkingStrategy
{
    Task<List<DocumentChunk>> ChunkAsync(ParsedDocument document, string documentId, CancellationToken ct = default);
}