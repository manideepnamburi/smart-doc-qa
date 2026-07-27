using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure.Chunking;

/// <summary>
/// Splits parsed pages into fixed-size token chunks with configurable overlap.
/// Simple word-based tokenization — good enough for Phase 1.
/// </summary>
public class FixedSizeChunkingStrategy : IChunkingStrategy
{
    private readonly ChunkingOptions _options;

    public FixedSizeChunkingStrategy(IOptions<ChunkingOptions> options)
    {
        _options = options.Value;
    }

    public Task<List<DocumentChunk>> ChunkAsync(
        ParsedDocument document,
        string documentId,
        CancellationToken ct = default)
    {
        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        foreach (var page in document.Pages)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(page.RawText)) continue;

            // Simple word tokenization (swap for tiktoken or ML tokenizer later)
            var words = page.RawText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var start = 0;
            while (start < words.Length)
            {
                var end = Math.Min(start + _options.TokenSize, words.Length);
                var content = string.Join(" ", words[start..end]);

                chunks.Add(new DocumentChunk(
                    ChunkId: $"{documentId}_{chunkIndex}",
                    DocumentId: documentId,
                    FileName: document.FileName,
                    Content: content,
                    ChunkType: page.IsScanned ? ChunkType.OcrPage : ChunkType.Text,
                    PageNumber: page.PageNumber,
                    ChunkIndex: chunkIndex,
                    Metadata: new Dictionary<string, string>
                    {
                        ["word_count"] = words.Length.ToString(),
                        ["start_word"] = start.ToString(),
                        ["end_word"] = end.ToString()
                    }
                ));

                chunkIndex++;

                // Overlap: step back by overlap amount
                start += _options.TokenSize - _options.Overlap;
            }
        }

        return Task.FromResult(chunks);
    }
}
