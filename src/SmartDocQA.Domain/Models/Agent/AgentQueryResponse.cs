namespace SmartDocQA.Domain.Models;

/// <summary>
/// The full response from POST /api/agent/query -- the final synthesized
/// answer plus the per-sub-question breakdown, so callers (and you,
/// debugging) can see exactly how the agent arrived at the answer,
/// including which sub-questions were verified and which weren't.
/// </summary>
public record AgentQueryResponse(
    string Answer,
    List<SubQuestionResult> SubQuestions,
    List<Citation> Citations,
    TimeSpan ProcessingTime);