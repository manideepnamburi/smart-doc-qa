using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using SmartDocQA.Infrastructure.Chunking;
using Xunit;

namespace SmartDocQA.Infrastructure.Tests.Chunking;

public class FixedSizeChunkingStrategyTests
{
    private static FixedSizeChunkingStrategy CreateStrategy(int tokenSize, int overlap)
    {
        var options = Options.Create(new ChunkingOptions
        {
            TokenSize = tokenSize,
            Overlap = overlap
        });
        return new FixedSizeChunkingStrategy(options);
    }

    private static string MakeWords(int count, string prefix = "word")
        => string.Join(" ", Enumerable.Range(0, count).Select(i => $"{prefix}{i}"));

    [Fact]
    public async Task ChunkAsync_ExactMultipleOfTokenSize_ProducesExpectedChunkCount()
    {
        // Arrange: 100 words, TokenSize=50, Overlap=10.
        // Step size = 50 - 10 = 40. Starts: 0, 40, 80 (80 < 100, so it fires) -> 3 chunks.
        var strategy = CreateStrategy(tokenSize: 50, overlap: 10);
        var page = new ParsedPage(PageNumber: 1, RawText: MakeWords(100), IsScanned: false);
        var document = new ParsedDocument("test.pdf", new List<ParsedPage> { page }, TotalPages: 1);

        // Act
        var chunks = await strategy.ChunkAsync(document, "doc-1");

        // Assert
        Assert.Equal(3, chunks.Count);
        Assert.Equal("doc-1_0", chunks[0].ChunkId);
        Assert.Equal("doc-1_1", chunks[1].ChunkId);
        Assert.Equal("doc-1_2", chunks[2].ChunkId);

        // Metadata should record exact word boundaries used
        Assert.Equal("0", chunks[0].Metadata["start_word"]);
        Assert.Equal("50", chunks[0].Metadata["end_word"]);
        Assert.Equal("40", chunks[1].Metadata["start_word"]);
        Assert.Equal("90", chunks[1].Metadata["end_word"]);
        Assert.Equal("80", chunks[2].Metadata["start_word"]);
        Assert.Equal("100", chunks[2].Metadata["end_word"]); // clamped to word count
    }

    [Fact]
    public async Task ChunkAsync_OverlapRegion_ContainsSameWordsFromBothChunks()
    {
        // Arrange: small, hand-traceable example.
        // 10 words, TokenSize=6, Overlap=2 -> step = 4.
        // Chunk 0: words[0..6)  = word0 word1 word2 word3 word4 word5
        // Chunk 1: words[4..10) = word4 word5 word6 word7 word8 word9
        // Chunk 2: words[8..10) = word8 word9  (short tail remainder, start=8 < 10)
        // Overlap region should be word4, word5 -- present at the END of chunk 0
        // and the START of chunk 1.
        var strategy = CreateStrategy(tokenSize: 6, overlap: 2);
        var page = new ParsedPage(1, MakeWords(10), IsScanned: false);
        var document = new ParsedDocument("test.pdf", new List<ParsedPage> { page }, 1);

        // Act
        var chunks = await strategy.ChunkAsync(document, "doc-1");

        // Assert: 3 chunks total, including the short tail remainder
        Assert.Equal(3, chunks.Count);
        Assert.Equal("word0 word1 word2 word3 word4 word5", chunks[0].Content);
        Assert.Equal("word4 word5 word6 word7 word8 word9", chunks[1].Content);
        Assert.Equal("word8 word9", chunks[2].Content);

        // The actual overlapping words, verified word-for-word, not just counted
        Assert.EndsWith("word4 word5", chunks[0].Content);
        Assert.StartsWith("word4 word5", chunks[1].Content);
    }

