using SmartDocQA.Domain.Enums;

namespace SmartDocQA.Domain.Models;

/// <summary>
/// The output of QueryDocumentUseCase -- the synthesized answer plus
/// everything needed to understand how confident and well-grounded it is:
/// its citations, confidence level, which retrieval mode ran, where the
/// answer actually came from, and timing/chunk-count metadata for
/// debugging and observability.
/// </summary>
public record QAResult(
    string Answer,
    List<Citation> Citations,
    ConfidenceLevel Confidence,
    RetrievalMode RetrievalMode,

    /// <summary>
    /// Where the answer came from:
    /// Document    → found in your ingested documents (citations available)
    /// LLMFallback → from Claude's general knowledge (no citations)
    /// NotFound    → not found and fallback was not requested
    /// </summary>
    AnswerSource AnswerSource,

    int ChunksRetrieved,
    int ChunksAfterRerank,
    TimeSpan ProcessingTime
);