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
