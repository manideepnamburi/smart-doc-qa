using SmartDocQA.Domain.Models;

namespace SmartDocQA.Domain.Interfaces;

/// <summary>
/// Factory that picks the right IDocumentSourceResolver for a given source type.
/// </summary>
public interface IDocumentSourceResolverFactory
{
    IDocumentSourceResolver GetResolver(DocumentSource source);
}
