using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SmartDocQA.Infrastructure.Parsing;

// ─── PDF Parser ───────────────────────────────────────────────────────────────

public class PdfDocumentParser : IDocumentParser
{
    private readonly ILogger<PdfDocumentParser> _logger;

    public PdfDocumentParser(ILogger<PdfDocumentParser> logger) => _logger = logger;

    public bool CanParse(string fileName) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        _logger.LogInformation("Parsing PDF: {FileName}", fileName);

        // PdfPig works on seekable streams — buffer if needed
        using var pdf = PdfDocument.Open(stream);
        var pages = new List<ParsedPage>();

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var text = string.Join(" ", page.GetWords().Select(w => w.Text));
            var isScanned = string.IsNullOrWhiteSpace(text);

            pages.Add(new ParsedPage(
                PageNumber: page.Number,
                RawText: text,
                IsScanned: isScanned));
        }

        _logger.LogInformation("PDF parsed: {Pages} pages, {Scanned} scanned",
            pages.Count, pages.Count(p => p.IsScanned));

        return Task.FromResult(new ParsedDocument(fileName, pages, pages.Count));
    }
}

// ─── Word Document Parser ─────────────────────────────────────────────────────

public class WordDocumentParser : IDocumentParser
{
    private readonly ILogger<WordDocumentParser> _logger;

    public WordDocumentParser(ILogger<WordDocumentParser> logger) => _logger = logger;

    public bool CanParse(string fileName) =>
        fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".doc", StringComparison.OrdinalIgnoreCase);

    public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        _logger.LogInformation("Parsing Word document: {FileName}", fileName);

        // Buffer stream — OpenXml requires a seekable stream
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        using var wordDoc = WordprocessingDocument.Open(ms, isEditable: false);
        var body = wordDoc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Word document has no body");

        // Word docs don't have true page numbers — we simulate pages by
        // splitting on page break elements (w:lastRenderedPageBreak or w:br type=page)
        var pages = new List<ParsedPage>();
        var currentPageLines = new List<string>();
        var pageNumber = 1;

        foreach (var element in body.ChildElements)
        {
            ct.ThrowIfCancellationRequested();

            // Check for explicit page breaks
            var hasPageBreak = element.Descendants<Break>()
                .Any(b => b.Type?.Value == BreakValues.Page);

            var text = element.InnerText?.Trim();
            if (!string.IsNullOrEmpty(text))
                currentPageLines.Add(text);

            if (hasPageBreak)
            {
                pages.Add(new ParsedPage(pageNumber++, string.Join("\n", currentPageLines), false));
                currentPageLines.Clear();
            }
        }

        // Last page
        if (currentPageLines.Count > 0)
            pages.Add(new ParsedPage(pageNumber, string.Join("\n", currentPageLines), false));

        // Ensure at least one page
        if (pages.Count == 0)
            pages.Add(new ParsedPage(1, body.InnerText ?? string.Empty, false));

        _logger.LogInformation("Word document parsed: {Pages} simulated pages", pages.Count);
        return Task.FromResult(new ParsedDocument(fileName, pages, pages.Count));
    }
}
