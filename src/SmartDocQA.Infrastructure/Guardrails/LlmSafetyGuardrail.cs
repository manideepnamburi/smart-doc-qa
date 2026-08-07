using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.Guardrails;

// ═════════════════════════════════════════════════════════════════════════
// CHECK 5 OF 5 — LLM SAFETY CLASSIFIER
//
// Real Claude API call on every question. Catches paraphrased/novel attack
// framings no fixed regex list could cover. Own enable flag, own model
// choice (LlmSafetyCheckModel), independent of AnthropicOptions.ChatModel.
//
// COST-ORDERING: must be registered LAST among input guardrails.
//
// FAIL-OPEN BY DESIGN: classifier errors let the question through (logged
// loudly), rather than blocking on infrastructure failure.
//
// fallbackToLLM interaction: an "off_topic" classification is a SCOPE
// preference, not a security concern -- if the caller has opted into
// general-knowledge answers, off-topic questions are allowed through.
// Every other category (prompt_injection, harmful_request, other) is
// still blocked regardless of this flag, since those are genuine safety
// concerns that a scope preference must never override.
// ═════════════════════════════════════════════════════════════════════════

public class LlmSafetyGuardrail : IInputGuardrail
{
    private readonly ILlmClient _llmClient;
    private readonly GuardrailOptions _options;
    private readonly ILogger<LlmSafetyGuardrail> _logger;
    private readonly string _promptTemplate;

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

    public async Task<GuardrailResult> CheckAsync(string question, bool fallbackToLLM = false, CancellationToken ct = default)
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
                // Off-topic + explicit opt-in to general knowledge = allow.
                // Every other unsafe category stays blocked no matter what.
                if (classification.Category == "off_topic" && fallbackToLLM)
                {
                    _logger.LogInformation(
                        "LLM safety guardrail classified question as off_topic but allowed it through (FallbackToLLM=true). Question: '{Question}'",
                        question);
                    return GuardrailResult.Pass();
                }

                _logger.LogWarning(
                    "LLM safety guardrail rejected question. Category={Category}, Reason={Reason}. Question: '{Question}'",
                    classification.Category, classification.Reason, question);

                // Fixed double-message bug: this used to prepend "Your
                // question could not be processed:" itself, which
                // QueryDocumentUseCase's Step 0 ALSO prepends, resulting
                // in that phrase appearing twice in the final response.
                // Return only the specific reason here.
                return GuardrailResult.Fail($"({classification.Category}) {classification.Reason}");
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
