namespace SmartDocQA.Domain.Models;

/// <summary>
/// A chart, diagram, or scanned-text page identified by Claude Vision
/// during ingestion (see IChartExtractor). Description is the model's
/// text summary of the visual content -- what gets embedded and retrieved
/// like any other chunk -- while ImageBytes preserves the original
/// rendered page image for reference.
/// </summary>
public record ExtractedChart(int PageNumber, string Description, byte[] ImageBytes);