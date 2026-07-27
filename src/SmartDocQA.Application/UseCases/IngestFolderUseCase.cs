using Microsoft.Extensions.Logging;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Application.UseCases;

/// <summary>
/// Smart folder ingestion with priority logic — mirrors delete priority pattern.
///
/// Priority 1 — filesToIngest list provided
///   → ingest only those specific files from the folder
///   → extension matters: FileA.txt and FileA.pdf are different
///
/// Priority 2 — fileTypes provided
///   → ingest all files of those types from the folder
///   → example: [".pdf", ".docx"] ingests only PDFs and Word docs
///
/// Priority 3 — ingestAll = true
///   → ingest all supported files from folder and subfolders
///
/// Priority 4 — nothing provided
///   → validation error
///
/// In all cases: already-ingested files are skipped (registry check).
/// </summary>
public class IngestFolderUseCase
{
    private readonly IFolderScanner _folderScanner;
    private readonly IDocumentRegistry _registry;
    private readonly IngestDocumentUseCase _ingestDocumentUseCase;
    private readonly ILogger<IngestFolderUseCase> _logger;

    public IngestFolderUseCase(
        IFolderScanner folderScanner,
        IDocumentRegistry registry,
        IngestDocumentUseCase ingestDocumentUseCase,
        ILogger<IngestFolderUseCase> logger)
    {
        _folderScanner         = folderScanner;
        _registry              = registry;
        _ingestDocumentUseCase = ingestDocumentUseCase;
        _logger                = logger;
    }

    public async Task<FolderIngestResult> ExecuteAsync(
        FolderIngestRequest request, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Validate ──────────────────────────────────────────────────────────
        bool hasFileList  = request.FilesToIngest?.Count > 0;
        bool hasFileTypes = request.FileTypes?.Count > 0;
        bool hasIngestAll = request.IngestAll;

        if (!hasFileList && !hasFileTypes && !hasIngestAll)
        {
            return new FolderIngestResult(
                FolderPath:     request.FolderPath,
                Mode:           IngestMode.None,
                TotalFound:     0,
                Ingested:       new List<DocumentMetadata>(),
                Skipped:        new List<string>(),
                Failed:         new List<FailedFile>(),
                NotFound:       new List<string>(),
                ProcessingTime: sw.Elapsed,
                Error: "No ingestion criteria provided. " +
                       "Provide filesToIngest, fileTypes, or set ingestAll=true.");
        }

        if (!Directory.Exists(request.FolderPath))
        {
            return new FolderIngestResult(
                FolderPath:     request.FolderPath,
                Mode:           IngestMode.None,
                TotalFound:     0,
                Ingested:       new List<DocumentMetadata>(),
                Skipped:        new List<string>(),
                Failed:         new List<FailedFile>(),
                NotFound:       new List<string>(),
                ProcessingTime: sw.Elapsed,
                Error: $"Folder not found: {request.FolderPath}");
        }

        // ── Priority 1: Specific file list ────────────────────────────────────
        if (hasFileList)
        {
            _logger.LogInformation(
                "Ingest mode: SpecificFiles | Count={Count} | Folder={Folder}",
                request.FilesToIngest!.Count, request.FolderPath);

            return await IngestSpecificFilesAsync(
                request.FolderPath, request.FilesToIngest!, sw, ct);
        }

        // ── Priority 2: File types ────────────────────────────────────────────
        if (hasFileTypes)
        {
            var normalizedTypes = request.FileTypes!
                .Select(t => t.StartsWith('.') ? t.ToLower() : $".{t.ToLower()}")
                .ToList();

            _logger.LogInformation(
                "Ingest mode: FileTypes | Types={Types} | Folder={Folder}",
                string.Join(", ", normalizedTypes), request.FolderPath);

            return await IngestByFileTypesAsync(
                request.FolderPath, normalizedTypes, sw, ct);
        }

        // ── Priority 3: Ingest all ────────────────────────────────────────────
        _logger.LogInformation(
            "Ingest mode: IngestAll | Folder={Folder}", request.FolderPath);

        return await IngestAllAsync(request.FolderPath, sw, ct);
    }

