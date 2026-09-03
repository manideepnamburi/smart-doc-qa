using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Encapsulates the full "retrieve and rank" strategy: given a query,
/// returns a ranked, ready-to-synthesize set of chunks. This is the
/// abstraction that lets the retrieval STRATEGY be swapped independently
/// of the use cases that consume it (QueryDocumentUseCase,
/// AgentQueryUseCase, and any future caller).
///
/// WHY THIS EXISTS (Phase 7.5 extraction):
/// Before this interface, "dense + sparse + graph retrieve -> RRF fuse ->
/// rerank" was written inline inside QueryDocumentUseCase.ExecuteAsync.
/// That worked fine when there was only one caller, but the moment a
/// second caller (the new agentic query pipeline) needed the exact same
/// retrieval behavior, inline logic meant either (a) duplicating ~30
/// lines of orchestration code in two places, guaranteed to drift apart
/// over time, or (b) this interface -- one implementation, every caller
/// depends on the abstraction, not the concrete class.
///
/// This is deliberately a SEPARATE, higher-level abstraction from the
/// existing IDenseRetriever / ISparseRetriever / IFusionStrategy /
/// IReranker interfaces -- those let you swap ONE algorithm at a
/// time (e.g. replace BM25 with a different sparse retriever) while
/// keeping the same overall orchestration. IRetrievalPipeline lets you
/// swap the ENTIRE orchestration strategy itself (e.g. skip fusion
/// entirely and query the graph only, or introduce a completely
/// different ranking approach) without any of its callers knowing or
/// caring -- they only ever see "give me ranked chunks for this query."
/// </summary>
public interface IRetrievalPipeline
{
    /// <summary>
    /// Runs the configured retrieval strategy end-to-end and returns
    /// ranked chunks ready for answer synthesis.
    /// </summary>
    /// <param name="request">
    /// The query and retrieval options (document scope filter, whether
    /// graph retrieval and reranking are enabled for this call).
    /// </param>
    /// <param name="ct">Cancellation token, propagated to every retrieval
    /// call made internally (dense, sparse, graph, and reranking).</param>
    Task<RetrievalPipelineResult> RetrieveAndRankAsync(
        RetrievalRequest request,
        CancellationToken ct = default);
}
