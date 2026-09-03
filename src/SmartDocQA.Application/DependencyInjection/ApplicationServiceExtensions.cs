using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Application.Pipelines;
using SmartDocQA.Application.UseCases;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Application.DependencyInjection;

/// <summary>
/// Registers everything owned by the Application layer: use cases, the
/// retrieval pipeline abstraction, and any Application-level config
/// sections. Mirrors the existing AddInfrastructure() pattern in
/// SmartDocQA.Infrastructure.DependencyInjection -- each layer owns and
/// exposes its own registration extension method, called from Program.cs
/// in dependency order.
///
/// WHY IRetrievalPipeline IS REGISTERED HERE, NOT IN AddInfrastructure():
/// HybridRetrievalPipeline lives in SmartDocQA.Application (it's pure
/// orchestration over Domain interfaces, not a concrete external
/// integration), so registering it from Infrastructure would mean
/// Infrastructure referencing Application -- backwards from Clean
/// Architecture's intended dependency direction (Infrastructure depends
/// on Application/Domain, never the reverse). Registering it here keeps
/// every layer's registrations owned by that same layer.
/// </summary>
public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Config sections owned by the Application layer ─────────────────
        services.Configure<AgentModeOptions>(
            configuration.GetSection(AgentModeOptions.SectionName));

        // ── Use Cases ────────────────────────────────────────────────────────
        services.AddScoped<IngestDocumentUseCase>();

        // Also expose IngestDocumentUseCase via its interface, resolving to
        // the SAME scoped instance as the concrete registration above --
        // this lets DocumentsController keep injecting the concrete
        // IngestDocumentUseCase directly (unchanged) while IngestFolderUseCase
        // injects the new IIngestDocumentUseCase, without creating two
        // separate instances per request.
        services.AddScoped<IIngestDocumentUseCase>(
            sp => sp.GetRequiredService<IngestDocumentUseCase>());

        services.AddScoped<IngestFolderUseCase>();
        services.AddScoped<QueryDocumentUseCase>();
        services.AddScoped<DeleteDocumentsUseCase>();

        // ── Retrieval Pipeline (Phase 7.5) ──────────────────────────────────
        // HybridRetrievalPipeline is today's implementation (dense+sparse+
        // graph -> RRF fuse -> rerank). Swap this one line to change the
        // retrieval STRATEGY for every caller (QueryDocumentUseCase,
        // AgentQueryUseCase) at once.
        services.AddScoped<IRetrievalPipeline, HybridRetrievalPipeline>();

        // ── Agentic Query Pipeline (Phase 7.5) ──────────────────────────────
        services.AddScoped<AgentQueryUseCase>();

        return services;
    }
}