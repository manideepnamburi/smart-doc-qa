using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure.Guardrails;


// CHECK 3 OF 4 — GROUNDING CHECK (currently disabled — this is the OUTPUT
// side guardrail, activate after checks 1-2, before check 4)
//
// Deterministic, zero LLM/embedding cost — just an internal-consistency
// check on the QAResult that already came back from synthesis. This is
// the cheap version of "grounding check": it catches the exact bug found
// during the eval harness work (AnswerSource says Document/grounded, but
// Citations is empty — an internal contradiction that should never happen).
//
// This is NOT a full semantic grounding check (i.e. "does the answer TEXT
// actually match what the cited chunks say") — that would require an LLM
// pass reading both the answer and the citations, similar in spirit to the
// eval harness's LLM-as-judge, but running inline on every production
// query. Documented as a future enhancement, not built here — see the
// README Known Issues note once this check is tested and committed.
//
// TO ACTIVATE: uncomment this whole class, then in
// InfrastructureServiceExtensions.cs uncomment the matching
// AddScoped<IOutputGuardrail, GroundingCheckGuardrail>() line, then set
// "EnableGroundingCheck": true in appsettings.json.
// ═════════════════════════════════════════════════════════════════════════

public class GroundingCheckGuardrail : IOutputGuardrail
{
    private readonly GuardrailOptions _options;
    private readonly ILogger<GroundingCheckGuardrail> _logger;

    public GroundingCheckGuardrail(
        IOptions<GuardrailOptions> options,
        ILogger<GroundingCheckGuardrail> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<GuardrailResult> CheckAsync(QAResult result, CancellationToken ct = default)
    {
        if (!_options.EnableGroundingCheck)
            return Task.FromResult(GuardrailResult.Pass());

        // The core contradiction this catches: the synthesizer claims the
        // answer came from the ingested documents (AnswerSource.Document),
        // but provided zero citations to back that claim up. This exact
        // inconsistency was found independently three times during the
        // eval harness work this session -- this check turns "we noticed
        // it in eval reports" into "the system catches it automatically,
        // in production, on every query."
        if (result.AnswerSource == AnswerSource.Document && result.Citations.Count == 0)
        {
            _logger.LogWarning(
                "Grounding check failed: AnswerSource=Document but zero citations. Answer: '{Answer}'",
                result.Answer);
            return Task.FromResult(GuardrailResult.Fail(
                "The answer claimed to be grounded in the source documents but provided no supporting citations."));
        }

        return Task.FromResult(GuardrailResult.Pass());
    }
}