    // ── Priority 1: Ingest specific named files ───────────────────────────────

    private async Task<FolderIngestResult> IngestSpecificFilesAsync(
        string folderPath,
        List<string> fileNames,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        // Scan folder to find full paths for requested files
        var allFiles = _folderScanner.Scan(folderPath);

        var ingested = new List<DocumentMetadata>();
        var skipped  = new List<string>();
        var failed   = new List<FailedFile>();
        var notFound = new List<string>();

        foreach (var fileName in fileNames)
        {
            ct.ThrowIfCancellationRequested();

            // Match by exact file name including extension (case-insensitive)
            var matchedPath = allFiles.FirstOrDefault(f =>
                string.Equals(
                    Path.GetFileName(f), fileName,
                    StringComparison.OrdinalIgnoreCase));

            if (matchedPath is null)
            {
                _logger.LogWarning(
                    "File not found in folder: {File}", fileName);
                notFound.Add(fileName);
                continue;
            }

            await ProcessFileAsync(
                matchedPath, ingested, skipped, failed, ct);
        }

        sw.Stop();
        LogSummary(IngestMode.SpecificFiles, ingested, skipped, failed, sw);

        return new FolderIngestResult(
            FolderPath:     folderPath,
            Mode:           IngestMode.SpecificFiles,
            TotalFound:     fileNames.Count,
            Ingested:       ingested,
            Skipped:        skipped,
            Failed:         failed,
            NotFound:       notFound,
            ProcessingTime: sw.Elapsed,
            Error:          null);
    }

    // ── Priority 2: Ingest by file type ──────────────────────────────────────

