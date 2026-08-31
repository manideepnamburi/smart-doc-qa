namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Rewrites a sub-question that failed verification, using the reason
/// the verifier gave for the failure, so a retry has a genuinely better
/// chance of retrieving the right evidence instead of repeating the
/// exact same query and failing the exact same way.
///
/// WHY THIS IS SEPARATE FROM IQueryRewriter:
/// IQueryRewriter resolves ambiguous questions and follow-ups using
/// CONVERSATION HISTORY ("what about women?" -> a self-contained query).
/// This interface solves a different problem: given a sub-question that
/// already failed once, and a specific, concrete reason WHY it failed
/// (from IAnswerVerifier), produce a reformulated version targeting that
/// gap. The two never share a call site or a use case, and conflating
/// them would mean overloading one interface with two different
/// reasons to change -- a violation of single responsibility that would
/// make both harder to reason about and test independently.
/// </summary>
public interface IQueryReformulator
{
    /// <summary>
    /// Produces a reformulated version of a failed sub-question, informed
    /// by why the previous attempt's retrieved evidence didn't satisfy it.
    /// </summary>
    /// <param name="originalSubQuestion">The sub-question as it was last tried.</param>
    /// <param name="failureReason">
    /// The IAnswerVerifier's Reason from the failed attempt -- e.g. "evidence
    /// discusses the topic generally but doesn't break it down by age group."
    /// </param>
    Task<string> ReformulateAsync(
        string originalSubQuestion,
        string failureReason,
        CancellationToken ct = default);
}