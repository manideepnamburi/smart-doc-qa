using Microsoft.Extensions.Logging;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Application.UseCases;

/// <summary>
/// DeleteDocumentsUseCase — the single orchestrator for removing ingested
/// documents from every store they were written to.
///
/// PRIORITY LOGIC (mirrors the same pattern used by IngestFolderUseCase,
/// for a consistent, predictable API shape across ingest AND delete):
///
///   Priority 1 — FilesToDelete list provided -> delete ONLY those specific
///                files (exact FileName match, extension-sensitive —
///                "FileA.pdf" and "FileA.txt" are different documents and
///                deleting one never touches the other)
///   Priority 2 — FileTypes provided          -> delete every file whose
///                extension matches (only checked if FilesToDelete was
///                empty)
///   Priority 3 — DeleteAll = true            -> wipe everything (only
///                checked if both of the above were empty)
///   Priority 4 — nothing provided            -> return a validation error
///                rather than silently doing nothing
///
/// WHY DELETION MUST TOUCH **FOUR** STORES, NOT THREE:
/// A single ingested document leaves a footprint in FOUR separate places,
/// and every one of them must be cleaned up or stale/orphaned data is left
/// behind that can surface in future searches or waste storage forever:
///   1. Qdrant       (dense vector storage — semantic search)
///   2. BM25 index   (SQLite-backed keyword search — Phase 2)
///   3. Registry     (SQLite — prevents duplicate-ingestion detection)
///   4. Neo4j graph  (entities + relationships extracted from this
///                    document's chunks — Phase 5)
/// The graph store is the most recently added of the four (Phase 5) and is
/// easy to forget precisely BECAUSE it wasn't part of the original Phase 1
/// delete design — this class exists specifically to make sure that gap
/// doesn't cause silently orphaned graph nodes after a document is deleted.
/// </summary>
public class DeleteDocumentsUseCase
{
    private readonly IDocumentRegistry _registry;
    private readonly IVectorStore _vectorStore;
    private readonly IKeywordIndex _keywordIndex;
    private readonly IGraphStore _graphStore;
    private readonly ILogger<DeleteDocumentsUseCase> _logger;

    public DeleteDocumentsUseCase(
        IDocumentRegistry registry,
        IVectorStore vectorStore,
        IKeywordIndex keywordIndex,
        IGraphStore graphStore,
        ILogger<DeleteDocumentsUseCase> logger)
    {
        _registry = registry;
        _vectorStore = vectorStore;
        _keywordIndex = keywordIndex;
        _graphStore = graphStore;
        _logger = logger;
    }

    /// <summary>
    /// Entry point — inspects which criteria were provided on the request
    /// and routes to the matching priority handler below. This method
    /// itself does no deletion; it only decides WHICH mode applies and
    /// validates that at least one valid mode was actually requested.
    /// </summary>
    public async Task<DeleteResult> ExecuteAsync(
        DeleteRequest request, CancellationToken ct = default)
    {
        bool hasFileList = request.FilesToDelete?.Count > 0;
        bool hasFileTypes = request.FileTypes?.Count > 0;
        bool hasDeleteAll = request.DeleteAll;

        // Guard clause — an empty request is almost always a caller mistake
        // (forgot to set a field), so we return an explicit error instead
        // of silently treating it as "delete nothing" or "delete everything".
        if (!hasFileList && !hasFileTypes && !hasDeleteAll)
        {
            return new DeleteResult(
                Success: false,
                Mode: DeleteMode.None,
                Deleted: new List<DeletedEntry>(),
                NotFound: new List<string>(),
                Error: "No deletion criteria provided. " +
                          "Provide filesToDelete, fileTypes, or set deleteAll=true.");
        }

        // The registry is the single source of truth for "what documents
        // do we currently have" — every mode below needs this list to
        // resolve file names/types into actual DocumentIds before it can
        // clean up the other three stores (which are keyed by DocumentId,
        // not FileName).
        var allEntries = await _registry.GetAllAsync(ct);

        if (hasFileList)
        {
            _logger.LogInformation(
                "Delete mode: SpecificFiles | Count={Count}",
                request.FilesToDelete!.Count);

            return await DeleteByFileNamesAsync(
                request.FilesToDelete!, allEntries, ct);
        }

        if (hasFileTypes)
        {
            // Normalize so callers can pass either "pdf" or ".pdf" —
            // one less footgun for whoever calls this API.
            var normalizedTypes = request.FileTypes!
                .Select(t => t.StartsWith('.') ? t.ToLower() : $".{t.ToLower()}")
                .ToList();

            _logger.LogInformation(
                "Delete mode: FileTypes | Types={Types}",
                string.Join(", ", normalizedTypes));

            return await DeleteByFileTypesAsync(normalizedTypes, allEntries, ct);
        }

        _logger.LogWarning("Delete mode: DeleteAll — removing all documents");
        return await DeleteAllAsync(allEntries, ct);
    }

