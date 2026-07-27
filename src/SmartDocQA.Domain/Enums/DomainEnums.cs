namespace SmartDocQA.Domain.Enums;

public enum DocumentSourceType
{
    LocalPath,
    OneDrive,
    AzureBlob
}

public enum ChunkType
{
    Text,
    Table,
    Chart,
    OcrPage
}

public enum ChunkingStrategy
{
    FixedSize,
    Semantic
}

public enum RetrievalMode
{
    DenseOnly,
    SparseOnly,
    Hybrid,
    HybridWithGraph
}

public enum ConfidenceLevel
{
    High,
    Medium,
    Low,
    NotFound
}

/// <summary>
/// Indicates where the answer came from.
/// Document    = answer found in ingested documents (with citations).
/// LLMFallback = answer from Claude's general knowledge (FallbackToLLM=true).
/// NotFound    = not found in documents and no fallback was requested.
/// </summary>
public enum AnswerSource
{
    Document,
    LLMFallback,
    NotFound
}
