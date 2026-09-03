using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Checks whether a set of retrieved+ranked chunks actually answers a
/// specific sub-question, as part of the agentic query pipeline
/// (Phase 7.5). This is deliberately separate from IOutputGuardrail's
/// grounding check: the guardrail verifies a FINAL synthesized answer is
/// grounded in its sources; this verifier runs earlier, per sub-question,
/// BEFORE synthesis -- its job is to decide whether retrieval succeeded
/// well enough to move on, or whether the sub-question needs to be
/// reformulated and retried.
///
/// WHY A SEPARATE INTERFACE FROM THE GUARDRAIL:
/// The two checks answer different questions at different pipeline
/// stages: "is this answer safe/grounded to return to the user" (output
/// guardrail, runs once, after synthesis) vs. "did retrieval find good
/// enough evidence for THIS ONE sub-question to be worth synthesizing
/// from" (this interface, runs once per sub-question, before synthesis,
/// and its failure triggers a retry rather than blocking the response).
/// </summary>
public interface IAnswerVerifier
{
    /// <summary>
    /// Returns Verified=true if the ranked chunks contain evidence that
    /// actually answers the sub-question (not just "topically related"),
    /// along with a human-readable Reason explaining the verdict -- used
    /// for logging when verified, and to inform query reformulation when
    /// not.
    /// </summary>
    Task<VerificationResult> VerifyAsync(
        string subQuestion,
        List<RankedChunk> rankedChunks,
        CancellationToken ct = default);
}
