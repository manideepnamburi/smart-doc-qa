namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Tracks which files have already been ingested.
/// Prevents duplicate ingestion when a folder is re-scanned.
/// </summary>
public interface IDocumentRegistry
{
    /// <summary>Check if a file path has already been ingested.</summary>
    Task<bool> IsIngestedAsync(string filePath, CancellationToken ct = default);

    /// <summary>Record a file as successfully ingested.</summary>
    Task RegisterAsync(string filePath, string documentId, string fileName,
        long fileSizeBytes, CancellationToken ct = default);

    /// <summary>Get all registered documents.</summary>
    Task<List<RegistryEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Remove a single document from the registry.</summary>
    Task UnregisterAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Clear ALL records from the registry.
    /// Used during full system reset.
    /// </summary>
    Task ClearAllAsync(CancellationToken ct = default);
}

public record RegistryEntry(
    string FilePath,
    string DocumentId,
    string FileName,
    long FileSizeBytes,
    DateTime IngestedAt
);
