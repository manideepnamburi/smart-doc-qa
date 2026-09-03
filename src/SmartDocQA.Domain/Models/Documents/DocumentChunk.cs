using SmartDocQA.Domain.Enums;

namespace SmartDocQA.Domain.Models;

public record DocumentChunk(
    string ChunkId,
    string DocumentId,
    string FileName,
    string Content,
    ChunkType ChunkType,
    int PageNumber,
    int ChunkIndex,
    Dictionary<string, string> Metadata  // extensible — add whatever at ingestion
)
{
    public float[]? Embedding { get; init; }
}