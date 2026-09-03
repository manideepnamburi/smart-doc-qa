namespace SmartDocQA.Domain.Models;

/// <summary>
/// Input to IRetrievalPipeline.RetrieveAndRankAsync — the query plus the
/// per-call options that control which retrieval sources and reranking
/// step run for this specific request. Mirrors the flags QAQuery already
/// exposes (DocumentIdFilter, UseGraph, UseReranking) so both
/// QueryDocumentUseCase and AgentQueryUseCase can build one of these
/// directly from their own query objects.
/// </summary>
public record RetrievalRequest(
    string Query,
    string? DocumentIdFilter,
    bool UseGraph,
    bool UseReranking);