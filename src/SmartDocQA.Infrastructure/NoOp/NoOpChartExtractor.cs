using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

/// <summary>No-op stand-in for IChartExtractor, used when chart/vision extraction is disabled.</summary>
public class NoOpChartExtractor : IChartExtractor
{
    public Task<List<ExtractedChart>> ExtractChartsAsync(
        Stream stream, string fileName, CancellationToken ct = default)
        => Task.FromResult(new List<ExtractedChart>());
}