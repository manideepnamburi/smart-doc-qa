namespace SmartDocQA.Application.Configuration;

public class AnthropicOptions
{
    public const string SectionName = "Anthropic";
    public string ApiKey { get; set; } = string.Empty;
    public string ChatModel { get; set; } = "claude-haiku-4-5";
    public string VisionModel { get; set; } = "claude-haiku-4-5";
    public int MaxTokens { get; set; } = 1500;
    public int RetryCount { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;

    public double Temperature { get; set; } = 0;
}

public class QdrantOptions
{
    public const string SectionName = "Qdrant";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6334;
    public string CollectionName { get; set; } = "smart-doc-qa";
    public int VectorSize { get; set; } = 1536;
}

public class Neo4jOptions
{
    public const string SectionName = "Neo4j";
    public string Uri { get; set; } = "bolt://localhost:7687";
    public string Username { get; set; } = "neo4j";
    public string Password { get; set; } = "password123";
}

public class AzureDocIntelligenceOptions
{
    public const string SectionName = "AzureDocIntelligence";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
}

public class OneDriveOptions
{
    public const string SectionName = "OneDrive";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

public class AzureBlobOptions
{
    public const string SectionName = "AzureBlob";
    public string ConnectionString { get; set; } = string.Empty;
    public string DefaultContainer { get; set; } = "documents";
}

public class RagOptions
{
    public const string SectionName = "RAG";

    public int TopKDense { get; set; } = 20;
    public int TopKSparse { get; set; } = 20;
    public int TopKAfterFusion { get; set; } = 20;
    public int TopKAfterRerank { get; set; } = 5;
    public float VectorWeight { get; set; } = 0.6f;
    public float BM25Weight { get; set; } = 0.4f;
    public int RRF_K { get; set; } = 60;

    /// <summary>
    /// How many of the most recent conversation turns the query rewriter
    /// actually uses to resolve follow-up questions, regardless of how
    /// many turns the client sends. Keeps prompt size/cost bounded even
    /// if a client sends a very long conversation history.
    /// </summary>
    public int MaxHistoryTurns { get; set; } = 5;

    /// <summary>
    /// Minimum cosine similarity score for a chunk to pass to LLM.
    /// Range: 0.0 (no filter) to 1.0 (exact match only).
    /// Start at 0.5 — lower to 0.4 if too many "not found", raise to 0.6 to reduce hallucination.
    /// </summary>
    public float MinSimilarityScore { get; set; } = 0.5f;

    public bool UseGraphRetrieval { get; set; } = false;
    public bool UseReranking { get; set; } = false;
    public bool UseHybridIndex { get; set; } = true;
    public bool UseTableExtraction { get; set; } = false;
    public bool UseChartExtraction { get; set; } = false;
    public bool UseQueryRewriting { get; set; } = true;
}

public class ChunkingOptions
{
    public const string SectionName = "Chunking";
    public string Strategy { get; set; } = "FixedSize";
    public int TokenSize { get; set; } = 500;
    public int Overlap { get; set; } = 50;
}

public class PromptsOptions
{
    public const string SectionName = "Prompts";
    public string BasePath { get; set; } = "Prompts/";
    public string QASystem { get; set; } = "qa_system.txt";
    public string QAUser { get; set; } = "qa_user.txt";
    public string Rerank { get; set; } = "rerank_system.txt";
    public string EntityExtraction { get; set; } = "entity_extraction.txt";
    public string QueryRewrite { get; set; } = "query_rewrite.txt";
    public string DecomposeQuery { get; set; } = "decompose_query.txt";
}

public class EmbeddingOptions
{
    public const string SectionName = "Embedding";
    public string Provider { get; set; } = "AzureOpenAI";
    public string AzureEndpoint { get; set; } = string.Empty;
    public string AzureApiKey { get; set; } = string.Empty;
    public string AzureEmbeddingDeployment { get; set; } = "text-embedding-ada-002";
    public string OpenAIApiKey { get; set; } = string.Empty;
    public string OpenAIEmbeddingModel { get; set; } = "text-embedding-3-small";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaEmbeddingModel { get; set; } = "all-minilm";
    public int VectorSize { get; set; } = 1536;
}

public class RegistryOptions
{
    public const string SectionName = "Registry";
    public string DatabasePath { get; set; } = "registry/documents.db";
}

public class FolderScanOptions
{
    public const string SectionName = "FolderScan";
    public List<string> SupportedExtensions { get; set; } = new()
    {
        ".pdf",
        ".docx",
        ".doc",
        ".txt"
    };
}

/// <summary>
/// Configuration for the BM25 keyword index (Phase 2).
/// </summary>
public class BM25Options
{
    public const string SectionName = "BM25";

