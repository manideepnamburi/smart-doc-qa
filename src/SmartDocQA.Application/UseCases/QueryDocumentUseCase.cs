using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Application.UseCases;

/// <summary>
/// Orchestrates the full query pipeline:
/// [Input Guardrails] → Rewrite → IRetrievalPipeline (retrieve+fuse+rerank)
/// → Synthesize → [Output Guardrails]
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
///
/// RETRIEVAL PIPELINE EXTRACTION (Phase 7.5):
/// What used to be four inline steps here (parallel dense+sparse+graph
/// retrieve, RRF fusion, optional reranking, and retrieval-mode
/// determination) now lives behind the IRetrievalPipeline abstraction,
/// implemented today by HybridRetrievalPipeline. This class no longer
/// knows or cares HOW retrieval happens -- it only knows "give me a
/// query, get back ranked chunks." This was extracted specifically so
/// the new agentic query pipeline (AgentQueryUseCase) can reuse the exact
/// same retrieval behavior, once per sub-question, without duplicating
/// this logic. See IRetrievalPipeline's XML doc comment for the full
/// design rationale.
/// </summary>
public class QueryDocumentUseCase
{
    private readonly IQueryRewriter _queryRewriter;
    private readonly IRetrievalPipeline _retrievalPipeline;
    private readonly IAnswerSynthesizer _answerSynthesizer;
    private readonly IEnumerable<IInputGuardrail> _inputGuardrails;
    private readonly IEnumerable<IOutputGuardrail> _outputGuardrails;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<QueryDocumentUseCase> _logger;

    public QueryDocumentUseCase(
        IQueryRewriter queryRewriter,
        IRetrievalPipeline retrievalPipeline,
        IAnswerSynthesizer answerSynthesizer,
        IEnumerable<IInputGuardrail> inputGuardrails,
        IEnumerable<IOutputGuardrail> outputGuardrails,
        IOptions<RagOptions> ragOptions,
        ILogger<QueryDocumentUseCase> logger)
    {
        _queryRewriter = queryRewriter;
        _retrievalPipeline = retrievalPipeline;
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
            var checkResult = await guardrail.CheckAsync(query.Question, query.FallbackToLLM, ct);
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
           ? await _queryRewriter.RewriteAsync(query.Question, query.History, ct)
           : query.Question;

        _logger.LogDebug("Rewritten query: '{Rewritten}'", rewrittenQuery);

        // ── Step 2: Retrieve + fuse + rerank via the shared pipeline ───────────
        // This single call replaces what used to be four separate inline
        // steps (parallel retrieve, RRF fuse, rerank, mode determination).
        // See IRetrievalPipeline / HybridRetrievalPipeline for exactly what
        // happens inside this call -- the behavior is unchanged from
        // before this refactor, only its location moved.
        var retrievalRequest = new RetrievalRequest(
            Query: rewrittenQuery,
            DocumentIdFilter: query.DocumentIdFilter,
            UseGraph: query.UseGraph,
            UseReranking: query.UseReranking);

        var retrievalResult = await _retrievalPipeline.RetrieveAndRankAsync(retrievalRequest, ct);

        _logger.LogDebug(
            "Retrieval mode: {Mode} | Ranked chunks: {Count} | FallbackToLLM: {Fallback}",
            retrievalResult.Mode, retrievalResult.RankedChunks.Count, query.FallbackToLLM);

        // ── Step 3: Synthesize answer ─────────────────────────────────────────
        // query.FallbackToLLM flows into the synthesizer automatically.
        // If RankedChunks.Count == 0 and FallbackToLLM == true  → Claude answers from knowledge.
        // If RankedChunks.Count == 0 and FallbackToLLM == false → "not found" response.
        // If RankedChunks.Count  > 0                            → answer from document chunks.
        var result = await _answerSynthesizer.SynthesizeAsync(query, retrievalResult.RankedChunks, ct);

        // ── Step 4: Output guardrails (Phase 6.5) ─────────────────────────────
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
            retrievalResult.Mode, retrievalResult.RankedChunks.Count, result.AnswerSource, sw.ElapsedMilliseconds);

        return result with { ProcessingTime = sw.Elapsed };
    }
}