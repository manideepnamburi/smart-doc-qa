using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Application.UseCases;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Infrastructure.Chunking;
using SmartDocQA.Infrastructure.DocumentSources;
using SmartDocQA.Infrastructure.LLM;
using SmartDocQA.Infrastructure.Parsing;
using SmartDocQA.Infrastructure.Prompts;
using SmartDocQA.Infrastructure.Registry;
using SmartDocQA.Infrastructure.Retrieval;
using Microsoft.Extensions.Logging;
using SmartDocQA.Infrastructure.Guardrails;

namespace SmartDocQA.Infrastructure.DependencyInjection;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Bind all config sections ──────────────────────────────────────────
        services.Configure<AnthropicOptions>(
            configuration.GetSection(AnthropicOptions.SectionName));
        services.Configure<QdrantOptions>(
            configuration.GetSection(QdrantOptions.SectionName));
        services.Configure<Neo4jOptions>(
            configuration.GetSection(Neo4jOptions.SectionName));
        services.Configure<AzureDocIntelligenceOptions>(
            configuration.GetSection(AzureDocIntelligenceOptions.SectionName));
        services.Configure<OneDriveOptions>(
            configuration.GetSection(OneDriveOptions.SectionName));
        services.Configure<AzureBlobOptions>(
            configuration.GetSection(AzureBlobOptions.SectionName));
        services.Configure<RagOptions>(
            configuration.GetSection(RagOptions.SectionName));
        services.Configure<ChunkingOptions>(
            configuration.GetSection(ChunkingOptions.SectionName));
        services.Configure<PromptsOptions>(
            configuration.GetSection(PromptsOptions.SectionName));
        services.Configure<EmbeddingOptions>(
            configuration.GetSection(EmbeddingOptions.SectionName));
        services.Configure<RegistryOptions>(
            configuration.GetSection(RegistryOptions.SectionName));
        services.Configure<FolderScanOptions>(
            configuration.GetSection(FolderScanOptions.SectionName));
        services.Configure<BM25Options>(
            configuration.GetSection(BM25Options.SectionName));
        services.Configure<GuardrailOptions>(
          configuration.GetSection(GuardrailOptions.SectionName));
        services.Configure<VisionExtractionOptions>(
            configuration.GetSection(VisionExtractionOptions.SectionName));


        var ragOptions      = configuration.GetSection(RagOptions.SectionName).Get<RagOptions>() ?? new();
        var chunkingOptions = configuration.GetSection(ChunkingOptions.SectionName).Get<ChunkingOptions>() ?? new();

        // ── Document Source Resolvers ─────────────────────────────────────────
        services.AddScoped<IDocumentSourceResolver, LocalFileSourceResolver>();
        services.AddScoped<IDocumentSourceResolver, AzureBlobSourceResolver>();
        services.AddScoped<IDocumentSourceResolver, OneDriveSourceResolver>();
        services.AddScoped<IDocumentSourceResolverFactory, DocumentSourceResolverFactory>();

        // ── Document Registry (SQLite) ────────────────────────────────────────
        services.AddSingleton<IDocumentRegistry, SqliteDocumentRegistry>();

        // ── Folder Scanner ────────────────────────────────────────────────────
        services.AddSingleton<IFolderScanner, FolderScanner>();

        // ── Parsers ───────────────────────────────────────────────────────────
        services.AddScoped<IDocumentParser, PdfDocumentParser>();
        services.AddScoped<IDocumentParser, WordDocumentParser>();

        // ── Chunking — strategy from config ───────────────────────────────────
        services.AddScoped<FixedSizeChunkingStrategy>();
        services.AddScoped<IChunkingStrategy>(sp =>
            chunkingOptions.Strategy switch
            {
                "FixedSize" => sp.GetRequiredService<FixedSizeChunkingStrategy>(),
                _ => throw new InvalidOperationException(
                    $"Unknown chunking strategy: '{chunkingOptions.Strategy}'.")
            });

        // ── Metadata Enricher ─────────────────────────────────────────────────
        services.AddScoped<IMetadataEnricher, DefaultMetadataEnricher>();

        // ── LLM Client ────────────────────────────────────────────────────────
        services.AddHttpClient<ILlmClient, ClaudeHttpClient>()
            .AddStandardResilienceHandler();

        // ── Prompt Loader ─────────────────────────────────────────────────────
        services.AddSingleton<IPromptLoader, FilePromptLoader>();

        // ── Answer Synthesizer ────────────────────────────────────────────────
        services.AddScoped<IAnswerSynthesizer, ClaudeAnswerSynthesizer>();

        // ── Embedding Service — factory picks provider from config ────────────
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var options     = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger      = loggerFactory.CreateLogger("EmbeddingService");
            return EmbeddingServiceFactory.Create(options, logger);
        });

        // ── Vector Store — Qdrant ─────────────────────────────────────────────
        services.AddSingleton<IVectorStore, QdrantVectorStoreAdapter>();

        // ── BM25 Keyword Index ────────────────────────────────────────────────
        if (ragOptions.UseHybridIndex)
        {
            services.AddSingleton<IKeywordIndex, BM25KeywordIndex>();
            services.AddScoped<ISparseRetriever, BM25SparseRetriever>();
        }
        else
        {
            services.AddScoped<ISparseRetriever, NoOpSparseRetriever>();
        }

        // ── Graph Store + Retriever ───────────────────────────────────────────
        if (ragOptions.UseGraphRetrieval)
        {
            services.AddSingleton<IGraphStore, Neo4jGraphStore>();
            services.AddScoped<IGraphRetriever, Neo4jGraphRetriever>();
            services.AddScoped<IEntityExtractor, ClaudeEntityExtractor>();
        }
        else
        {
            services.AddSingleton<IGraphStore, NoOpGraphStore>();
            services.AddScoped<IEntityExtractor, NoOpEntityExtractor>();
        }

        // ── Dense Retriever ───────────────────────────────────────────────────
        services.AddScoped<IDenseRetriever, QdrantDenseRetriever>();

        // ── Fusion ────────────────────────────────────────────────────────────
        services.AddScoped<IFusionStrategy, RRFFusionStrategy>();

        // ── Reranker ─────────────────────────────────────────────────────────
        if (ragOptions.UseReranking)
            services.AddScoped<IReranker, ClaudeReranker>();

        // ── Table + Chart Extractors ──────────────────────────────────────────
        services.AddScoped<ITableExtractor>(ragOptions.UseTableExtraction
            ? sp => sp.GetRequiredService<AzureDocIntelligenceTableExtractor>()
            : _ => new NoOpTableExtractor());

        services.AddScoped<IChartExtractor>(ragOptions.UseChartExtraction
            ? sp => sp.GetRequiredService<ClaudeVisionChartExtractor>()
            : _ => new NoOpChartExtractor());

        services.AddScoped<AzureDocIntelligenceTableExtractor>();
        services.AddScoped<ClaudeVisionChartExtractor>();

        // ── Query Rewriter ────────────────────────────────────────────────────
        if (ragOptions.UseQueryRewriting)
            services.AddScoped<IQueryRewriter, ClaudeQueryRewriter>();
        else
            services.AddScoped<IQueryRewriter, PassthroughQueryRewriter>();

        // ── Document Repository ───────────────────────────────────────────────
        services.AddScoped<IDocumentRepository, CompositeDocumentRepository>();

        // ── Guardrails (Phase 6.5) ────────────────────────────────────────────
        // Multiple registrations against the same interface, same pattern as
        // IDocumentSourceResolver above -- QueryDocumentUseCase resolves all
        // of them as an IEnumerable and runs each one. Uncomment each line as
        // its matching guardrail class is uncommented and tested.

        services.AddScoped<IInputGuardrail, PromptInjectionGuardrail>();   // CHECK 1 -- active

         services.AddScoped<IInputGuardrail, PiiScrubGuardrail>();       // CHECK 2 -- uncomment when ready
         services.AddScoped<IOutputGuardrail, GroundingCheckGuardrail>(); // CHECK 3 -- uncomment when ready
        services.AddScoped<IInputGuardrail, OffTopicGuardrail>();       // CHECK 4 -- uncomment when ready
        services.AddScoped<IInputGuardrail, LlmSafetyGuardrail>();      // CHECK 5 -- uncomment LAST (most expensive; must stay last in this list so cheap checks run first)

        // ── Query Decomposer (Phase 7.5) ──────────────────────────────────────
        services.AddScoped<IQueryDecomposer, ClaudeQueryDecomposer>();

        // ── Answer Verifier (Phase 7.5) ───────────────────────────────────────
        services.AddScoped<IAnswerVerifier, ClaudeAnswerVerifier>();

        // ── Query Reformulator (Phase 7.5) ────────────────────────────────────
        services.AddScoped<IQueryReformulator, ClaudeQueryReformulator>();

        // ── Agent Answer Synthesizer (Phase 7.5) ──────────────────────────────
        services.AddScoped<IAgentAnswerSynthesizer, ClaudeAgentAnswerSynthesizer>();

        return services;

        return services;
    }
}
