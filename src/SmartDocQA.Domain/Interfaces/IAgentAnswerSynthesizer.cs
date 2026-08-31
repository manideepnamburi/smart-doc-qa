using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Combines the results of every sub-question in an agentic query into
/// one final, coherent answer -- with citations traceable back to WHICH
/// sub-question each cited fact came from, not just a flat pooled list.
///
/// WHY THIS IS SEPARATE FROM IAnswerSynthesizer:
/// IAnswerSynthesizer answers ONE query from ONE set of ranked chunks --
/// that's the right shape for QueryDocumentUseCase's single-pass
/// pipeline. The agentic pipeline (Phase 7.5) produces MULTIPLE
/// sub-answers, each with its own evidence and verification outcome, and
/// some sub-questions may have failed verification entirely (evidence
/// exhausted its retries without being confirmed). This interface's job
/// is specifically to weave those N results together -- acknowledging
/// gaps where a sub-question came up empty rather than silently omitting
/// or guessing at that part -- which IAnswerSynthesizer was never
/// designed to do and shouldn't be forced to.
/// </summary>
public interface IAgentAnswerSynthesizer
{
    /// <summary>
    /// Synthesizes one final answer from all sub-question results.
    /// </summary>
    /// <param name="originalQuestion">
    /// The user's original, undecomposed question -- gives the synthesizer
    /// the full context of what's actually being asked, beyond just the
    /// sum of its sub-question parts.
    /// </param>
    /// <param name="subQuestionResults">
    /// Every sub-question's outcome: its text, ranked evidence, and whether
    /// it was verified. Unverified sub-questions (retries exhausted) are
    /// still included -- the synthesizer decides how to acknowledge them
    /// in the final answer rather than having them silently vanish.
    /// </param>
    Task<AgentQueryResponse> SynthesizeAsync(
        string originalQuestion,
        List<SubQuestionResult> subQuestionResults,
        CancellationToken ct = default);
}