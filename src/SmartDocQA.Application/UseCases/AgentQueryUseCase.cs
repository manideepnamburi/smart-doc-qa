using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Application.UseCases;

/// <summary>
/// Orchestrates the agentic query pipeline (Phase 7.5):
/// [Triage (optional)] → Decompose → per sub-question, CONCURRENTLY:
/// [Retrieve → Verify → Reformulate+Retry] → Synthesize (with
/// per-sub-question attribution)
///
/// This is deliberately a SEPARATE use case from QueryDocumentUseCase,
/// not a mode flag on it -- the two pipelines have genuinely different
/// shapes (one query in/one answer out, vs. one query decomposed into N
/// sub-questions each independently retrieved/verified/retried). Keeping
/// them separate means QueryDocumentUseCase's proven, simple, cheap path
/// is never put at risk by the agent pipeline's added complexity, and
/// each can evolve independently.
///
/// Reuses QAQuery as its input (rather than a new request type) since
/// every field it needs -- Question, DocumentIdFilter, UseGraph,
/// UseReranking -- already exists there, and reusing it keeps the
/// request shape consistent between the two endpoints.
///
/// CONCURRENCY (added after initial build-out): each sub-question's
/// retrieve->verify->retry loop is fully independent of every other
/// sub-question's loop -- nothing about processing sub-question 1
/// depends on sub-question 2's outcome. The first version ran these
/// sequentially, one at a time, which meant total latency scaled
/// linearly with sub-question count (a 3-sub-question compound question
/// took ~36s in testing). This version runs them concurrently, bounded
/// by a SemaphoreSlim, using the exact same pattern as
/// ClaudeVisionChartExtractor's page-analysis concurrency fix --
/// AgentModeOptions.MaxConcurrentSubQuestions caps how many are in
/// flight at once, tunable against the real Anthropic rate limit without
/// touching code.
/// </summary>
public class AgentQueryUseCase
{
    private readonly IQueryDecomposer _decomposer;
    private readonly IRetrievalPipeline _retrievalPipeline;
    private readonly IAnswerVerifier _verifier;
    private readonly IQueryReformulator _reformulator;
    private readonly IAgentAnswerSynthesizer _agentSynthesizer;
    private readonly AgentModeOptions _agentOptions;
    private readonly ILogger<AgentQueryUseCase> _logger;

    public AgentQueryUseCase(
        IQueryDecomposer decomposer,
        IRetrievalPipeline retrievalPipeline,
        IAnswerVerifier verifier,
        IQueryReformulator reformulator,
        IAgentAnswerSynthesizer agentSynthesizer,
        IOptions<AgentModeOptions> agentOptions,
        ILogger<AgentQueryUseCase> logger)
    {
        _decomposer = decomposer;
        _retrievalPipeline = retrievalPipeline;
        _verifier = verifier;
        _reformulator = reformulator;
        _agentSynthesizer = agentSynthesizer;
        _agentOptions = agentOptions.Value;
        _logger = logger;
    }

