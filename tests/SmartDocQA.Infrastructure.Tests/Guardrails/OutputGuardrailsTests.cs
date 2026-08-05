using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Models;
using SmartDocQA.Infrastructure.Guardrails;
using Xunit;

namespace SmartDocQA.Infrastructure.Tests.Guardrails;

public class GroundingCheckGuardrailTests
{
    private static GroundingCheckGuardrail CreateGuardrail(bool enabled)
    {
        var options = Options.Create(new GuardrailOptions { EnableGroundingCheck = enabled });
        return new GroundingCheckGuardrail(options, NullLogger<GroundingCheckGuardrail>.Instance);
    }

    // Minimal QAResult builder -- only the two fields this guardrail
    // actually inspects (AnswerSource, Citations) vary per test; everything
    // else is filler with harmless defaults.
    private static QAResult MakeResult(AnswerSource answerSource, List<Citation> citations) => new(
        Answer: "Some answer text",
        Citations: citations,
        Confidence: ConfidenceLevel.High,
        RetrievalMode: RetrievalMode.Hybrid,
        AnswerSource: answerSource,
        ChunksRetrieved: 5,
        ChunksAfterRerank: 5,
        ProcessingTime: TimeSpan.FromSeconds(1));

    [Fact]
    public async Task CheckAsync_Disabled_AlwaysPasses_EvenForTheExactBugScenario()
    {
        var guardrail = CreateGuardrail(enabled: false);
        var buggyResult = MakeResult(AnswerSource.Document, new List<Citation>());

        var result = await guardrail.CheckAsync(buggyResult);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_AnswerSourceDocumentWithZeroCitations_Fails()
    {
        // Claim: this is THE bug this guardrail exists to catch -- the exact
        // inconsistency found three separate times during the eval harness
        // work (answer claims to be document-grounded but has no citations
        // to back that up).
        var guardrail = CreateGuardrail(enabled: true);
        var buggyResult = MakeResult(AnswerSource.Document, new List<Citation>());

        var result = await guardrail.CheckAsync(buggyResult);

        Assert.False(result.Passed);
        Assert.Contains("no supporting citations", result.Reason);
    }

    [Fact]
    public async Task CheckAsync_AnswerSourceDocumentWithCitationsPresent_Passes()
    {
        // Claim: the normal, correct case -- a grounded answer WITH
        // citations must never be flagged.
        var guardrail = CreateGuardrail(enabled: true);
        var goodResult = MakeResult(AnswerSource.Document, new List<Citation>
        {
            new("chunk-1", "test.pdf", 1, ChunkType.Text, "relevant excerpt")
        });

        var result = await guardrail.CheckAsync(goodResult);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_AnswerSourceNotFoundWithZeroCitations_Passes()
    {
        // Claim: a legitimate "not found" response has zero citations by
        // definition -- that's NOT a contradiction, and must not be flagged.
        // This distinguishes "correctly reported nothing found" from "claimed
        // grounding it doesn't have."
        var guardrail = CreateGuardrail(enabled: true);
        var notFoundResult = MakeResult(AnswerSource.NotFound, new List<Citation>());

        var result = await guardrail.CheckAsync(notFoundResult);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task CheckAsync_AnswerSourceLlmFallbackWithZeroCitations_Passes()
    {
        // Claim: an LLM-fallback answer (from Claude's general knowledge,
        // not the documents) legitimately has no citations -- also not a
        // contradiction, must not be flagged.
        var guardrail = CreateGuardrail(enabled: true);
        var fallbackResult = MakeResult(AnswerSource.LLMFallback, new List<Citation>());

        var result = await guardrail.CheckAsync(fallbackResult);

        Assert.True(result.Passed);
    }
}
