using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Extracts embedded images/charts from a document and summarises them via Claude Vision.
/// Optional — disabled via config flag.
/// </summary>
public interface IChartExtractor
{
    Task<List<ExtractedChart>> ExtractChartsAsync(Stream stream, string fileName, CancellationToken ct = default);
}