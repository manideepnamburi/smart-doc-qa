using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.Guardrails;

// ═════════════════════════════════════════════════════════════════════════
// CHECK 1 OF 4 — PROMPT INJECTION DETECTION (ACTIVE — test this one first)
//
// Cheap, deterministic, zero external dependencies (no LLM call, no
// embedding call) — this is pure pattern matching against known attack
// phrasings. Honest limitation, documented here rather than hidden: a
// determined attacker can paraphrase around any fixed pattern list. This
// catches the common/naive injection attempts, not a sophisticated one.
// A more thorough defense would add an LLM-based classifier as a second
// layer (see the Known Issues note we'll add to the README once this is
// tested) — deliberately out of scope for this first pass.
// ═════════════════════════════════════════════════════════════════════════

public class PromptInjectionGuardrail : IInputGuardrail
{
    private readonly GuardrailOptions _options;
    private readonly ILogger<PromptInjectionGuardrail> _logger;

    // Known injection phrasings, case-insensitive. Each one is a real
    // pattern seen in published prompt-injection attack writeups — this is
    // not an exhaustive list, it's the common/obvious cases.
    private static readonly (Regex Pattern, string Description)[] InjectionPatterns =
    {
        (new Regex(@"ignore\s+(all\s+|the\s+)?(previous|prior|above)\s+instructions", RegexOptions.IgnoreCase),
            "attempt to override prior instructions"),
        (new Regex(@"disregard\s+(the\s+)?(above|previous|prior)", RegexOptions.IgnoreCase),
            "attempt to override prior instructions"),
        (new Regex(@"you\s+are\s+now\s+", RegexOptions.IgnoreCase),
            "attempt to reassign the assistant's role"),
        (new Regex(@"new\s+instructions?\s*:", RegexOptions.IgnoreCase),
            "attempt to inject new instructions"),
        (new Regex(@"reveal\s+(your|the)\s+(system\s+)?prompt", RegexOptions.IgnoreCase),
            "attempt to extract the system prompt"),
        (new Regex(@"^\s*(system|assistant)\s*:", RegexOptions.IgnoreCase),
            "fake role marker attempting to impersonate a system/assistant turn"),
        (new Regex(@"act\s+as\s+(if\s+)?you\s+(are|were)\s+", RegexOptions.IgnoreCase),
            "attempt to reassign the assistant's role"),
    };

    public PromptInjectionGuardrail(
        IOptions<GuardrailOptions> options,
        ILogger<PromptInjectionGuardrail> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<GuardrailResult> CheckAsync(string question, CancellationToken ct = default)
    {
        if (!_options.EnablePromptInjectionCheck)
            return Task.FromResult(GuardrailResult.Pass());

        foreach (var (pattern, description) in InjectionPatterns)
        {
            if (pattern.IsMatch(question))
            {
                _logger.LogWarning(
                    "Prompt injection guardrail rejected question: {Description}. Question: '{Question}'",
                    description, question);
                return Task.FromResult(GuardrailResult.Fail(
                    $"Your question appears to contain an attempt to override system instructions ({description}). " +
                    "Please rephrase your question as a genuine request about the ingested documents."));
            }
        }

        return Task.FromResult(GuardrailResult.Pass());
    }
}


// CHECK 2 OF 4 — PII SCRUB (currently disabled — uncomment when ready to test)
//
// Same category as Check 1: cheap, deterministic regex matching, no LLM or
// embedding call. Scans the QUESTION text (not the retrieved documents —
// that's a different concern) for patterns that look like personal data,
// and rejects outright rather than attempting to silently redact-and-
// continue. Rejecting is the safer default: silently modifying what the
// user asked risks confusing them about why the answer doesn't match their
// actual question. A redact-and-continue variant is a reasonable future
// enhancement, documented rather than built here.
//
// TO ACTIVATE: uncomment this whole class, then in
// InfrastructureServiceExtensions.cs uncomment the matching
// AddScoped<IInputGuardrail, PiiScrubGuardrail>() line, then set
// "EnablePiiScrubCheck": true in appsettings.json.
// ═════════════════════════════════════════════════════════════════════════

public class PiiScrubGuardrail : IInputGuardrail
{
    private readonly GuardrailOptions _options;
    private readonly ILogger<PiiScrubGuardrail> _logger;

