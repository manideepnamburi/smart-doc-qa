using SmartDocQA.Domain.Enums;

namespace SmartDocQA.Domain.Models;

/// <summary>
/// Represents where a document lives — local, OneDrive, or Azure Blob.
/// Passed by the caller; infrastructure layer resolves it to a stream.
/// </summary>
public record DocumentSource(
    DocumentSourceType SourceType,
    string Path,                    // local path, blob URI, or OneDrive item ID
    string? ContainerName = null,   // Azure Blob only
    string? FileName = null         // override display name if needed
);