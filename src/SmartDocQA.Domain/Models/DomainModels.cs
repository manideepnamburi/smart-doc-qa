using SmartDocQA.Domain.Enums;

namespace SmartDocQA.Domain.Models;

// ─── Document Source ──────────────────────────────────────────────────────────

/// <summary>
/// Represents where a document lives — local, OneDrive, or Azure Blob.
/// Passed by the caller; infrastructure layer resolves it to a stream.
/// </summary>
public record DocumentSource(
    DocumentSourceType SourceType,
    string Path,                    // local path, blob URI, or OneDrive item ID
    string? ContainerName = null,   // Azure Blob only
    string? FileName = null         // override display name if needed
);

// ─── Document ─────────────────────────────────────────────────────────────────

public record DocumentMetadata(
    string DocumentId,
    string FileName,
    string SourcePath,
    DocumentSourceType SourceType,
    DateTime IngestedAt,
    int TotalPages,
    int TotalChunks
);

// ─── Chunk ────────────────────────────────────────────────────────────────────

public record DocumentChunk(
    string ChunkId,
    string DocumentId,
    string FileName,
    string Content,
    ChunkType ChunkType,
    int PageNumber,
    int ChunkIndex,
    Dictionary<string, string> Metadata  // extensible — add whatever at ingestion
)
{
    public float[]? Embedding { get; init; }
}

// ─── Retrieval ────────────────────────────────────────────────────────────────

public record RetrievedChunk(
    DocumentChunk Chunk,
    float Score,
    string RetrievalSource   // "dense", "sparse", "graph"
);

public record FusedChunk(
    DocumentChunk Chunk,
    float FusedScore,
    float? DenseScore,
    float? SparseScore,
    float? GraphScore
);

public record RankedChunk(
    DocumentChunk Chunk,
    float RerankerScore,
    string RerankerReason
);

// ─── Citation ─────────────────────────────────────────────────────────────────

public record Citation(
    string ChunkId,
    string FileName,
    int PageNumber,
    ChunkType ChunkType,
    string RelevantExcerpt    // short snippet shown to user
);


//existing direct-API caller -- Swagger, curl, the eval harness, every
// guardrail test today -- is completely unaffected):

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

// ─── Graph ────────────────────────────────────────────────────────────────────

public record GraphEntity(
    string Name,
    string Type,       // PERSON, ORG, CONCEPT, METRIC, DATE, etc.
    string DocumentId,
    int PageNumber
);

public record GraphRelationship(
    string FromEntity,
    string ToEntity,
    string RelationType,   // REPORTS_TO, MENTIONED_WITH, CAUSED_BY, etc.
    string DocumentId
);


/// <summary>
/// One past exchange in an ongoing conversation. The client (React UI)
/// holds the full conversation and resends recent turns with each new
/// question -- the backend itself stays stateless, same as Anthropic's own
/// Messages API. See README "Guardrails"/"Conversation" section for the
/// full rationale once documented.
/// </summary>
public record ConversationTurn(string Question, string Answer);

/// <summary>
/// One sub-question's full lifecycle through the agent pipeline: the
/// question text itself, its retrieved+ranked evidence, whether the
/// verifier accepted it, and how many attempts it took.
///
/// CORRECTED (during AgentQueryUseCase build-out): originally typed as
/// List&lt;RetrievedChunk&gt;, but IRetrievalPipeline.RetrieveAndRankAsync
/// returns List&lt;RankedChunk&gt; -- the reranked, scored result ready for
/// synthesis. RankedChunk carries the reranker score needed to judge
/// evidence quality per sub-question; RetrievedChunk is the earlier,
/// pre-fusion/pre-rerank shape and isn't what synthesis should consume.
/// </summary>
public record SubQuestionResult(
    string Question,
    List<RankedChunk> RankedChunks,
    bool Verified,
    string? VerificationReason,
    int AttemptsUsed);

/// <summary>
/// Output of IAnswerVerifier.VerifyAsync — whether the retrieved evidence
/// for a sub-question actually answers it, plus a human-readable reason
/// either way (used both for logging and, on failure, to inform the
/// query reformulation step about what specifically was missing).
/// </summary>
public record VerificationResult(bool Verified, string Reason);

/// <summary>
/// The full response from /api/agent/query -- the final synthesized
/// answer plus the per-sub-question breakdown, so callers (and you,
/// debugging) can see exactly how the agent arrived at the answer.
/// </summary>
public record AgentQueryResponse(
    string Answer,
    List<SubQuestionResult> SubQuestions,
    List<Citation> Citations,
    TimeSpan ProcessingTime);

// ─── Parsing / extraction records (moved from IAll.cs during the
// Phase 7.5 interface-file split — these are plain data returned by
// IDocumentParser / ITableExtractor / IChartExtractor respectively) ───

public record ParsedPage(int PageNumber, string RawText, bool IsScanned);
public record ParsedDocument(string FileName, List<ParsedPage> Pages, int TotalPages);
public record ExtractedTable(int PageNumber, string MarkdownContent, string Caption);
public record ExtractedChart(int PageNumber, string Description, byte[] ImageBytes);

/// <summary>
/// Result of a single guardrail check. Passed=false means the question (for
/// input guardrails) or answer (for output guardrails) failed this specific
/// check, with Reason explaining why in human-readable form.
/// </summary>
public record GuardrailResult(bool Passed, string? Reason = null)
{
    /// <summary>Convenience factory — most checks either pass cleanly or fail with a reason.</summary>
    public static GuardrailResult Pass() => new(true, null);
    public static GuardrailResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Input to IRetrievalPipeline.RetrieveAndRankAsync — the query plus the
/// per-call options that control which retrieval sources and reranking
/// step run for this specific request. Mirrors the flags QAQuery already
/// exposes (DocumentIdFilter, UseGraph, UseReranking) so both
/// QueryDocumentUseCase and AgentQueryUseCase can build one of these
/// directly from their own query objects.
/// </summary>
public record RetrievalRequest(
    string Query,
    string? DocumentIdFilter,
    bool UseGraph,
    bool UseReranking);

/// <summary>
/// Output of IRetrievalPipeline.RetrieveAndRankAsync — the final ranked
/// chunks ready for answer synthesis, plus the metadata
/// (RetrievalMode, chunk counts) that QueryDocumentUseCase's response
/// already reports today. Keeping these together in one result means the
/// pipeline is the single source of truth for both the chunks AND the
/// metadata describing how they were retrieved.
/// </summary>
public record RetrievalPipelineResult(
    List<RankedChunk> RankedChunks,
    RetrievalMode Mode,
    int ChunksRetrieved,
    int ChunksAfterRerank);