    private async Task<FolderIngestResult> IngestByFileTypesAsync(
        string folderPath,
        List<string> fileTypes,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        // Get all files then filter by requested extensions
        var allFiles = _folderScanner.Scan(folderPath);

        var matchedFiles = allFiles
            .Where(f => fileTypes.Contains(
                Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        _logger.LogInformation(
            "Found {Count} files of types [{Types}] in {Folder}",
            matchedFiles.Count, string.Join(", ", fileTypes), folderPath);

        var ingested = new List<DocumentMetadata>();
        var skipped  = new List<string>();
        var failed   = new List<FailedFile>();

        foreach (var filePath in matchedFiles)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessFileAsync(filePath, ingested, skipped, failed, ct);
        }

        sw.Stop();
        LogSummary(IngestMode.FileTypes, ingested, skipped, failed, sw);

        return new FolderIngestResult(
            FolderPath:     folderPath,
            Mode:           IngestMode.FileTypes,
            TotalFound:     matchedFiles.Count,
            Ingested:       ingested,
            Skipped:        skipped,
            Failed:         failed,
            NotFound:       new List<string>(),
            ProcessingTime: sw.Elapsed,
            Error:          null);
    }

    // ── Priority 3: Ingest all supported files ────────────────────────────────

    private async Task<FolderIngestResult> IngestAllAsync(
        string folderPath,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        var allFiles = _folderScanner.Scan(folderPath);

        if (allFiles.Count == 0)
        {
            _logger.LogWarning("No supported files found in {Folder}", folderPath);
            sw.Stop();
            return new FolderIngestResult(
                FolderPath:     folderPath,
                Mode:           IngestMode.IngestAll,
                TotalFound:     0,
                Ingested:       new List<DocumentMetadata>(),
                Skipped:        new List<string>(),
                Failed:         new List<FailedFile>(),
                NotFound:       new List<string>(),
                ProcessingTime: sw.Elapsed,
                Error:          null);
        }

        var ingested = new List<DocumentMetadata>();
        var skipped  = new List<string>();
        var failed   = new List<FailedFile>();

        foreach (var filePath in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessFileAsync(filePath, ingested, skipped, failed, ct);
        }

        sw.Stop();
        LogSummary(IngestMode.IngestAll, ingested, skipped, failed, sw);

        return new FolderIngestResult(
            FolderPath:     folderPath,
            Mode:           IngestMode.IngestAll,
            TotalFound:     allFiles.Count,
            Ingested:       ingested,
            Skipped:        skipped,
            Failed:         failed,
            NotFound:       new List<string>(),
            ProcessingTime: sw.Elapsed,
            Error:          null);
    }

    // ── Shared file processor ─────────────────────────────────────────────────

    private async Task ProcessFileAsync(
        string filePath,
        List<DocumentMetadata> ingested,
        List<string> skipped,
        List<FailedFile> failed,
        CancellationToken ct)
    {
        try
        {
            // Check registry — skip if already ingested
            var alreadyIngested = await _registry.IsIngestedAsync(filePath, ct);
            if (alreadyIngested)
            {
                _logger.LogDebug(
                    "Skipping (already ingested): {File}",
                    Path.GetFileName(filePath));
                skipped.Add(filePath);
                return;
            }

            _logger.LogInformation(
                "Ingesting: {File}", Path.GetFileName(filePath));

            var source = new DocumentSource(
                SourceType: DocumentSourceType.LocalPath,
                Path:       filePath,
                FileName:   Path.GetFileName(filePath));

            var metadata = await _ingestDocumentUseCase.ExecuteAsync(source, ct);

            // Register in SQLite
            var fileInfo = new FileInfo(filePath);
            await _registry.RegisterAsync(
                filePath:      filePath,
                documentId:    metadata.DocumentId,
                fileName:      metadata.FileName,
                fileSizeBytes: fileInfo.Length,
                ct:            ct);

            ingested.Add(metadata);

            _logger.LogInformation(
                "✅ Ingested: {File} → {Pages} pages, {Chunks} chunks",
                metadata.FileName, metadata.TotalPages, metadata.TotalChunks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "❌ Failed: {File}", Path.GetFileName(filePath));
            failed.Add(new FailedFile(
                filePath, Path.GetFileName(filePath), ex.Message));
        }
    }

    // ── Logging helper ────────────────────────────────────────────────────────

    private void LogSummary(
        IngestMode mode,
        List<DocumentMetadata> ingested,
        List<string> skipped,
        List<FailedFile> failed,
        System.Diagnostics.Stopwatch sw)
    {
        _logger.LogInformation(
            "Folder ingestion complete | Mode={Mode} | " +
            "Ingested={I} Skipped={S} Failed={F} | {T}ms",
            mode, ingested.Count, skipped.Count,
            failed.Count, sw.ElapsedMilliseconds);
    }
}

// ─── Request ──────────────────────────────────────────────────────────────────

public record FolderIngestRequest(
    /// <summary>Folder path to scan. Required for all modes.</summary>
    string FolderPath,

    /// <summary>
    /// Priority 1: Specific file names to ingest (including extension).
    /// Example: ["FileA.pdf", "FileC.pdf"]
    /// FileA.txt is NOT ingested — extension matters.
    /// </summary>
    List<string>? FilesToIngest = null,

    /// <summary>
    /// Priority 2: File types to ingest from the folder.
    /// Example: [".pdf"] or ["pdf"]
    /// Used only when FilesToIngest is empty.
    /// </summary>
    List<string>? FileTypes = null,

    /// <summary>
    /// Priority 3: Ingest all supported files from folder and subfolders.
    /// Used only when FilesToIngest and FileTypes are both empty.
    /// </summary>
    bool IngestAll = false
);

// ─── Result ───────────────────────────────────────────────────────────────────

public enum IngestMode
{
    None,
    SpecificFiles,
    FileTypes,
    IngestAll
}

public record FolderIngestResult(
    string FolderPath,
    IngestMode Mode,
    int TotalFound,
    List<DocumentMetadata> Ingested,
    List<string> Skipped,
    List<FailedFile> Failed,
    List<string> NotFound,
    TimeSpan ProcessingTime,
    string? Error)
{
    public int IngestedCount => Ingested.Count;
    public int SkippedCount  => Skipped.Count;
    public int FailedCount   => Failed.Count;
}

public record FailedFile(string FilePath, string FileName, string Error);
