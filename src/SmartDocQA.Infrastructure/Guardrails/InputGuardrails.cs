using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.Guardrails;

// ═════════════════════════════════════════════════════════════════════════
// CHECK 1 OF 4 — PROMPT INJECTION DETECTION (ACTIVE)
//
// Cheap, deterministic, zero external dependencies. Ignores fallbackToLLM
// entirely -- this is a SECURITY check, not a topic-scope check, so it
// must never be bypassed by the "answer from general knowledge" toggle.
// ═════════════════════════════════════════════════════════════════════════

public class PromptInjectionGuardrail : IInputGuardrail
{
    private readonly GuardrailOptions _options;
    private readonly ILogger<PromptInjectionGuardrail> _logger;

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

    public Task<GuardrailResult> CheckAsync(string question, bool fallbackToLLM = false, CancellationToken ct = default)
    {
        if (!_options.EnablePromptInjectionCheck)
            return Task.FromResult(GuardrailResult.Pass());

        // fallbackToLLM is deliberately unused here -- see class docstring.

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

// ═════════════════════════════════════════════════════════════════════════
// CHECK 2 OF 4 — PII SCRUB
//
// Same security-check reasoning as Check 1: fallbackToLLM is ignored.
// Personal data in the question is a problem whether or not general-
// knowledge answers are allowed.
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

    public Task<GuardrailResult> CheckAsync(string question, bool fallbackToLLM = false, CancellationToken ct = default)
    {
        if (!_options.EnablePiiScrubCheck)
            return Task.FromResult(GuardrailResult.Pass());

        // fallbackToLLM is deliberately unused here -- see class docstring.

        foreach (var (pattern, description) in PiiPatterns)
        {
            if (pattern.IsMatch(question))
            {
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

// ═════════════════════════════════════════════════════════════════════════
// CHECK 4 OF 4 — OFF-TOPIC REJECTION
//
// The one check where fallbackToLLM DOES change behavior: if the caller
// has explicitly opted into general-knowledge answers, "unrelated to the
// documents" is no longer a rejection reason -- that's precisely what
// FallbackToLLM=true means. Skipping here also saves the embedding call +
// vector search entirely, not just the rejection.
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

    public async Task<GuardrailResult> CheckAsync(string question, bool fallbackToLLM = false, CancellationToken ct = default)
    {
        if (!_options.EnableOffTopicCheck)
            return GuardrailResult.Pass();

        if (fallbackToLLM)
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
