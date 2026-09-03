using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

// ─── Azure Doc Intelligence Table Extractor (Phase 4 — Real) ─────────────────

public class AzureDocIntelligenceTableExtractor : ITableExtractor
{
    private readonly AzureDocIntelligenceOptions _options;
    private readonly ILogger<AzureDocIntelligenceTableExtractor> _logger;
    private DocumentAnalysisClient? _client;

    public AzureDocIntelligenceTableExtractor(
        IOptions<AzureDocIntelligenceOptions> options,
        ILogger<AzureDocIntelligenceTableExtractor> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    private DocumentAnalysisClient GetClient() =>
        _client ??= new DocumentAnalysisClient(
            new Uri(_options.Endpoint),
            new AzureKeyCredential(_options.ApiKey));

    public async Task<List<ExtractedTable>> ExtractTablesAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return new List<ExtractedTable>();

        try
        {
            _logger.LogInformation(
                "Azure Doc Intelligence: analyzing {File} for tables", fileName);

            if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);

            var operation = await GetClient().AnalyzeDocumentAsync(
                WaitUntil.Completed,
                "prebuilt-layout",
                stream,
                cancellationToken: ct);

            var result = operation.Value;
            var tables = new List<ExtractedTable>();

            _logger.LogInformation(
                "Azure Doc Intelligence: found {Count} tables in {File}",
                result.Tables.Count, fileName);

            foreach (var table in result.Tables)
            {
                var markdown  = ConvertToMarkdown(table);
                var firstRegion = table.BoundingRegions.FirstOrDefault();
                var pageNumber = firstRegion.PageNumber;

                // Proximity linking — find any paragraph on this page to use as caption
                var caption = result.Paragraphs
                    .Where(p => p.BoundingRegions.Any(r => r.PageNumber == pageNumber))
                    .OrderByDescending(p => p.Content.Length)
                    .FirstOrDefault()?.Content ?? string.Empty;

                // Truncate caption to first 200 chars — just enough for context
                if (caption.Length > 200)
                    caption = caption[..200] + "...";

                tables.Add(new ExtractedTable(
                    PageNumber:      pageNumber,
                    MarkdownContent: markdown,
                    Caption:         caption));

                _logger.LogDebug(
                    "Extracted table {Rows}×{Cols} on page {Page}",
                    table.RowCount, table.ColumnCount, pageNumber);
            }

            return tables;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Azure Doc Intelligence failed for {File} — skipping table extraction",
                fileName);
            return new List<ExtractedTable>();
        }
    }

    private static string ConvertToMarkdown(DocumentTable table)
    {
        // Build a 2D grid — handles merged cells gracefully
        var grid = new string[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
            grid[cell.RowIndex, cell.ColumnIndex] = cell.Content.Trim();

        var sb = new System.Text.StringBuilder();

        for (int row = 0; row < table.RowCount; row++)
        {
            sb.Append("| ");
            for (int col = 0; col < table.ColumnCount; col++)
            {
                sb.Append(grid[row, col] ?? "");
                sb.Append(" | ");
            }
            sb.AppendLine();

            // Separator after header row
            if (row == 0)
            {
                sb.Append("| ");
                for (int col = 0; col < table.ColumnCount; col++)
                    sb.Append("--- | ");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}