    private static readonly (Regex Pattern, string Description)[] PiiPatterns =
    {
        (new Regex(@"\b\d{3}-\d{2}-\d{4}\b"), "Social Security Number"),
        (new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"), "email address"),
        (new Regex(@"\b(\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b"), "phone number"),
        (new Regex(@"\b(?:\d[ -]*?){13,16}\b"), "credit card number"),
    };

    public PiiScrubGuardrail(
        IOptions<GuardrailOptions> options,
        ILogger<PiiScrubGuardrail> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<GuardrailResult> CheckAsync(string question, CancellationToken ct = default)
    {
        if (!_options.EnablePiiScrubCheck)
            return Task.FromResult(GuardrailResult.Pass());

        foreach (var (pattern, description) in PiiPatterns)
        {
            if (pattern.IsMatch(question))
            {
                // Deliberately do NOT log the actual matched PII value —
                // only that a match of this type occurred. Logging the
                // real SSN/email/etc. would defeat the entire point of
                // this guardrail.
                _logger.LogWarning(
                    "PII guardrail rejected a question containing what appears to be a {Description}.",
                    description);
                return Task.FromResult(GuardrailResult.Fail(
                    $"Your question appears to contain personal information (a {description}). " +
                    "Please rephrase your question without including personal data."));
            }
        }

        return Task.FromResult(GuardrailResult.Pass());
    }
}



// CHECK 4 OF 4 — OFF-TOPIC REJECTION (currently disabled — uncomment LAST,
// after checks 1-3, since this one has real dependencies to wire up)
//
// Unlike checks 1-2, this ISN'T free — it makes a real embedding call plus
// a top-1 vector search, reusing the same IEmbeddingService and IVectorStore
// already registered for the main retrieval pipeline (no new infrastructure
// needed). Still meaningfully cheaper than letting the FULL pipeline run
// (dense+sparse+graph retrieval, RRF fusion, reranking, and a Claude
// synthesis call) only to discover nothing relevant exists — this check
// fails fast after one cheap similarity lookup instead.
//
// Distinct from the existing "AnswerSource.NotFound" path: that happens
// AFTER the full pipeline already ran and found nothing. This guardrail
// rejects BEFORE any of that cost is incurred, and also catches a case the
// existing path doesn't: if FallbackToLLM=true, an off-topic question would
// otherwise get a full free-form answer from Claude's general knowledge —
// this guardrail can reject it before that happens, if that's the desired
// behavior for your use case.
//
// TO ACTIVATE: uncomment this whole class, then in
// InfrastructureServiceExtensions.cs uncomment the matching
// AddScoped<IInputGuardrail, OffTopicGuardrail>() line, then set
// "EnableOffTopicCheck": true and tune "OffTopicSimilarityThreshold" in
// appsettings.json.
// ═════════════════════════════════════════════════════════════════════════

public class OffTopicGuardrail : IInputGuardrail
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly GuardrailOptions _options;
    private readonly ILogger<OffTopicGuardrail> _logger;

    public OffTopicGuardrail(
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IOptions<GuardrailOptions> options,
        ILogger<OffTopicGuardrail> logger)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GuardrailResult> CheckAsync(string question, CancellationToken ct = default)
    {
        if (!_options.EnableOffTopicCheck)
            return GuardrailResult.Pass();

        var queryVector = await _embeddingService.EmbedAsync(question, ct);
        var topResults = await _vectorStore.SearchAsync(queryVector, topK: 1, ct: ct);

        var topScore = topResults.Count > 0 ? topResults[0].Score : 0f;

        if (topScore < _options.OffTopicSimilarityThreshold)
        {
            _logger.LogWarning(
                "Off-topic guardrail rejected question (top similarity {Score:F3} below threshold {Threshold:F3}). Question: '{Question}'",
                topScore, _options.OffTopicSimilarityThreshold, question);
            return GuardrailResult.Fail(
                "Your question doesn't appear to relate to the ingested documents. " +
                "Please ask something about the content that has been loaded into this system.");
        }

        return GuardrailResult.Pass();
    }
}

