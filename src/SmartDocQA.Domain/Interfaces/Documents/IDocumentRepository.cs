using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Document repository — coordinates all 3 stores as a unit of work.
/// </summary>
public interface IDocumentRepository
{
    Task SaveAsync(List<DocumentChunk> chunks, DocumentMetadata metadata, CancellationToken ct = default);
    Task DeleteAsync(string documentId, CancellationToken ct = default);
    Task<List<DocumentMetadata>> ListAsync(CancellationToken ct = default);
}
