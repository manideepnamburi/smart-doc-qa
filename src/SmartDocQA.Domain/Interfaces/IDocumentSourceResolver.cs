using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Resolves a DocumentSource (local / OneDrive / Azure Blob) into a readable stream.
/// Each source type has its own implementation; selected via factory.
/// </summary>
public interface IDocumentSourceResolver
{
    Task<Stream> ResolveAsync(DocumentSource source, CancellationToken ct = default);
    bool CanResolve(DocumentSource source);
}