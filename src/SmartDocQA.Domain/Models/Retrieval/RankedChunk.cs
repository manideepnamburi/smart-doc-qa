namespace SmartDocQA.Domain.Models;

/// <summary>
/// The final, reranked form of a chunk -- what IRetrievalPipeline actually
/// returns to callers (QueryDocumentUseCase, AgentQueryUseCase) as
/// synthesis-ready evidence. RerankerScore reflects the LLM reranker's
/// judgment of relevance to the query (or, when reranking is disabled/
/// unavailable, falls back to the fusion score -- see RerankerReason,
/// e.g. "fusion-only" or "rrf-fallback", for which path produced it).
/// </summary>
public record RankedChunk(
    DocumentChunk Chunk,
    float RerankerScore,
    string RerankerReason
);