    public async Task<AgentQueryResponse> ExecuteAsync(QAQuery query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation(
            "Processing agent query: '{Question}' | TriageBeforeDecomposition={Triage}",
            query.Question, _agentOptions.TriageBeforeDecomposition);

        // ── Step 1: Decide the sub-question list ────────────────────────────
        // Two strategies exist side by side, picked by config rather than
        // one replacing the other -- lets the actual cost/latency difference
        // between them be measured later instead of assumed.
        List<string> subQuestions;

        if (_agentOptions.TriageBeforeDecomposition)
        {
            if (LooksCompound(query.Question))
            {
                subQuestions = await _decomposer.DecomposeAsync(
                    query.Question, _agentOptions.MaxSubQuestions, ct);
            }
            else
            {
                _logger.LogDebug(
                    "Triage: question did not look compound, skipping decomposition call");
                subQuestions = new List<string> { query.Question };
            }
        }
        else
        {
            subQuestions = await _decomposer.DecomposeAsync(
                query.Question, _agentOptions.MaxSubQuestions, ct);
        }

        _logger.LogInformation(
            "Question decomposed into {Count} sub-question(s)", subQuestions.Count);

        // ── Step 2: Retrieve → verify → reformulate+retry, CONCURRENTLY ───────
        // Each sub-question's loop runs independently; SemaphoreSlim caps
        // how many are in flight at once via MaxConcurrentSubQuestions. The
        // order of subQuestionResults is preserved (matching the order
        // subQuestions came back in) via Task.WhenAll on an ordered list of
        // tasks, NOT a ConcurrentBag -- unlike the Vision extractor's
        // page-order requirement (which needed an explicit re-sort because
        // pages could complete in any order), Task.WhenAll on an array
        // already returns results in the same order as the input tasks,
        // regardless of which one finished first.
        var semaphore = new SemaphoreSlim(_agentOptions.MaxConcurrentSubQuestions);

        var subQuestionTasks = subQuestions.Select(async subQuestion =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                return await ProcessSubQuestionAsync(subQuestion, query, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var subQuestionResults = (await Task.WhenAll(subQuestionTasks)).ToList();

        // ── Step 3: Synthesize the final, attributed answer ──────────────────
        var response = await _agentSynthesizer.SynthesizeAsync(
            query.Question, subQuestionResults, ct);

        sw.Stop();

        _logger.LogInformation(
            "Agent query complete | SubQuestions={Count} | Verified={Verified} | Time={T}ms",
            subQuestionResults.Count,
            subQuestionResults.Count(r => r.Verified),
            sw.ElapsedMilliseconds);

        return response with { ProcessingTime = sw.Elapsed };
    }

    // Runs the retrieve -> verify -> (reformulate + retry) loop for ONE
    // sub-question, up to MaxRetriesPerSubQuestion + 1 total attempts.
    // Unchanged from the sequential version -- concurrency was added
    // OUTSIDE this method (in ExecuteAsync's Task.WhenAll), not inside
    // it; each call to this method still runs its own attempts one after
    // another, since a sub-question's 2nd attempt genuinely depends on
    // its 1st attempt's verification result.
    private async Task<SubQuestionResult> ProcessSubQuestionAsync(
        string originalSubQuestion,
        QAQuery query,
        CancellationToken ct)
    {
        var currentQuestionText = originalSubQuestion;
        var maxAttempts = _agentOptions.MaxRetriesPerSubQuestion + 1;

        var lastRankedChunks = new List<RankedChunk>();
        string? lastReason = null;
        var verified = false;
        var attempts = 0;

        while (attempts < maxAttempts)
        {
            attempts++;

            var retrievalRequest = new RetrievalRequest(
                Query: currentQuestionText,
                DocumentIdFilter: query.DocumentIdFilter,
                UseGraph: query.UseGraph,
                UseReranking: query.UseReranking);

            var retrievalResult = await _retrievalPipeline.RetrieveAndRankAsync(retrievalRequest, ct);
            lastRankedChunks = retrievalResult.RankedChunks;

            var verification = await _verifier.VerifyAsync(currentQuestionText, lastRankedChunks, ct);
            lastReason = verification.Reason;

            if (verification.Verified)
            {
                verified = true;
                break;
            }

            if (attempts < maxAttempts)
            {
                _logger.LogDebug(
                    "Sub-question '{Question}' failed verification (attempt {Attempt}/{Max}), reformulating",
                    originalSubQuestion, attempts, maxAttempts);

                currentQuestionText = await _reformulator.ReformulateAsync(
                    currentQuestionText, verification.Reason, ct);
            }
        }

        if (!verified)
        {
            _logger.LogWarning(
                "Sub-question '{Question}' unverified after {Attempts} attempt(s). Last reason: {Reason}",
                originalSubQuestion, attempts, lastReason);
        }

        return new SubQuestionResult(
            Question: originalSubQuestion,
            RankedChunks: lastRankedChunks,
            Verified: verified,
            VerificationReason: lastReason,
            AttemptsUsed: attempts);
    }

    // Lightweight, local heuristic used ONLY when TriageBeforeDecomposition
    // is enabled -- deliberately NOT an LLM call. Unchanged from the
    // original version.
    private static bool LooksCompound(string question)
    {
        var lower = question.ToLowerInvariant();

        string[] compoundSignals =
        {
            " and ", " compare", " versus", " vs ", " vs.",
            "difference between", "both ", "each of"
        };

        if (compoundSignals.Any(signal => lower.Contains(signal)))
            return true;

        if (question.Count(c => c == '?') > 1)
            return true;

        if (question.Length > 120)
            return true;

        return false;
    }
}
