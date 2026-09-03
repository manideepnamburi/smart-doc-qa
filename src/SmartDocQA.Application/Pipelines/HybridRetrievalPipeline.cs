using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Application.Pipelines;

/// <summary>
/// The default, current implementation of IRetrievalPipeline: dense +
/// sparse + (optional) graph retrieval in parallel, fused with RRF, then
/// optionally reranked by an LLM-based reranker.
///
/// WHERE THIS CODE CAME FROM (Phase 7.5 extraction):
/// This is a FAITHFUL, UNCHANGED COPY of what used to be steps 2-4,
/// written inline inside QueryDocumentUseCase.ExecuteAsync. Nothing about
/// the actual retrieval/fusion/rerank BEHAVIOR changed during this move —
/// only WHERE the code lives. QueryDocumentUseCase now calls this class
/// through the IRetrievalPipeline interface instead of running these
/// steps inline, and the new AgentQueryUseCase (Phase 7.5) can call the
/// exact same logic per sub-question without duplicating a single line
/// of it.
///
/// If a future phase wants a genuinely different retrieval strategy (e.g.
/// graph-only, or a different fusion algorithm), that becomes a NEW class
/// implementing IRetrievalPipeline, registered in place of this one in
/// Program.cs — neither QueryDocumentUseCase nor AgentQueryUseCase would
/// need to change at all.
/// </summary>
public class HybridRetrievalPipeline : IRetrievalPipeline
{
    private readonly IDenseRetriever _denseRetriever;
    private readonly ISparseRetriever _sparseRetriever;
    private readonly IGraphRetriever? _graphRetriever;
    private readonly IFusionStrategy _fusionStrategy;
    private readonly IReranker? _reranker;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<HybridRetrievalPipeline> _logger;

    public HybridRetrievalPipeline(
        IDenseRetriever denseRetriever,
        ISparseRetriever sparseRetriever,
        IFusionStrategy fusionStrategy,
        IOptions<RagOptions> ragOptions,
        ILogger<HybridRetrievalPipeline> logger,
        IGraphRetriever? graphRetriever = null,
        IReranker? reranker = null)
    {
        _denseRetriever = denseRetriever;
        _sparseRetriever = sparseRetriever;
        _graphRetriever = graphRetriever;
        _fusionStrategy = fusionStrategy;
        _reranker = reranker;
        _ragOptions = ragOptions.Value;
        _logger = logger;
    }

    public async Task<RetrievalPipelineResult> RetrieveAndRankAsync(
        RetrievalRequest request,
        CancellationToken ct = default)
    {
        // ── Retrieve from all sources in parallel ──────────────────────────
        // Identical to the old QueryDocumentUseCase Step 2: dense and
        // sparse always run; graph only runs if the caller asked for it
        // AND graph retrieval is enabled in config AND a graph retriever
        // is actually registered (it's optional -- Neo4j may not be
        // configured in every environment).
        var denseTask = _denseRetriever.RetrieveAsync(
            request.Query, _ragOptions.TopKDense, request.DocumentIdFilter, ct);

        var sparseTask = _sparseRetriever.RetrieveAsync(
            request.Query, _ragOptions.TopKSparse, request.DocumentIdFilter, ct);

        Task<List<RetrievedChunk>>? graphTask = null;
        if (request.UseGraph && _ragOptions.UseGraphRetrieval && _graphRetriever is not null)
            graphTask = _graphRetriever.RetrieveAsync(request.Query, _ragOptions.TopKDense, ct);

        await Task.WhenAll(
            denseTask,
            sparseTask,
            graphTask ?? Task.FromResult(new List<RetrievedChunk>()));

        var denseResults = await denseTask;
        var sparseResults = await sparseTask;
        var graphResults = graphTask is not null ? await graphTask : null;

        _logger.LogDebug(
            "Retrieved: Dense={D} Sparse={S} Graph={G}",
            denseResults.Count, sparseResults.Count, graphResults?.Count ?? 0);

        // ── Fuse with RRF ───────────────────────────────────────────────────
        // Identical to the old Step 3.
        var fused = _fusionStrategy.Fuse(
            denseResults, sparseResults, graphResults, _ragOptions.TopKAfterFusion);

        _logger.LogDebug("After RRF fusion: {Count} chunks", fused.Count);

        // ── Rerank (optional, config-driven) ────────────────────────────────
        // Identical to the old Step 4.
        List<RankedChunk> ranked;

        if (request.UseReranking && _ragOptions.UseReranking && _reranker is not null)
        {
            ranked = await _reranker.RerankAsync(
                request.Query, fused, _ragOptions.TopKAfterRerank, ct);
            _logger.LogDebug("After reranking: {Count} chunks", ranked.Count);
        }
        else
        {
            // No reranker — convert fused results directly, same fallback
            // behavior as before.
            ranked = fused
                .Take(_ragOptions.TopKAfterRerank)
                .Select(f => new RankedChunk(f.Chunk, f.FusedScore, "fusion-only"))
                .ToList();
        }

        // ── Determine retrieval mode for result metadata ────────────────────
        // Identical to the old Step 5.
        var mode = (request.UseGraph && graphResults?.Count > 0)
            ? RetrievalMode.HybridWithGraph
            : RetrievalMode.Hybrid;

        _logger.LogDebug(
            "Retrieval mode: {Mode} | Ranked chunks: {Count}",
            mode, ranked.Count);

        return new RetrievalPipelineResult(
            RankedChunks: ranked,
            Mode: mode,
            ChunksRetrieved: denseResults.Count + sparseResults.Count + (graphResults?.Count ?? 0),
            ChunksAfterRerank: ranked.Count);
    }
}