    [Fact]
    public async Task ChunkAsync_BlankOrWhitespacePage_ProducesNoChunks()
    {
        // Arrange: one real page, one blank page, one whitespace-only page.
        var strategy = CreateStrategy(tokenSize: 50, overlap: 10);
        var pages = new List<ParsedPage>
        {
            new(1, MakeWords(20), IsScanned: false),
            new(2, "", IsScanned: false),
            new(3, "   \n\t  ", IsScanned: false)
        };
        var document = new ParsedDocument("test.pdf", pages, TotalPages: 3);

        // Act
        var chunks = await strategy.ChunkAsync(document, "doc-1");

        // Assert: only page 1 contributes a chunk; blank/whitespace pages are skipped entirely
        Assert.Single(chunks);
        Assert.Equal(1, chunks[0].PageNumber);
    }

    [Fact]
    public async Task ChunkAsync_ScannedPage_TagsChunksAsOcrPage()
    {
        // Arrange
        var strategy = CreateStrategy(tokenSize: 50, overlap: 10);
        var page = new ParsedPage(1, MakeWords(20), IsScanned: true);
        var document = new ParsedDocument("scanned.pdf", new List<ParsedPage> { page }, 1);

        // Act
        var chunks = await strategy.ChunkAsync(document, "doc-1");

        // Assert: downstream reranking/citation logic depends on this tag
        // to treat OCR'd content differently from clean text extraction.
        Assert.All(chunks, c => Assert.Equal(ChunkType.OcrPage, c.ChunkType));
    }

    [Fact]
    public async Task ChunkAsync_NonScannedPage_TagsChunksAsText()
    {
        var strategy = CreateStrategy(tokenSize: 50, overlap: 10);
        var page = new ParsedPage(1, MakeWords(20), IsScanned: false);
        var document = new ParsedDocument("test.pdf", new List<ParsedPage> { page }, 1);

        var chunks = await strategy.ChunkAsync(document, "doc-1");

        Assert.All(chunks, c => Assert.Equal(ChunkType.Text, c.ChunkType));
    }

    [Fact]
    public async Task ChunkAsync_MultiplePages_ChunkIndexIsContinuousAcrossPages()
    {
        // Arrange: two pages, each small enough to produce exactly one chunk.
        // ChunkIndex should keep incrementing across pages, not reset per page.
        var strategy = CreateStrategy(tokenSize: 50, overlap: 10);
        var pages = new List<ParsedPage>
        {
            new(1, MakeWords(20, "p1w"), IsScanned: false),
            new(2, MakeWords(20, "p2w"), IsScanned: false)
        };
        var document = new ParsedDocument("test.pdf", pages, 2);

        // Act
        var chunks = await strategy.ChunkAsync(document, "doc-1");

        // Assert
        Assert.Equal(2, chunks.Count);
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[0].PageNumber);
        Assert.Equal(1, chunks[1].ChunkIndex);
        Assert.Equal(2, chunks[1].PageNumber);
    }

    // ── Defensive fix: Overlap >= TokenSize would cause start += (TokenSize - Overlap)
    // to be zero or negative, making the while loop in ChunkAsync never terminate.
    // The constructor now guards against this instead of allowing a hang.

    [Fact]
    public void Constructor_OverlapEqualToTokenSize_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateStrategy(tokenSize: 50, overlap: 50));

        Assert.Contains("Overlap", ex.Message);
        Assert.Contains("TokenSize", ex.Message);
    }

    [Fact]
    public void Constructor_OverlapGreaterThanTokenSize_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(
            () => CreateStrategy(tokenSize: 50, overlap: 75));
    }

    [Fact]
    public void Constructor_OverlapLessThanTokenSize_DoesNotThrow()
    {
        // Sanity check: valid config (matches production defaults, 500/50)
        // must NOT be rejected by the new guard clause.
        var strategy = CreateStrategy(tokenSize: 500, overlap: 50);
        Assert.NotNull(strategy);
    }

    [Fact]
    public void Constructor_ZeroOverlap_DoesNotThrow()
    {
        // Edge case: zero overlap is valid (chunks are simply adjacent, no repeat).
        var strategy = CreateStrategy(tokenSize: 50, overlap: 0);
        Assert.NotNull(strategy);
    }
}
