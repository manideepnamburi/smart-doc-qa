namespace SmartDocQA.Domain.Models;

/// <summary>
/// A single chunk as it comes back from ONE retriever (dense, sparse, or
/// graph) before fusion combines results across all three. RetrievalSource
/// identifies which retriever produced it -- "dense", "sparse", or "graph"
/// -- so downstream fusion/logging can tell them apart.
/// </summary>
public record RetrievedChunk(
    DocumentChunk Chunk,
    float Score,
    string RetrievalSource   // "dense", "sparse", "graph"
);