    /// <summary>
    /// Priority 1 handler — deletes only the exact files named in the
    /// request. Files not found in the registry are reported back in
    /// NotFound rather than silently ignored, so a caller who mistyped
    /// a file name finds out immediately instead of assuming it worked.
    /// </summary>
    private async Task<DeleteResult> DeleteByFileNamesAsync(
        List<string> fileNames,
        List<RegistryEntry> allEntries,
        CancellationToken ct)
    {
        var deleted = new List<DeletedEntry>();
        var notFound = new List<string>();

        foreach (var fileName in fileNames)
        {
            var match = allEntries.FirstOrDefault(e =>
                string.Equals(e.FileName, fileName,
                    StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _logger.LogWarning("File not found in registry: {File}", fileName);
                notFound.Add(fileName);
                continue;
            }

            await DeleteEntryAsync(match, ct);

            deleted.Add(new DeletedEntry(
                FileName: match.FileName,
                DocumentId: match.DocumentId,
                FilePath: match.FilePath));

            _logger.LogInformation("Deleted: {File}", match.FileName);
        }

        return new DeleteResult(
            Success: true,
            Mode: DeleteMode.SpecificFiles,
            Deleted: deleted,
            NotFound: notFound,
            Error: null);
    }

    /// <summary>
    /// Priority 2 handler — deletes every registered document whose file
    /// extension matches one of the requested types. Uses the same
    /// per-document DeleteEntryAsync helper as the specific-files path,
    /// so both modes get identical four-store cleanup guarantees.
    /// </summary>
    private async Task<DeleteByTypeResult> DeleteByFileTypesAsync(
        List<string> fileTypes,
        List<RegistryEntry> allEntries,
        CancellationToken ct)
    {
        var matches = allEntries
            .Where(e => fileTypes.Contains(
                Path.GetExtension(e.FileName).ToLower()))
            .ToList();

        if (matches.Count == 0)
        {
            return new DeleteByTypeResult(
                Success: true,
                Mode: DeleteMode.FileTypes,
                FileTypes: fileTypes,
                Deleted: new List<DeletedEntry>(),
                Error: null,
                Message: $"No files found with types: {string.Join(", ", fileTypes)}");
        }

        var deleted = new List<DeletedEntry>();

        foreach (var entry in matches)
        {
            await DeleteEntryAsync(entry, ct);

            deleted.Add(new DeletedEntry(
                FileName: entry.FileName,
                DocumentId: entry.DocumentId,
                FilePath: entry.FilePath));

            _logger.LogInformation(
                "Deleted ({Type}): {File}",
                Path.GetExtension(entry.FileName), entry.FileName);
        }

        return new DeleteByTypeResult(
            Success: true,
            Mode: DeleteMode.FileTypes,
            FileTypes: fileTypes,
            Deleted: deleted,
            Error: null,
            Message: $"Deleted {deleted.Count} file(s) of type(s): {string.Join(", ", fileTypes)}");
    }

    /// <summary>
    /// Priority 3 handler — wipes every document from all four stores.
    ///
    /// WHY QDRANT / BM25 / REGISTRY USE A DEDICATED "CLEAR EVERYTHING"
    /// CALL INSTEAD OF LOOPING PER DOCUMENT:
    /// Those three stores each expose a purpose-built bulk operation
    /// (ResetCollectionAsync, ClearAllAsync, ClearAllAsync) that drops and
    /// recreates the underlying storage in one operation — this is both
    /// faster than N individual per-document deletes AND safer, since it
    /// guarantees nothing is left behind even if the registry's list of
    /// "known documents" were ever slightly out of sync with what's
    /// actually stored.
    ///
    /// WHY THE GRAPH STORE IS DIFFERENT — LOOPED PER DOCUMENT INSTEAD:
    /// IGraphStore currently only exposes DeleteByDocumentAsync (delete
    /// the footprint of ONE document), not an equivalent bulk "wipe the
    /// whole graph" operation. This is because Neo4jGraphStore.
    /// DeleteByDocumentAsync is intentionally careful — it only removes an
    /// entity/relationship once ITS LAST referencing document has been
    /// removed (an entity like "CDC" is expected to be shared across many
    /// documents, see Neo4jGraphStore comments), so a blunt "drop
    /// everything" operation would need to bypass that safety logic
    /// entirely. Looping per document here reuses the existing, already
    /// correct, already-tested per-document cleanup logic rather than
    /// introducing a second, less careful deletion path into Neo4jGraphStore.
    /// A dedicated bulk-reset method is a reasonable future addition if
    /// full-wipe performance ever becomes a bottleneck at larger scale.
    /// </summary>
    private async Task<DeleteResult> DeleteAllAsync(
        List<RegistryEntry> allEntries, CancellationToken ct)
    {
        var deleted = new List<DeletedEntry>();

        foreach (var entry in allEntries)
        {
            deleted.Add(new DeletedEntry(
                FileName: entry.FileName,
                DocumentId: entry.DocumentId,
                FilePath: entry.FilePath));
        }

        // Bulk-clear the three stores that support it — fast, guaranteed-clean.
        await _vectorStore.ResetCollectionAsync(ct);   // Qdrant — drop + recreate
        await _keywordIndex.ClearAllAsync(ct);         // BM25  — wipe index.json
        await _registry.ClearAllAsync(ct);             // SQLite — clear registry

        // Graph cleanup — one call per document, using the same safe,
        // reference-counted deletion logic as the other two delete modes
        // (see class-level summary and DeleteEntryAsync for why this
        // can't just be a single "drop the whole graph" call).
        // Non-fatal per document: one failed graph cleanup should never
        // prevent the rest of the documents' graph data from being removed.
        foreach (var entry in allEntries)
        {
            try
            {
                await _graphStore.DeleteByDocumentAsync(entry.DocumentId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Graph cleanup failed for document {DocId} ({File}) during DeleteAll — continuing",
                    entry.DocumentId, entry.FileName);
            }
        }

        _logger.LogWarning(
            "DeleteAll complete — {Count} documents removed from " +
            "Qdrant, BM25 index, registry, and graph",
            deleted.Count);

        return new DeleteResult(
            Success: true,
            Mode: DeleteMode.DeleteAll,
            Deleted: deleted,
            NotFound: new List<string>(),
            Error: null);
    }

    /// <summary>
    /// Shared per-document cleanup helper used by BOTH the SpecificFiles
    /// and FileTypes delete paths (DeleteAllAsync has its own bulk-first,
    /// graph-looped approach above and does not call this helper).
    ///
    /// Runs all four store deletions for ONE document. The graph deletion
    /// is wrapped in its own try/catch — non-fatal by the same design
    /// principle used throughout this project's extraction pipeline
    /// (Phase 4/5): a failure in one store's cleanup should never prevent
    /// the other stores from being cleaned up, and should never abort
    /// deletion of the remaining files in a multi-file request.
    /// </summary>
    private async Task DeleteEntryAsync(RegistryEntry entry, CancellationToken ct)
    {
        // Remove from Qdrant (dense vectors)
        await _vectorStore.DeleteByDocumentAsync(entry.DocumentId, ct);

        // Remove from BM25 index (keyword search)
        await _keywordIndex.DeleteByDocumentAsync(entry.DocumentId, ct);

        // Remove from Neo4j graph — entities/relationships whose ONLY
        // remaining reference was this document get fully removed;
        // entities shared with other still-existing documents are kept,
        // just with this document's ID stripped from their reference list
        // (see Neo4jGraphStore.DeleteByDocumentAsync for the exact logic).
        try
        {
            await _graphStore.DeleteByDocumentAsync(entry.DocumentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Graph cleanup failed for document {DocId} ({File}) — continuing",
                entry.DocumentId, entry.FileName);
        }

        // Remove from SQLite registry LAST — once this runs, the document
        // is no longer considered "known" by the system, so every other
        // cleanup above must have already been attempted before this point.
        await _registry.UnregisterAsync(entry.DocumentId, ct);
    }
}

/// <summary>Incoming delete request — see class-level priority rules above.</summary>
public record DeleteRequest(
    List<string>? FilesToDelete,
    List<string>? FileTypes,
    bool DeleteAll = false
);

/// <summary>Which priority branch actually handled a given delete request.</summary>
public enum DeleteMode
{
    None,
    SpecificFiles,
    FileTypes,
    DeleteAll
}

/// <summary>One document that was successfully removed from all stores.</summary>
public record DeletedEntry(
    string FileName,
    string DocumentId,
    string FilePath);

/// <summary>Result returned by the SpecificFiles and DeleteAll modes.</summary>
public record DeleteResult(
    bool Success,
    DeleteMode Mode,
    List<DeletedEntry> Deleted,
    List<string> NotFound,
    string? Error);

/// <summary>
/// Result returned by the FileTypes mode — extends DeleteResult with the
/// specific types that were matched and a human-readable summary Message.
/// </summary>
public record DeleteByTypeResult(
    bool Success,
    DeleteMode Mode,
    List<string> FileTypes,
    List<DeletedEntry> Deleted,
    string? Error,
    string? Message) : DeleteResult(
        Success,
        Mode,
        Deleted,
        new List<string>(),
        Error);
