using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

/// <summary>No-op stand-in for ITableExtractor, used when table extraction is disabled.</summary>
public class NoOpTableExtractor : ITableExtractor
{
    public Task<List<ExtractedTable>> ExtractTablesAsync(
        Stream stream, string fileName, CancellationToken ct = default)
        => Task.FromResult(new List<ExtractedTable>());
}