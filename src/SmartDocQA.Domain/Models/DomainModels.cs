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


// ─── Query + Result ───────────────────────────────────────────────────────────

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

    string? DocumentIdFilter = null   // scope to a specific document
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
