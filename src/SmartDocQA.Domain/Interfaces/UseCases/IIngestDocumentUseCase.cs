using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

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
