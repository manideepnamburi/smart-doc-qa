using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

// ─── Document Source Resolution ───────────────────────────────────────────────

/// <summary>
/// Resolves a DocumentSource (local / OneDrive / Azure Blob) into a readable stream.
/// Each source type has its own implementation; selected via factory.
/// </summary>
public interface IDocumentSourceResolver
{
    Task<Stream> ResolveAsync(DocumentSource source, CancellationToken ct = default);
    bool CanResolve(DocumentSource source);
}

/// <summary>
/// Factory that picks the right IDocumentSourceResolver for a given source type.
/// </summary>
public interface IDocumentSourceResolverFactory
{
    IDocumentSourceResolver GetResolver(DocumentSource source);
}

// ─── Parsing ─────────────────────────────────────────────────────────────────

/// <summary>
/// Extracts raw text + page metadata from a document stream.
/// Implementations: PdfParser, WordDocParser.
/// </summary>
public interface IDocumentParser
{
    bool CanParse(string fileName);
    Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default);
}

public record ParsedPage(int PageNumber, string RawText, bool IsScanned);
public record ParsedDocument(string FileName, List<ParsedPage> Pages, int TotalPages);

/// <summary>
/// Extracts tables from a document using Azure Document Intelligence.
/// Optional — disabled via config flag if not needed.
/// </summary>
public interface ITableExtractor
{
    Task<List<ExtractedTable>> ExtractTablesAsync(Stream stream, string fileName, CancellationToken ct = default);
}

public record ExtractedTable(int PageNumber, string MarkdownContent, string Caption);

/// <summary>
/// Extracts embedded images/charts from a document and summarises them via Claude Vision.
/// Optional — disabled via config flag.
/// </summary>
public interface IChartExtractor
{
    Task<List<ExtractedChart>> ExtractChartsAsync(Stream stream, string fileName, CancellationToken ct = default);
}

public record ExtractedChart(int PageNumber, string Description, byte[] ImageBytes);

// ─── Chunking ─────────────────────────────────────────────────────────────────

/// <summary>
/// Splits parsed content into DocumentChunks ready for embedding.
/// Strategy is selected from config (FixedSize or Semantic).
/// </summary>
public interface IChunkingStrategy
{
    Task<List<DocumentChunk>> ChunkAsync(ParsedDocument document, string documentId, CancellationToken ct = default);
}

// ─── Metadata Enrichment ──────────────────────────────────────────────────────

/// <summary>
/// Adds metadata to chunks after chunking — timestamps, source type, permissions, etc.
/// </summary>
public interface IMetadataEnricher
{
    Task<List<DocumentChunk>> EnrichAsync(List<DocumentChunk> chunks, DocumentSource source, CancellationToken ct = default);
}

// ─── Embedding ────────────────────────────────────────────────────────────────

/// <summary>
/// Generates float[] embeddings for a list of texts.
/// Implementation calls Claude or any other embedding provider.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<List<float[]>> EmbedBatchAsync(List<string> texts, CancellationToken ct = default);
}

/// <summary>
/// Vector store — Qdrant today, swappable tomorrow.
/// SK's IVectorStore abstraction sits under this.
/// </summary>
public interface IVectorStore
{
    Task UpsertAsync(DocumentChunk chunk, CancellationToken ct = default);

    Task UpsertBatchAsync(List<DocumentChunk> chunks, CancellationToken ct = default);

    Task<List<RetrievedChunk>> SearchAsync(float[] queryVector, int topK,
        string? documentIdFilter = null, CancellationToken ct = default);

    Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Delete the entire collection and recreate it empty.
    /// Used during full system reset — wipes ALL vectors.
    /// </summary>
    Task ResetCollectionAsync(CancellationToken ct = default);
}


/// <summary>
/// BM25 keyword index — SemanticKernel.Rankers.BM25 today, Lucene tomorrow.
/// </summary>
public interface IKeywordIndex
{
    Task IndexAsync(DocumentChunk chunk, CancellationToken ct = default);
    Task IndexBatchAsync(List<DocumentChunk> chunks, CancellationToken ct = default);
    Task<List<RetrievedChunk>> SearchAsync(string query, int topK, string? documentIdFilter = null, CancellationToken ct = default);
    Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);    
}

