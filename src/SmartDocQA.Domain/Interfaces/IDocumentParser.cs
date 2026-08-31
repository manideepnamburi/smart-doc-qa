using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Extracts raw text + page metadata from a document stream.
/// Implementations: PdfParser, WordDocParser.
/// </summary>
public interface IDocumentParser
{
    bool CanParse(string fileName);
    Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken ct = default);
}