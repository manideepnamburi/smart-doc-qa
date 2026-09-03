using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Qdrant.Client;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using SmartDocQA.Infrastructure.VectorStore;

namespace SmartDocQA.Infrastructure;

/// <summary>
/// Real Qdrant vector store using Semantic Kernel's official connector.
/// Includes MinSimilarityScore filtering — chunks below threshold are
/// discarded before being passed to the LLM, preventing hallucination
/// on out-of-scope questions and saving unnecessary API calls.
/// </summary>
public class QdrantVectorStoreAdapter : IVectorStore, IAsyncDisposable
{
    private readonly QdrantClient _client;
    private readonly QdrantVectorStore _store;
    private readonly QdrantOptions _options;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<QdrantVectorStoreAdapter> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _collectionReady = false;

    public QdrantVectorStoreAdapter(
        IOptions<QdrantOptions> options,
        IOptions<RagOptions> ragOptions,
        ILogger<QdrantVectorStoreAdapter> logger)
    {
        _options = options.Value;
        _ragOptions = ragOptions.Value;
        _logger = logger;
        _client = new QdrantClient(_options.Host, _options.Port);
        _store = new QdrantVectorStore(_client, ownsClient: true);
    }

    // ── Collection initialisation ─────────────────────────────────────────────

    private async Task<VectorStoreCollection<ulong, QdrantChunkRecord>>
        GetCollectionAsync(CancellationToken ct)
    {
        var collection = _store.GetCollection<ulong, QdrantChunkRecord>(_options.CollectionName);

        if (!_collectionReady)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (!_collectionReady)
                {
                    await collection.EnsureCollectionExistsAsync(ct);
                    _collectionReady = true;
                    _logger.LogInformation(
                        "Qdrant collection '{Name}' ready on {Host}:{Port}",
                        _options.CollectionName, _options.Host, _options.Port);
                }
            }
            finally { _initLock.Release(); }
        }
        return collection;
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    public async Task UpsertAsync(DocumentChunk chunk, CancellationToken ct = default)
        => await UpsertBatchAsync(new List<DocumentChunk> { chunk }, ct);

    public async Task UpsertBatchAsync(
        List<DocumentChunk> chunks, CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(ct);

        var records = chunks.Select(chunk => new QdrantChunkRecord
        {
            Id = StableId(chunk.ChunkId),
            ChunkId = chunk.ChunkId,
            DocumentId = chunk.DocumentId,
            FileName = chunk.FileName,
            Content = chunk.Content,
            ChunkType = chunk.ChunkType.ToString(),
            PageNumber = chunk.PageNumber,
            ChunkIndex = chunk.ChunkIndex,
            SourceType = chunk.Metadata.GetValueOrDefault("source_type", ""),
            IngestedAt = chunk.Metadata.GetValueOrDefault("ingested_at", ""),
            Embedding = chunk.Embedding ?? Array.Empty<float>()
        }).ToList();

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            await collection.UpsertAsync(record, cancellationToken: ct);
        }

        _logger.LogInformation(
            "Upserted {Count} chunks to Qdrant '{Collection}'",
            chunks.Count, _options.CollectionName);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<List<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK,
        string? documentIdFilter = null,
        CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(ct);

        var searchOptions = new VectorSearchOptions<QdrantChunkRecord>
        {
            VectorProperty = r => r.Embedding
        };

        var results = new List<RetrievedChunk>();

        await foreach (var result in collection.SearchAsync(
            new ReadOnlyMemory<float>(queryVector),
            top: topK,
            searchOptions,
            cancellationToken: ct))
        {
            // Filter 1 — optional document scope filter
            if (documentIdFilter is not null &&
                result.Record.DocumentId != documentIdFilter)
                continue;

            // Filter 2 — similarity threshold
            // Chunks below MinSimilarityScore are not relevant enough to send to LLM.
            // This prevents hallucination on out-of-scope questions and saves API calls.
            if ((result.Score ?? 0) < _ragOptions.MinSimilarityScore)
            {
                _logger.LogDebug(
                    "Chunk {ChunkId} discarded — score {Score:F3} below threshold {Threshold}",
                    result.Record.ChunkId, result.Score ?? 0, _ragOptions.MinSimilarityScore);
                continue;
            }

            results.Add(new RetrievedChunk(
                Chunk: new DocumentChunk(
                    ChunkId: result.Record.ChunkId,
                    DocumentId: result.Record.DocumentId,
                    FileName: result.Record.FileName,
                    Content: result.Record.Content,
                    ChunkType: Enum.Parse<ChunkType>(result.Record.ChunkType),
                    PageNumber: result.Record.PageNumber,
                    ChunkIndex: result.Record.ChunkIndex,
                    Metadata: new Dictionary<string, string>
                    {
                        ["source_type"] = result.Record.SourceType,
                        ["ingested_at"] = result.Record.IngestedAt
                    }),
                Score: (float)(result.Score ?? 0),
                RetrievalSource: "dense"));
        }

        _logger.LogDebug(
            "Qdrant search: {Total} retrieved, {Kept} passed threshold {Threshold}",
            topK, results.Count, _ragOptions.MinSimilarityScore);

        return results;
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteByDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        await _client.DeleteAsync(
            _options.CollectionName,
            filter: new Qdrant.Client.Grpc.Filter
            {
                Must =
                {
                    new Qdrant.Client.Grpc.Condition
                    {
                        Field = new Qdrant.Client.Grpc.FieldCondition
                        {
                            Key   = "document_id",
                            Match = new Qdrant.Client.Grpc.Match { Text = documentId }
                        }
                    }
                }
            },
            cancellationToken: ct);

        _logger.LogInformation(
            "Deleted all chunks for document {DocumentId} from Qdrant", documentId);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public async Task ResetCollectionAsync(CancellationToken ct = default)
    {
        _logger.LogWarning(
            "Resetting Qdrant collection '{Name}' — all vectors will be deleted",
            _options.CollectionName);

        // Delete the entire collection
        await _client.DeleteCollectionAsync(_options.CollectionName, cancellationToken: ct);

        // Recreate it empty — ready for fresh ingestion
        _collectionReady = false;
        await GetCollectionAsync(ct);

        _logger.LogWarning(
            "Qdrant collection '{Name}' reset complete — ready for fresh ingestion",
            _options.CollectionName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ulong StableId(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToUInt64(hash, 0);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _initLock.Dispose();
        await Task.CompletedTask;
    }
}
