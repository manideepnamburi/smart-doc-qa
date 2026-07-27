using Microsoft.Extensions.VectorData;

namespace SmartDocQA.Infrastructure.VectorStore;

/// <summary>
/// Represents how a DocumentChunk is stored in Qdrant.
/// Dimensions must match your embedding provider:
///   Azure text-embedding-ada-002 = 1536
///   Ollama all-minilm            = 384
/// </summary>
public class QdrantChunkRecord
{
    [VectorStoreKey]
    public ulong Id { get; set; }

    // IsIndexed = true (renamed from IsFilterable in April 2025 SK update)
    [VectorStoreData(IsIndexed = true)]
    public string ChunkId { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string DocumentId { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string FileName { get; set; } = string.Empty;

    [VectorStoreData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreData]
    public string ChunkType { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public int PageNumber { get; set; }

    [VectorStoreData]
    public int ChunkIndex { get; set; }

    [VectorStoreData]
    public string SourceType { get; set; } = string.Empty;

    [VectorStoreData]
    public string IngestedAt { get; set; } = string.Empty;

    // Dimensions: is required (named parameter), DistanceFunction is a property
    [VectorStoreVector(Dimensions: 1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
