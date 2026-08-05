using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Application.UseCases;

/// <summary>
/// Orchestrates the full query pipeline:
/// [Input Guardrails] → Rewrite → Dense + Sparse + Graph Retrieve → RRF Fusion
/// → Rerank → Synthesize → [Output Guardrails]
///
/// FallbackToLLM flag flows through the entire pipeline untouched.
/// The AnswerSynthesizer is responsible for deciding what to do when
/// no relevant chunks are found based on that flag.
///
/// Guardrails (Phase 6.5): input guardrails run FIRST, before any
/// retrieval/synthesis cost is incurred -- a rejected question short-
/// circuits immediately. Output guardrails run LAST, after synthesis, as a
/// final safety check before the result reaches the caller. Both are
/// IEnumerable collections resolved by DI -- if zero guardrails are
/// registered (or all are disabled via config), these loops are a no-op
/// and the pipeline behaves exactly as it did before Phase 6.5.
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
    private readonly IEnumerable<IInputGuardrail> _inputGuardrails;
    private readonly IEnumerable<IOutputGuardrail> _outputGuardrails;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<QueryDocumentUseCase> _logger;

    public QueryDocumentUseCase(
        IQueryRewriter queryRewriter,
        IDenseRetriever denseRetriever,
        ISparseRetriever sparseRetriever,
        IFusionStrategy fusionStrategy,
        IAnswerSynthesizer answerSynthesizer,
        IEnumerable<IInputGuardrail> inputGuardrails,
        IEnumerable<IOutputGuardrail> outputGuardrails,
        IOptions<RagOptions> ragOptions,
        ILogger<QueryDocumentUseCase> logger,
        IGraphRetriever? graphRetriever = null,
        IReranker? reranker = null)
    {
        _queryRewriter = queryRewriter;
        _denseRetriever = denseRetriever;
        _sparseRetriever = sparseRetriever;
        _graphRetriever = graphRetriever;
        _fusionStrategy = fusionStrategy;
        _reranker = reranker;
        _answerSynthesizer = answerSynthesizer;
        _inputGuardrails = inputGuardrails;
        _outputGuardrails = outputGuardrails;
        _ragOptions = ragOptions.Value;
        _logger = logger;
    }

    public async Task<QAResult> ExecuteAsync(QAQuery query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation(
            "Processing query: '{Question}' | FallbackToLLM={Fallback}",
            query.Question, query.FallbackToLLM);

        // ── Step 0: Input guardrails (Phase 6.5) ──────────────────────────────
        // Runs BEFORE any retrieval/synthesis cost. Each registered guardrail
        // gets a chance to reject the question; the first rejection wins and
        // short-circuits the entire pipeline -- no embedding call, no
        // retrieval, no Claude synthesis call happens for a rejected question.
        foreach (var guardrail in _inputGuardrails)
        {
            var checkResult = await guardrail.CheckAsync(query.Question, ct);
            if (!checkResult.Passed)
            {
                sw.Stop();
                _logger.LogWarning(
                    "Query rejected by input guardrail {Guardrail}: {Reason}",
                    guardrail.GetType().Name, checkResult.Reason);

                // Reuses existing AnswerSource.NotFound / ConfidenceLevel.NotFound
                // rather than adding a new enum value -- avoids rippling into
                // the eval harness's separately-maintained copy of AnswerSource.
                return new QAResult(
                    Answer: $"Your question could not be processed: {checkResult.Reason}",
                    Citations: new List<Citation>(),
                    Confidence: ConfidenceLevel.NotFound,
                    RetrievalMode: RetrievalMode.DenseOnly, // no retrieval was attempted
                    AnswerSource: AnswerSource.NotFound,
                    ChunksRetrieved: 0,
                    ChunksAfterRerank: 0,
                    ProcessingTime: sw.Elapsed);
            }
        }

        // ── Step 1: Rewrite query for better retrieval ────────────────────────
        var rewrittenQuery = _ragOptions.UseQueryRewriting
            ? await _queryRewriter.RewriteAsync(query.Question, ct)
            : query.Question;

        _logger.LogDebug("Rewritten query: '{Rewritten}'", rewrittenQuery);

        // ── Step 2: Parallel retrieval from all sources ───────────────────────
        var denseTask = _denseRetriever.RetrieveAsync(
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

        var denseResults = await denseTask;
        var sparseResults = await sparseTask;
        var graphResults = graphTask is not null ? await graphTask : null;

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

        // ── Step 7: Output guardrails (Phase 6.5) ─────────────────────────────
        // Last line of defense before the answer reaches the caller. If ANY
        // registered output guardrail fails, we override the result with a
        // safe fallback rather than returning a potentially-unsafe answer --
        // the whole point of an output guardrail is to actually BLOCK a bad
        // answer, not just log that it happened.
        foreach (var guardrail in _outputGuardrails)
        {
            var checkResult = await guardrail.CheckAsync(result, ct);
            if (!checkResult.Passed)
            {
                _logger.LogWarning(
                    "Answer flagged by output guardrail {Guardrail}: {Reason}",
                    guardrail.GetType().Name, checkResult.Reason);

                result = result with
                {
                    Answer = "I'm unable to verify this answer is properly grounded in the source documents. " +
                             "Please try rephrasing your question.",
                    Citations = new List<Citation>(),
                    AnswerSource = AnswerSource.NotFound,
                    Confidence = ConfidenceLevel.NotFound
                };
                break; // one override is enough; no need to keep checking further guardrails
            }
        }

        sw.Stop();

        _logger.LogInformation(
            "Query complete | Mode={Mode} | Chunks={C} | Source={Source} | Time={T}ms",
            mode, ranked.Count, result.AnswerSource, sw.ElapsedMilliseconds);

        return result with { ProcessingTime = sw.Elapsed };
    }
}