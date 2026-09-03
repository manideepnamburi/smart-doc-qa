namespace SmartDocQA.Domain.Models;

/// <summary>
/// A chunk after Reciprocal Rank Fusion (RRF) has combined its scores
/// across dense, sparse, and (optionally) graph retrieval. The individual
/// per-source scores are preserved alongside the combined FusedScore so
/// callers can inspect or log which retriever(s) actually surfaced this
/// chunk -- a chunk found by only one source will have null for the
/// others.
/// </summary>
public record FusedChunk(
    DocumentChunk Chunk,
    float FusedScore,
    float? DenseScore,
    float? SparseScore,
    float? GraphScore
);