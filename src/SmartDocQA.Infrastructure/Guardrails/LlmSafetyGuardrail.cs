using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.Guardrails;


// CHECK 5 OF 5 — LLM SAFETY CLASSIFIER (currently disabled — uncomment LAST,
// after checks 1-4, since this is the most expensive check by far)
//
// Unlike checks 1-4, this ISN'T free or even cheap — it's a real Claude API
// call on every single question. This is the "brain reading intent" layer
// discussed earlier: it catches paraphrased/novel attack phrasings that no
// fixed regex list could ever cover, at the cost of latency + money on
// every request. That's a genuine, deliberate trade-off, not a flaw —
// that's exactly why it has its OWN enable flag and its OWN model choice
// (LlmSafetyCheckModel), independent of AnthropicOptions.ChatModel. Default
// model is a cheap/fast one (Haiku) since a safety classification task
// doesn't need your best model — same reasoning as the eval harness's
// judge model choice.
//
// COST-ORDERING: this must be registered LAST among input guardrails (after
// PromptInjection, PiiScrub, OffTopic) so cheap checks reject obvious cases
// first, and this expensive LLM call only runs for questions that already
// passed every free check. .NET's DI resolves IEnumerable<T> in
// registration order, so registration order below directly controls this.
//
// FAIL-OPEN BY DESIGN: if the LLM call itself fails (network error,
// timeout, malformed response), this guardrail logs a warning and PASSES
// the question through rather than blocking it. Rationale: a guardrail
// infrastructure outage shouldn't take down basic Q&A availability. If you
// want the opposite (fail-closed -- reject on any classifier error), that's
// a one-line change, flagged below at the catch block.
//
// TO ACTIVATE: uncomment this whole class, then in
// InfrastructureServiceExtensions.cs uncomment the matching
// AddScoped<IInputGuardrail, LlmSafetyGuardrail>() line (it must be the
// LAST IInputGuardrail registration), then set "EnableLlmSafetyCheck": true
// in appsettings.json.
// ═════════════════════════════════════════════════════════════════════════

public class LlmSafetyGuardrail : IInputGuardrail
{
    private readonly ILlmClient _llmClient;
    private readonly GuardrailOptions _options;
    private readonly ILogger<LlmSafetyGuardrail> _logger;
    private readonly string _promptTemplate;

    // Hardcoded filename rather than a PromptsOptions config property --
    // this one prompt doesn't need to be independently swappable via
    // appsettings, keeping this guardrail self-contained. Lives at
    // src/SmartDocQA.API/Prompts/guardrail_safety_check.txt (same Prompts/
    // folder as every other prompt, already covered by the existing
    // CopyToOutputDirectory rule in the API's .csproj -- no build changes needed).
    private const string PromptFileName = "guardrail_safety_check.txt";

    public LlmSafetyGuardrail(
        ILlmClient llmClient,
        IOptions<GuardrailOptions> options,
        ILogger<LlmSafetyGuardrail> logger)
    {
        _llmClient = llmClient;
        _options = options.Value;
        _logger = logger;

        var promptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", PromptFileName);
        if (!File.Exists(promptPath))
            throw new FileNotFoundException(
                $"Guardrail safety-check prompt not found at '{promptPath}'. " +
                $"Ensure {PromptFileName} exists in src/SmartDocQA.API/Prompts/ and is " +
                $"set to CopyToOutputDirectory=PreserveNewest.");

        _promptTemplate = File.ReadAllText(promptPath);
    }

    public async Task<GuardrailResult> CheckAsync(string question, CancellationToken ct = default)
    {
        if (!_options.EnableLlmSafetyCheck)
            return GuardrailResult.Pass();

        try
        {
            var prompt = _promptTemplate.Replace("{{question}}", question);

            var response = await _llmClient.CompleteAsync(
                systemPrompt: "You are a safety classifier. Respond with ONLY the requested JSON, no other text.",
                userPrompt: prompt,
                ct: ct,
                modelOverride: _options.LlmSafetyCheckModel);

            var classification = ParseClassification(response);

            if (!classification.Safe)
            {
                _logger.LogWarning(
                    "LLM safety guardrail rejected question. Category={Category}, Reason={Reason}. Question: '{Question}'",
                    classification.Category, classification.Reason, question);
                return GuardrailResult.Fail(
                    $"Your question could not be processed ({classification.Category}): {classification.Reason}");
            }

            return GuardrailResult.Pass();
        }
        catch (Exception ex)
        {
            // FAIL-OPEN: log loudly, but let the question through. To make
            // this fail-CLOSED instead (reject on any classifier error),
            // replace "return GuardrailResult.Pass();" below with:
            //   return GuardrailResult.Fail("Safety check unavailable; question rejected as a precaution.");
            _logger.LogError(ex,
                "LLM safety guardrail failed to classify question -- failing OPEN (allowing the question through).");
            return GuardrailResult.Pass();
        }
    }

    private static SafetyClassification ParseClassification(string rawResponse)
    {
        var text = rawResponse.Trim();

        // Defensive stripping in case the model wraps its JSON in markdown
        // fences despite being told not to -- same pattern as the eval
        // harness's judge response parsing.
        if (text.StartsWith("```"))
        {
            text = text[(text.IndexOf('\n') + 1)..];
            var lastFence = text.LastIndexOf("```");
            if (lastFence >= 0) text = text[..lastFence];
        }

        return JsonSerializer.Deserialize<SafetyClassification>(text)
            ?? throw new InvalidOperationException($"Could not parse safety classifier response as JSON: {text}");
    }

    private class SafetyClassification
    {
        [JsonPropertyName("safe")]
        public bool Safe { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = "unknown";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }
}