/// <summary>
/// Graph store — Neo4j today, ArangoDB tomorrow.
/// </summary>
public interface IGraphStore
{
    Task UpsertEntityAsync(GraphEntity entity, CancellationToken ct = default);
    Task UpsertRelationshipAsync(GraphRelationship relationship, CancellationToken ct = default);
    Task<List<RetrievedChunk>> SearchByEntityAsync(string query, int topK, CancellationToken ct = default);
    Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default);
}

/// <summary>
/// Document repository — coordinates all 3 stores as a unit of work.
/// </summary>
public interface IDocumentRepository
{
    Task SaveAsync(List<DocumentChunk> chunks, DocumentMetadata metadata, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);
    Task<List<DocumentMetadata>> ListAsync(CancellationToken ct = default);
}

// ─── Retrieval ───────────────────────────────────────────────────────────────

public interface IDenseRetriever
{
    Task<List<RetrievedChunk>> RetrieveAsync(string query, int topK, string? documentIdFilter = null, CancellationToken ct = default);
}

public interface ISparseRetriever
{
    Task<List<RetrievedChunk>> RetrieveAsync(string query, int topK, string? documentIdFilter = null, CancellationToken ct = default);
}

public interface IGraphRetriever
{
    Task<List<RetrievedChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default);
}

/// <summary>
/// Fuses results from dense + sparse + graph retrievers.
/// Default implementation uses Reciprocal Rank Fusion (RRF).
/// </summary>
public interface IFusionStrategy
{
    List<FusedChunk> Fuse(
        List<RetrievedChunk> denseResults,
        List<RetrievedChunk> sparseResults,
        List<RetrievedChunk>? graphResults,
        int topK);
}

// ─── Reranking ───────────────────────────────────────────────────────────────

/// <summary>
/// Re-scores fused chunks against the original query for precision.
/// Default: Claude-as-reranker. Swappable with cross-encoder ONNX model.
/// </summary>
public interface IReranker
{
    Task<List<RankedChunk>> RerankAsync(string query, List<FusedChunk> candidates, int topK, CancellationToken ct = default);
}

// ─── LLM + Generation ────────────────────────────────────────────────────────

/// <summary>
/// Raw LLM client — wraps Claude HTTP API today, swappable to OpenAI, Azure OpenAI.
/// </summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<string> CompleteWithVisionAsync(string systemPrompt, string userPrompt, byte[] imageBytes, CancellationToken ct = default);
}

/// <summary>
/// Loads prompt templates from the Prompts/ folder.
/// </summary>
public interface IPromptLoader
{
    string Load(string promptFileName);
    string LoadAndFill(string promptFileName, Dictionary<string, string> variables);
}

/// <summary>
/// Builds the final answer from ranked chunks + query. Extracts citations.
/// </summary>
public interface IAnswerSynthesizer
{
    Task<QAResult> SynthesizeAsync(QAQuery query, List<RankedChunk> rankedChunks, CancellationToken ct = default);
}

/// <summary>
/// Rewrites ambiguous or vague queries for better retrieval.
/// </summary>
public interface IQueryRewriter
{
    Task<string> RewriteAsync(string originalQuery, CancellationToken ct = default);
}

/// <summary>
/// Extracts named entities and relationships from chunk text for the Graph store.
/// </summary>
public interface IEntityExtractor
{
    Task<(List<GraphEntity> Entities, List<GraphRelationship> Relationships)> ExtractAsync(
        DocumentChunk chunk, CancellationToken ct = default);
}

// ── Add this to src/SmartDocQA.Domain/Interfaces/IAll.cs ──
// Place it near the other use-case-facing interfaces (e.g. alongside
// IDocumentRepository), since it plays the same role: an abstraction that
// Infrastructure/Application implement and other Application classes
// consume, instead of depending on each other's concrete classes directly.

/// <summary>
/// Orchestrates ingestion of a single document end-to-end (parse, chunk,
/// embed, persist, graph-extract — see IngestDocumentUseCase for the full
/// pipeline). Extracted as an interface so callers like IngestFolderUseCase
/// depend on this abstraction rather than the concrete IngestDocumentUseCase
/// class, consistent with every other cross-class dependency in this
/// codebase, and so the single-document ingestion step can be faked in
/// tests without needing every downstream dependency (parsers, embedders,
/// Qdrant, Neo4j, ...) wired up.
/// </summary>
public interface IIngestDocumentUseCase
{
    Task<DocumentMetadata> ExecuteAsync(DocumentSource source, CancellationToken ct = default);
}
