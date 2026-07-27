using Microsoft.AspNetCore.Mvc;
using SmartDocQA.Application.UseCases;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DocumentsController : ControllerBase
{
    private readonly IngestDocumentUseCase _ingestUseCase;
    private readonly IngestFolderUseCase _ingestFolderUseCase;
    private readonly QueryDocumentUseCase _queryUseCase;
    private readonly DeleteDocumentsUseCase _deleteUseCase;
    private readonly IDocumentRegistry _registry;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IngestDocumentUseCase ingestUseCase,
        IngestFolderUseCase ingestFolderUseCase,
        QueryDocumentUseCase queryUseCase,
        DeleteDocumentsUseCase deleteUseCase,
        IDocumentRegistry registry,
        IVectorStore vectorStore,
        ILogger<DocumentsController> logger)
    {
        _ingestUseCase       = ingestUseCase;
        _ingestFolderUseCase = ingestFolderUseCase;
        _queryUseCase        = queryUseCase;
        _deleteUseCase       = deleteUseCase;
        _registry            = registry;
        _vectorStore         = vectorStore;
        _logger              = logger;
    }

    // ─── POST /api/documents/ingest/local ─────────────────────────────────────

    /// <summary>
    /// Ingest a single document from a local file path.
    /// </summary>
    [HttpPost("ingest/local")]
    public async Task<IActionResult> IngestLocal(
        [FromBody] IngestLocalRequest request, CancellationToken ct)
    {
        var source = new DocumentSource(
            SourceType: DocumentSourceType.LocalPath,
            Path:       request.Path,
            FileName:   Path.GetFileName(request.Path));

        var metadata = await _ingestUseCase.ExecuteAsync(source, ct);
        return Ok(metadata);
    }

    // ─── POST /api/documents/ingest/folder ───────────────────────────────────

    /// <summary>
    /// Smart folder ingestion with priority logic:
    ///
    /// Priority 1 — filesToIngest provided
    ///   → ingest only those specific files (extension matters)
    ///   → ["FileA.pdf", "FileC.pdf"] — FileA.txt is NOT ingested
    ///
    /// Priority 2 — fileTypes provided
    ///   → ingest all files of those types from the folder
    ///   → [".pdf"] or ["pdf"] — ingests all PDFs only
    ///
    /// Priority 3 — ingestAll = true
    ///   → ingest all supported files from folder and subfolders
    ///
    /// In all cases: already-ingested files are automatically skipped.
    /// </summary>
    [HttpPost("ingest/folder")]
    public async Task<IActionResult> IngestFolder(
        [FromBody] FolderIngestRequest request, CancellationToken ct)
    {
        var result = await _ingestFolderUseCase.ExecuteAsync(request, ct);

        // Validation error
        if (result.Error is not null && result.Mode == IngestMode.None)
            return BadRequest(new { error = result.Error });

        return Ok(new
        {
            folderPath     = result.FolderPath,
            mode           = result.Mode.ToString(),
            totalFound     = result.TotalFound,
            ingestedCount  = result.IngestedCount,
            skippedCount   = result.SkippedCount,
            failedCount    = result.FailedCount,
            notFoundCount  = result.NotFound.Count,
            processingTime = result.ProcessingTime.ToString(@"hh\:mm\:ss"),

            ingested = result.Ingested.Select(m => new
            {
                documentId  = m.DocumentId,
                fileName    = m.FileName,
                totalPages  = m.TotalPages,
                totalChunks = m.TotalChunks,
                ingestedAt  = m.IngestedAt
            }),

            skipped  = result.Skipped.Select(Path.GetFileName),
            notFound = result.NotFound,

            failed = result.Failed.Select(f => new
            {
                fileName = f.FileName,
                error    = f.Error
            }),

            message = result.Mode switch
            {
                IngestMode.SpecificFiles =>
                    $"Ingested {result.IngestedCount} of {result.TotalFound} requested files. " +
                    (result.NotFound.Count > 0
                        ? $"{result.NotFound.Count} not found in folder."
                        : "") +
                    (result.SkippedCount > 0
                        ? $" {result.SkippedCount} already ingested (skipped)."
                        : ""),

                IngestMode.FileTypes =>
                    $"Ingested {result.IngestedCount} file(s). " +
                    (result.SkippedCount > 0
                        ? $"{result.SkippedCount} already ingested (skipped)."
                        : ""),

                IngestMode.IngestAll =>
                    $"Ingested {result.IngestedCount} of {result.TotalFound} files. " +
                    (result.SkippedCount > 0
                        ? $"{result.SkippedCount} already ingested (skipped)."
                        : ""),
                _ => ""
            }
        });
    }

    // ─── POST /api/documents/ingest/blob ─────────────────────────────────────

    /// <summary>
    /// Ingest a document from Azure Blob Storage.
    /// </summary>
    [HttpPost("ingest/blob")]
    public async Task<IActionResult> IngestBlob(
        [FromBody] IngestBlobRequest request, CancellationToken ct)
    {
        var source = new DocumentSource(
            SourceType:    DocumentSourceType.AzureBlob,
            Path:          request.BlobPath,
            ContainerName: request.ContainerName,
            FileName:      Path.GetFileName(request.BlobPath));

        var metadata = await _ingestUseCase.ExecuteAsync(source, ct);
        return Ok(metadata);
    }

    // ─── POST /api/documents/ingest/onedrive ─────────────────────────────────

    /// <summary>
    /// Ingest a document from OneDrive (Phase 6).
    /// </summary>
    [HttpPost("ingest/onedrive")]
    public async Task<IActionResult> IngestOneDrive(
        [FromBody] IngestOneDriveRequest request, CancellationToken ct)
    {
        var source = new DocumentSource(
            SourceType: DocumentSourceType.OneDrive,
            Path:       request.ItemId,
            FileName:   request.FileName);

        var metadata = await _ingestUseCase.ExecuteAsync(source, ct);
        return Ok(metadata);
    }

    // ─── POST /api/documents/query ────────────────────────────────────────────

    /// <summary>
    /// Ask a question against ingested documents.
    /// Set fallbackToLLM=true to get AI answer when not found in documents.
    /// </summary>
    [HttpPost("query")]
    public async Task<IActionResult> Query(
        [FromBody] QueryRequest request, CancellationToken ct)
    {
        var query = new QAQuery(
            Question:         request.Question,
            UseGraph:         request.UseGraph,
            UseReranking:     request.UseReranking,
            FallbackToLLM:    request.FallbackToLLM,
            DocumentIdFilter: request.DocumentIdFilter);

        var result = await _queryUseCase.ExecuteAsync(query, ct);
        return Ok(result);
    }

    // ─── DELETE /api/documents/delete ────────────────────────────────────────

    /// <summary>
    /// Smart delete with priority logic:
    ///
    /// Priority 1 — filesToDelete provided
    ///   → delete only those specific files (extension matters)
    ///   → ["FileA.pdf", "FileC.pdf"] — FileA.txt is NOT deleted
    ///
    /// Priority 2 — fileTypes provided
    ///   → delete all files of those types
    ///   → [".pdf"] or ["pdf"] — deletes ALL PDFs
    ///
    /// Priority 3 — deleteAll = true
    ///   → delete everything
    ///
    /// Priority 4 — nothing provided
    ///   → validation error
    /// </summary>
    [HttpDelete("delete")]
    public async Task<IActionResult> DeleteDocuments(
        [FromBody] DeleteDocumentsRequest request, CancellationToken ct)
    {
        var deleteRequest = new DeleteRequest(
            FilesToDelete: request.FilesToDelete,
            FileTypes:     request.FileTypes,
            DeleteAll:     request.DeleteAll);

        var result = await _deleteUseCase.ExecuteAsync(deleteRequest, ct);

        if (!result.Success && result.Mode == DeleteMode.None)
            return BadRequest(new { error = result.Error });

        return Ok(new
        {
            success      = result.Success,
            mode         = result.Mode.ToString(),
            deletedCount = result.Deleted.Count,
            notFound     = result.NotFound,

            deleted = result.Deleted.Select(d => new
            {
                fileName   = d.FileName,
                documentId = d.DocumentId
            }),

            message = result.Mode switch
            {
                DeleteMode.SpecificFiles =>
                    $"{result.Deleted.Count} file(s) deleted." +
                    (result.NotFound.Count > 0
                        ? $" {result.NotFound.Count} not found: {string.Join(", ", result.NotFound)}"
                        : ""),
                DeleteMode.FileTypes =>
                    (result as DeleteByTypeResult)?.Message ?? "",
                DeleteMode.DeleteAll =>
                    $"All {result.Deleted.Count} document(s) deleted.",
                _ => ""
            }
        });
    }

    // ─── GET /api/documents/registry ─────────────────────────────────────────

    /// <summary>
    /// List all ingested documents from the registry.
    /// Shows fileName, extension, size, and ingest date.
    /// </summary>
    [HttpGet("registry")]
    public async Task<IActionResult> GetRegistry(CancellationToken ct)
    {
        var entries = await _registry.GetAllAsync(ct);

        return Ok(new
        {
            totalDocuments = entries.Count,
            documents = entries.Select(e => new
            {
                documentId = e.DocumentId,
                fileName   = e.FileName,
                extension  = Path.GetExtension(e.FileName).ToLower(),
                filePath   = e.FilePath,
                fileSizeMb = Math.Round(e.FileSizeBytes / 1024.0 / 1024.0, 2),
                ingestedAt = e.IngestedAt
            })
        });
    }

    // ─── GET /api/documents/health ────────────────────────────────────────────

    [HttpGet("health")]
    public IActionResult Health() =>
        Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

public record IngestLocalRequest(string Path);

public record IngestBlobRequest(string BlobPath, string ContainerName);

public record IngestOneDriveRequest(string ItemId, string FileName);

public record QueryRequest(
    string Question,
    bool UseGraph = false,
    bool UseReranking = false,
    bool FallbackToLLM = false,
    string? DocumentIdFilter = null);

public record DeleteDocumentsRequest(
    /// <summary>
    /// Priority 1: Specific file names including extension.
    /// ["FileA.pdf", "FileC.pdf"] — FileA.txt is NOT deleted.
    /// </summary>
    List<string>? FilesToDelete = null,

    /// <summary>
    /// Priority 2: File types to delete.
    /// [".pdf"] or ["pdf"] — deletes all PDFs.
    /// Used only when FilesToDelete is empty.
    /// </summary>
    List<string>? FileTypes = null,

    /// <summary>
    /// Priority 3: Delete everything.
    /// Used only when FilesToDelete and FileTypes are both empty.
    /// </summary>
    bool DeleteAll = false);
