using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Extracts tables from a document using Azure Document Intelligence.
/// Optional — disabled via config flag if not needed.
/// </summary>
public interface ITableExtractor
{
    Task<List<ExtractedTable>> ExtractTablesAsync(Stream stream, string fileName, CancellationToken ct = default);
}