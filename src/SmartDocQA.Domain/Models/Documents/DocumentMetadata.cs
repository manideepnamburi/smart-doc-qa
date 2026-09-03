using SmartDocQA.Domain.Enums;

namespace SmartDocQA.Domain.Models;

public record DocumentMetadata(
    string DocumentId,
    string FileName,
    string SourcePath,
    DocumentSourceType SourceType,
    DateTime IngestedAt,
    int TotalPages,
    int TotalChunks
);
