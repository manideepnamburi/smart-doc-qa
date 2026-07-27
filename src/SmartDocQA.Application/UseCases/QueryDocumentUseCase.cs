using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Application.UseCases;

/// <summary>
/// Orchestrates the full query pipeline:
/// Rewrite → Dense + Sparse + Graph Retrieve → RRF Fusion → Rerank → Synthesize
///
/// FallbackToLLM flag flows through the entire pipeline untouched.
/// The AnswerSynthesizer is responsible for deciding what to do when
/// no relevant chunks are found based on that flag.
/// </summary>
public class QueryDocumentUseCase
{
    private readonly IQueryRewriter _queryRewriter;
    private readonly IDenseRetriever _denseRetriever;
    private readonly ISparseRetriever _sparseRetriever;
    private readonly IGraphRetriever? _graphRetriever;
    private readonly IFusionStrategy _fusionStrategy;
    private readonly IReranker? _reranker;
    private readonly IAnswerSynthesizer _answerSynthesizer;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<QueryDocumentUseCase> _logger;

    public QueryDocumentUseCase(
        IQueryRewriter queryRewriter,
        IDenseRetriever denseRetriever,
        ISparseRetriever sparseRetriever,
        IFusionStrategy fusionStrategy,
        IAnswerSynthesizer answerSynthesizer,
        IOptions<RagOptions> ragOptions,
        ILogger<QueryDocumentUseCase> logger,
        IGraphRetriever? graphRetriever = null,
        IReranker? reranker = null)
    {
        _queryRewriter     = queryRewriter;
        _denseRetriever    = denseRetriever;
        _sparseRetriever   = sparseRetriever;
        _graphRetriever    = graphRetriever;
        _fusionStrategy    = fusionStrategy;
        _reranker          = reranker;
        _answerSynthesizer = answerSynthesizer;
        _ragOptions        = ragOptions.Value;
        _logger            = logger;
    }

    public async Task<QAResult> ExecuteAsync(QAQuery query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation(
            "Processing query: '{Question}' | FallbackToLLM={Fallback}",
            query.Question, query.FallbackToLLM);

        // ── Step 1: Rewrite query for better retrieval ────────────────────────
        var rewrittenQuery = _ragOptions.UseQueryRewriting
            ? await _queryRewriter.RewriteAsync(query.Question, ct)
            : query.Question;

        _logger.LogDebug("Rewritten query: '{Rewritten}'", rewrittenQuery);

        // ── Step 2: Parallel retrieval from all sources ───────────────────────
        var denseTask  = _denseRetriever.RetrieveAsync(
            rewrittenQuery, _ragOptions.TopKDense, query.DocumentIdFilter, ct);

        var sparseTask = _sparseRetriever.RetrieveAsync(
            rewrittenQuery, _ragOptions.TopKSparse, query.DocumentIdFilter, ct);

        Task<List<RetrievedChunk>>? graphTask = null;
        if (query.UseGraph && _ragOptions.UseGraphRetrieval && _graphRetriever is not null)
            graphTask = _graphRetriever.RetrieveAsync(rewrittenQuery, _ragOptions.TopKDense, ct);

        await Task.WhenAll(
            denseTask,
            sparseTask,
            graphTask ?? Task.FromResult(new List<RetrievedChunk>()));

        var denseResults  = await denseTask;
        var sparseResults = await sparseTask;
        var graphResults  = graphTask is not null ? await graphTask : null;

        _logger.LogDebug(
            "Retrieved: Dense={D} Sparse={S} Graph={G}",
            denseResults.Count, sparseResults.Count, graphResults?.Count ?? 0);

        // ── Step 3: Fuse with RRF ─────────────────────────────────────────────
        var fused = _fusionStrategy.Fuse(
            denseResults, sparseResults, graphResults, _ragOptions.TopKAfterFusion);

        _logger.LogDebug("After RRF fusion: {Count} chunks", fused.Count);

        // ── Step 4: Rerank (optional, config-driven) ──────────────────────────
        List<RankedChunk> ranked;

        if (query.UseReranking && _ragOptions.UseReranking && _reranker is not null)
        {
            ranked = await _reranker.RerankAsync(
                rewrittenQuery, fused, _ragOptions.TopKAfterRerank, ct);
            _logger.LogDebug("After reranking: {Count} chunks", ranked.Count);
        }
        else
        {
            // No reranker — convert fused results directly
            ranked = fused
                .Take(_ragOptions.TopKAfterRerank)
                .Select(f => new RankedChunk(f.Chunk, f.FusedScore, "fusion-only"))
                .ToList();
        }

        // ── Step 5: Determine retrieval mode for result metadata ──────────────
        var mode = (query.UseGraph && graphResults?.Count > 0)
            ? RetrievalMode.HybridWithGraph
            : RetrievalMode.Hybrid;

        _logger.LogDebug(
            "Retrieval mode: {Mode} | Ranked chunks: {Count} | FallbackToLLM: {Fallback}",
            mode, ranked.Count, query.FallbackToLLM);

        // ── Step 6: Synthesize answer ─────────────────────────────────────────
        // query.FallbackToLLM flows into the synthesizer automatically.
        // If ranked.Count == 0 and FallbackToLLM == true  → Claude answers from knowledge.
        // If ranked.Count == 0 and FallbackToLLM == false → "not found" response.
        // If ranked.Count  > 0                            → answer from document chunks.
        var result = await _answerSynthesizer.SynthesizeAsync(query, ranked, ct);

        sw.Stop();

        _logger.LogInformation(
            "Query complete | Mode={Mode} | Chunks={C} | Source={Source} | Time={T}ms",
            mode, ranked.Count, result.AnswerSource, sw.ElapsedMilliseconds);

        return result with { ProcessingTime = sw.Elapsed };
    }
}
