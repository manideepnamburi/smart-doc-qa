using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure.DocumentSources;

// ─── Local File Resolver ─────────────────────────────────────────────────────

public class LocalFileSourceResolver : IDocumentSourceResolver
{
    public bool CanResolve(DocumentSource source) =>
        source.SourceType == DocumentSourceType.LocalPath;

    public Task<Stream> ResolveAsync(
        DocumentSource source, CancellationToken ct = default)
    {
        if (!File.Exists(source.Path))
            throw new FileNotFoundException(
                $"Document not found at local path: {source.Path}");

        Stream stream = File.OpenRead(source.Path);
        return Task.FromResult(stream);
    }
}

// ─── Azure Blob Resolver ─────────────────────────────────────────────────────

public class AzureBlobSourceResolver : IDocumentSourceResolver
{
    private readonly AzureBlobOptions _options;
    private readonly ILogger<AzureBlobSourceResolver> _logger;

    public AzureBlobSourceResolver(
        IOptions<AzureBlobOptions> options,
        ILogger<AzureBlobSourceResolver> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public bool CanResolve(DocumentSource source) =>
        source.SourceType == DocumentSourceType.AzureBlob;

    public async Task<Stream> ResolveAsync(
        DocumentSource source, CancellationToken ct = default)
    {
        var containerName = source.ContainerName ?? _options.DefaultContainer;
        _logger.LogInformation(
            "Resolving Azure Blob: {Container}/{Path}", containerName, source.Path);

        var blobClient = new BlobClient(
            _options.ConnectionString,
            containerName,
            source.Path);

        var ms = new MemoryStream();
        await blobClient.DownloadToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }
}

// ─── OneDrive Resolver ───────────────────────────────────────────────────────

/// <summary>
/// OneDrive resolver — stubbed for Phase 1.
/// Full implementation uses Microsoft.Graph SDK v5.
/// Enable in Phase 6 when OneDrive source support is needed.
/// </summary>
public class OneDriveSourceResolver : IDocumentSourceResolver
{
    private readonly ILogger<OneDriveSourceResolver> _logger;

    public OneDriveSourceResolver(ILogger<OneDriveSourceResolver> logger)
        => _logger = logger;

    public bool CanResolve(DocumentSource source) =>
        source.SourceType == DocumentSourceType.OneDrive;

    public Task<Stream> ResolveAsync(
        DocumentSource source, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "OneDrive source resolver is not yet implemented. " +
            "Use LocalPath or AzureBlob for now. Path: {Path}", source.Path);

        throw new NotImplementedException(
            "OneDrive source resolver will be implemented in Phase 6. " +
            "Please use LocalPath or AzureBlob source type.");
    }
}

// ─── Factory ─────────────────────────────────────────────────────────────────

public class DocumentSourceResolverFactory : IDocumentSourceResolverFactory
{
    private readonly IEnumerable<IDocumentSourceResolver> _resolvers;

    public DocumentSourceResolverFactory(
        IEnumerable<IDocumentSourceResolver> resolvers)
        => _resolvers = resolvers;

    public IDocumentSourceResolver GetResolver(DocumentSource source)
    {
        var resolver = _resolvers.FirstOrDefault(r => r.CanResolve(source))
            ?? throw new NotSupportedException(
                $"No resolver registered for source type: {source.SourceType}.");

        return resolver;
    }
}
