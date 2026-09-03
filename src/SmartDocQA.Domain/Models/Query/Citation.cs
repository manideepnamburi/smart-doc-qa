using SmartDocQA.Domain.Enums;

namespace SmartDocQA.Domain.Models;

/// <summary>
/// A single source reference attached to a synthesized answer — enough
/// for the caller (UI or API consumer) to show the user exactly which
/// chunk, page, and file a cited fact came from, plus a short excerpt for
/// context without needing to fetch the full chunk separately.
/// </summary>
public record Citation(
    string ChunkId,
    string FileName,
    int PageNumber,
    ChunkType ChunkType,
    string RelevantExcerpt    // short snippet shown to user
);