    /// <summary>
    /// Path to the BM25 SQLite database file.
    /// Relative paths resolve from AppContext.BaseDirectory.
    /// Replaced the old JSON file (IndexPath) in the Phase 2 SQLite migration —
    /// full-document deletes went from ~22s to ~5-50ms at 10,000+ document scale.
    /// </summary>
    public string DatabasePath { get; set; } = "bm25/bm25.db";
}

/// <summary>
/// Phase 6.5 Guardrails config. Each check has its own on/off flag so they
/// can be enabled one at a time during testing without touching code —
/// just flip the flag in appsettings.json, matching the config-driven
/// pattern used everywhere else in this project (UseReranking,
/// UseGraphRetrieval, etc.).
///
/// All default to FALSE deliberately: guardrails should be an explicit,
/// deliberate opt-in per check as each is verified working, not silently
/// active the moment this class exists.
/// </summary>
public class GuardrailOptions
{
    public const string SectionName = "Guardrails";

    // ── Input guardrails ──────────────────────────────────────────────────
    public bool EnablePromptInjectionCheck { get; set; } = false;
    public bool EnablePiiScrubCheck { get; set; } = false;
    public bool EnableOffTopicCheck { get; set; } = false;

    /// <summary>
    /// Minimum top-1 similarity score (cosine) a question must have against
    /// the ingested document collection to be considered "on topic."
    /// Below this, the off-topic guardrail rejects the question before any
    /// further pipeline work happens. Tune based on your embedding model —
    /// start conservative (low threshold) and raise it if legitimate
    /// questions are being rejected.
    /// </summary>
    public float OffTopicSimilarityThreshold { get; set; } = 0.3f;

    // ── Output guardrails ──────────────────────────────────────────────────
    public bool EnableGroundingCheck { get; set; } = false;

    // ── Check 5: LLM safety classifier (most expensive, fully optional) ────
    public bool EnableLlmSafetyCheck { get; set; } = false;

    /// <summary>
    /// Which model runs the safety classification call. Deliberately
    /// separate from AnthropicOptions.ChatModel -- a classification task
    /// doesn't need your best/most expensive model. Defaults to a cheap,
    /// fast Claude model; change based on your budget/accuracy trade-off.
    /// </summary>
    public string LlmSafetyCheckModel { get; set; } = "claude-haiku-4-5-20251001";
}

/// <summary>
/// CORS allowed origins, environment-driven so dev (Vite's default port)
/// and production (a real domain, once deployed) need zero code changes --
/// just a different appsettings value per environment.
/// </summary>
public class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Controls how many pages the Claude Vision chart/OCR extractor analyzes
/// concurrently during ingestion. Higher = faster ingest on large PDFs, but
/// tune this against your Anthropic account's actual RPM (Console > Settings
/// > Limits) -- too high triggers 429 rate-limit errors instead of the
/// per-page timeouts you'd see today.
/// </summary>
public class VisionExtractionOptions
{
    public const string SectionName = "VisionExtraction";

    public int MaxConcurrency { get; set; } = 5;
}

/// <summary>
/// Controls the agentic query pipeline (Phase 7.5): decomposing compound
/// questions into sub-questions, retrieving/verifying/retrying per
/// sub-question, then synthesizing a combined answer.
/// </summary>
public class AgentModeOptions
{
    public const string SectionName = "AgentMode";

    /// <summary>
    /// If true, a cheap triage check runs before deciding whether to
    /// decompose. If false, every question always goes through full
    /// decomposition. Both code paths exist; this flag just picks which
    /// one runs -- lets you compare cost/latency between the two later
    /// without changing code.
    /// </summary>
    public bool TriageBeforeDecomposition { get; set; } = false;

    /// <summary>
    /// Safety cap on how many sub-questions decomposition can split a
    /// single question into, so a pathological input can't explode into
    /// dozens of retrieval calls.
    /// </summary>
    public int MaxSubQuestions { get; set; } = 5;

    /// <summary>
    /// How many times a single sub-question can be retried (with a
    /// reformulated query) if the verifier fails it, before giving up
    /// on that sub-question.
    /// </summary>
    public int MaxRetriesPerSubQuestion { get; set; } = 2;
}