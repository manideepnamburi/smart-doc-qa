using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using System.Collections.Concurrent;

namespace SmartDocQA.Infrastructure;

// ─── Claude Vision Chart Extractor (Phase 4b) ────────────────────────────────
//
// Renders each PDF page to PNG (PDFtoImage/PDFium), sends it to Claude Vision,
// and captures a text description for pages containing charts/diagrams or
// scanned text. One mechanism covers BOTH chart description AND OCR.

public class ClaudeVisionChartExtractor : IChartExtractor
{
    private readonly ILlmClient _llmClient;
    private readonly VisionExtractionOptions _options;
    private readonly ILogger<ClaudeVisionChartExtractor> _logger;

    private const string SystemPrompt =
        "You are a document analysis assistant. You describe visual content " +
        "from document pages accurately and concisely for use in a search index.";
    private const string UserPrompt =
        "Analyze this document page image.\n" +
        "1. If it contains a chart, graph, or diagram: describe the data it shows — " +
        "axis labels, categories, values, and the key trend or takeaway.\n" +
        "2. If it is a scanned page of text (an image of text): transcribe the text.\n" +
        "3. If it contains neither (plain text page, decorative images only, or blank): " +
        "reply with exactly NONE.\n" +
        "Reply with only the description, the transcription, or NONE.";

    public ClaudeVisionChartExtractor(
        ILlmClient llmClient,
        IOptions<VisionExtractionOptions> options,
        ILogger<ClaudeVisionChartExtractor> logger)
    {
        _llmClient = llmClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<ExtractedChart>> ExtractChartsAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return new List<ExtractedChart>();

        byte[] pdfBytes;
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms, ct);
            pdfBytes = ms.ToArray();
        }

        var pageCount = Conversion.GetPageCount(pdfBytes);
        _logger.LogInformation(
            "Claude Vision: rendering {Pages} pages of {File} for chart/OCR analysis",
            pageCount, fileName);

        // ── Step 1: Render all pages to PNG, SEQUENTIALLY ──────────────────
        // PDFium (via PDFtoImage) isn't guaranteed thread-safe for concurrent
        // renders against one document buffer, and rendering is fast/local
        // anyway (no network wait) -- nothing to gain from parallelizing this
        // part. The real bottleneck, and the only part worth parallelizing,
        // is the network round-trip to Claude in Step 2 below.
        var renderedPages = new List<(int PageIndex, byte[] PngBytes)>();
        for (int i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var bitmap = Conversion.ToImage(pdfBytes, page: i,
                options: new RenderOptions(Dpi: 120));
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 85);
            renderedPages.Add((i, encoded.ToArray()));
        }

        // ── Step 2: Analyze pages CONCURRENTLY, capped by MaxConcurrency ───
        // The actual fix: instead of each page waiting on the prior page's
        // full network round-trip before starting, up to MaxConcurrency
        // pages are in-flight to the Vision API at once. SemaphoreSlim caps
        // how many -- tune _options.MaxConcurrency in appsettings.json
        // against your account's real RPM, not by guessing.
        var semaphore = new SemaphoreSlim(_options.MaxConcurrency);
        var results = new ConcurrentBag<ExtractedChart>();

        var tasks = renderedPages.Select(async page =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var description = await _llmClient.CompleteWithVisionAsync(
                    SystemPrompt, UserPrompt, page.PngBytes, ct);

                if (string.IsNullOrWhiteSpace(description) ||
                    description.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Page {Page}: no visual content", page.PageIndex + 1);
                    return;
                }

                results.Add(new ExtractedChart(
                    PageNumber: page.PageIndex + 1,
                    Description: description.Trim(),
                    ImageBytes: page.PngBytes));

                _logger.LogInformation(
                    "Page {Page}: visual content captured ({Length} chars)",
                    page.PageIndex + 1, description.Trim().Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same fail-soft behavior as before: one page's failure
                // (timeout, transient error) doesn't take down the whole
                // document's ingestion -- it's just skipped and logged.
                _logger.LogWarning(ex,
                    "Vision analysis failed for page {Page} of {File} — skipping page",
                    page.PageIndex + 1, fileName);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // ConcurrentBag doesn't preserve insertion order -- re-sort by page
        // number so downstream chunking/citation output is deterministic,
        // same as the old sequential version guaranteed for free.
        var orderedResults = results.OrderBy(r => r.PageNumber).ToList();

        _logger.LogInformation(
            "Claude Vision: {Count} pages with charts/scanned content in {File}",
            orderedResults.Count, fileName);

        return orderedResults;
    }
}