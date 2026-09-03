namespace SmartDocQA.Domain.Models;

/// <summary>
/// The input to QueryDocumentUseCase (and, reused as-is, to
/// AgentQueryUseCase via AgentController) -- everything needed to answer
/// one question against the ingested documents. Every field is optional
/// or defaulted so an existing direct-API caller -- Swagger, curl, the
/// eval harness, every guardrail test written so far -- is completely
/// unaffected by fields added later.
/// </summary>
public record QAQuery(
    string Question,
    bool UseGraph = true,
    bool UseReranking = true,

    /// <summary>
    /// When true: if the answer is not found in documents,
    /// Claude will answer from its own general knowledge.
    /// When false: returns "not found" if no relevant chunks are found.
    /// Example: healthcare doc + politics question + FallbackToLLM=true
    ///          → Claude answers from general knowledge, clearly labelled.
    /// </summary>
    bool FallbackToLLM = false,

    string? DocumentIdFilter = null,

    /// <summary>
    /// Recent conversation turns, oldest first. Optional -- a request with
    /// no history behaves exactly as it always has (no follow-up
    /// resolution). Only the React chat UI populates this; direct API
    /// testing is unaffected unless you choose to include it.
    /// </summary>
    List<ConversationTurn>? History = null
);
