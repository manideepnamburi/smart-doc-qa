using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using OllamaSharp;
using Qdrant.Client;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;
using SmartDocQA.Infrastructure.VectorStore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;
using Neo4j.Driver;

namespace SmartDocQA.Infrastructure;

// ─── NoOp Stubs ──────────────────────────────────────────────────────────────

public class NoOpSparseRetriever : ISparseRetriever
{
    public Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, string? documentIdFilter = null,
        CancellationToken ct = default)
        => Task.FromResult(new List<RetrievedChunk>());
}

public class NoOpGraphStore : IGraphStore
{
    public Task UpsertEntityAsync(GraphEntity entity, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task UpsertRelationshipAsync(GraphRelationship rel, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<List<RetrievedChunk>> SearchByEntityAsync(string query, int topK, CancellationToken ct = default)
        => Task.FromResult(new List<RetrievedChunk>());
    public Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default)
        => Task.CompletedTask;
}

public class NoOpEntityExtractor : IEntityExtractor
{
    public Task<(List<GraphEntity> Entities, List<GraphRelationship> Relationships)> ExtractAsync(
        DocumentChunk chunk, CancellationToken ct = default)
        => Task.FromResult((new List<GraphEntity>(), new List<GraphRelationship>()));
}

public class NoOpTableExtractor : ITableExtractor
{
    public Task<List<ExtractedTable>> ExtractTablesAsync(
        Stream stream, string fileName, CancellationToken ct = default)
        => Task.FromResult(new List<ExtractedTable>());
}

public class NoOpChartExtractor : IChartExtractor
{
    public Task<List<ExtractedChart>> ExtractChartsAsync(
        Stream stream, string fileName, CancellationToken ct = default)
        => Task.FromResult(new List<ExtractedChart>());
}

public class PassthroughQueryRewriter : IQueryRewriter
{
    public Task<string> RewriteAsync(string originalQuery, CancellationToken ct = default)
        => Task.FromResult(originalQuery);
}

// ─── Default Metadata Enricher ────────────────────────────────────────────────

public class DefaultMetadataEnricher : IMetadataEnricher
{
    public Task<List<DocumentChunk>> EnrichAsync(
        List<DocumentChunk> chunks, DocumentSource source, CancellationToken ct = default)
    {
        var enriched = chunks.Select(c =>
        {
            var metadata = new Dictionary<string, string>(c.Metadata)
            {
                ["source_type"] = source.SourceType.ToString(),
                ["ingested_at"] = DateTime.UtcNow.ToString("O"),
                ["file_name"]   = c.FileName
            };
            return c with { Metadata = metadata };
        }).ToList();
        return Task.FromResult(enriched);
    }
}

// ─── Real Qdrant Vector Store ─────────────────────────────────────────────────

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
        _options    = options.Value;
        _ragOptions = ragOptions.Value;
        _logger     = logger;
        _client     = new QdrantClient(_options.Host, _options.Port);
        _store      = new QdrantVectorStore(_client, ownsClient: true);
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
            Id         = StableId(chunk.ChunkId),
            ChunkId    = chunk.ChunkId,
            DocumentId = chunk.DocumentId,
            FileName   = chunk.FileName,
            Content    = chunk.Content,
            ChunkType  = chunk.ChunkType.ToString(),
            PageNumber = chunk.PageNumber,
            ChunkIndex = chunk.ChunkIndex,
            SourceType = chunk.Metadata.GetValueOrDefault("source_type", ""),
            IngestedAt = chunk.Metadata.GetValueOrDefault("ingested_at", ""),
            Embedding  = chunk.Embedding ?? Array.Empty<float>()
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
                    ChunkId:    result.Record.ChunkId,
                    DocumentId: result.Record.DocumentId,
                    FileName:   result.Record.FileName,
                    Content:    result.Record.Content,
                    ChunkType:  Enum.Parse<ChunkType>(result.Record.ChunkType),
                    PageNumber: result.Record.PageNumber,
                    ChunkIndex: result.Record.ChunkIndex,
                    Metadata: new Dictionary<string, string>
                    {
                        ["source_type"] = result.Record.SourceType,
                        ["ingested_at"] = result.Record.IngestedAt
                    }),
                Score:           (float)(result.Score ?? 0),
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

// ─── BM25 Sparse Retriever ────────────────────────────────────────────────────
// NOTE: BM25KeywordIndex moved to Retrieval/BM25KeywordIndex.cs (Phase 2 — real implementation)

public class BM25SparseRetriever : ISparseRetriever
{
    private readonly IKeywordIndex _index;
    public BM25SparseRetriever(IKeywordIndex index) => _index = index;

    public Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, string? documentIdFilter = null,
        CancellationToken ct = default)
        => _index.SearchAsync(query, topK, documentIdFilter, ct);
}

// ─── Qdrant Dense Retriever ───────────────────────────────────────────────────

public class QdrantDenseRetriever : IDenseRetriever
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;

    public QdrantDenseRetriever(IVectorStore vectorStore, IEmbeddingService embeddingService)
    {
        _vectorStore      = vectorStore;
        _embeddingService = embeddingService;
    }

    public async Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, string? documentIdFilter = null,
        CancellationToken ct = default)
    {
        var queryVector = await _embeddingService.EmbedAsync(query, ct);
        return await _vectorStore.SearchAsync(queryVector, topK, documentIdFilter, ct);
    }
}

// ─── Neo4j Graph Store (Phase 5 — Real Implementation) ────────────────────────
//
// WHAT THIS CLASS DOES
// ---------------------
// This is the write/read gateway to the Neo4j knowledge graph. It implements
// IGraphStore, which the rest of the app depends on — nothing outside this
// class knows or cares that Neo4j specifically is the backing database.
// (That's the whole point of the interface — Phase 1's Clean Architecture
// design means we could swap this for ArangoDB later by writing ONE new
// class and changing ONE line in DI registration.)
//
// WHY "MERGE" INSTEAD OF "CREATE" EVERYWHERE
// --------------------------------------------
// Cypher's CREATE always makes a brand-new node/relationship, even if an
// identical one already exists — that would flood the graph with duplicates
// every time the same entity (e.g. "NHANES") is mentioned in a different
// chunk. MERGE is Neo4j's "create-if-missing, else-match-existing" operator —
// it's the direct graph-database equivalent of the canonical-naming problem
// we solved in the extraction prompt. The prompt stops Claude from naming
// the same entity two different ways *within* one chunk; MERGE stops the
// *database* from creating two nodes for the same name across *different*
// chunks/documents.
//
// WHY WE DISPOSE THE DRIVER (IAsyncDisposable)
// -----------------------------------------------
// The Neo4j driver holds a connection pool, similar in spirit to why we
// buffer PDF streams in Phase 4 rather than sharing one live stream —
// long-lived resources need an explicit, deterministic cleanup path so
// they don't leak connections when the app shuts down. Registered as a
// Singleton in DI (see AddInfrastructure), so one driver/connection pool
// is shared for the whole app's lifetime — this is the correct lifetime
// for a driver object, which is itself thread-safe and designed to be
// reused, not recreated per request.

public class Neo4jGraphStore : IGraphStore, IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly ILogger<Neo4jGraphStore> _logger;

    public Neo4jGraphStore(IOptions<Neo4jOptions> options, ILogger<Neo4jGraphStore> logger)
    {
        var opts = options.Value;

        // GraphDatabase.Driver() does NOT connect immediately — it just
        // configures the connection pool. The actual TCP/Bolt handshake
        // happens lazily on the first session.RunAsync() call below.
        _driver = GraphDatabase.Driver(
            opts.Uri,
            AuthTokens.Basic(opts.Username, opts.Password));

        _logger = logger;
    }

    // ── UpsertEntityAsync ───────────────────────────────────────────────────
    // Called once per extracted entity during ingestion (see
    // ClaudeEntityExtractor.ExtractAsync → its caller in the ingestion
    // pipeline). Creates a single (:Entity {name: ...}) node, or updates
    // an existing one if this exact name was already merged from a
    // previous chunk/document.
    //
    // documentIds / pageNumbers are stored as LISTS on the node (not
    // overwritten) because the SAME entity (e.g. "CDC") will legitimately
    // appear across many chunks and even many different documents over
    // time — we want to accumulate "everywhere this entity was seen",
    // not just remember the last place it was mentioned.
    public async Task UpsertEntityAsync(GraphEntity entity, CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"MERGE (e:Entity {name: $name})
                      ON CREATE SET e.type = $type,
                                    e.documentIds = [$documentId],
                                    e.pageNumbers = [$pageNumber]
                      ON MATCH SET  e.documentIds = CASE
                                        WHEN NOT $documentId IN e.documentIds
                                        THEN e.documentIds + $documentId
                                        ELSE e.documentIds END,
                                    e.pageNumbers = CASE
                                        WHEN NOT $pageNumber IN e.pageNumbers
                                        THEN e.pageNumbers + $pageNumber
                                        ELSE e.pageNumbers END",
                    new
                    {
                        name       = entity.Name,
                        type       = entity.Type,
                        documentId = entity.DocumentId,
                        pageNumber = entity.PageNumber
                    });
            });

            _logger.LogDebug(
                "Neo4j entity upserted: {Name} ({Type})", entity.Name, entity.Type);
        }
        catch (Exception ex)
        {
            // Non-fatal by design — same resilience pattern as the table
            // and chart extractors in Phase 4. One bad entity should never
            // abort the whole ingestion; we log and move on.
            _logger.LogWarning(ex,
                "Neo4j upsert failed for entity {Name} — skipping", entity.Name);
        }
    }

    // ── UpsertRelationshipAsync ─────────────────────────────────────────────
    // Called once per extracted relationship. Note it MERGEs the two
    // endpoint entities again (not just the relationship) — this is a
    // safety net. If UpsertEntityAsync failed or was never called for one
    // of these two names (e.g. a transient error), this line guarantees
    // the relationship still has valid nodes to attach to rather than
    // silently failing or creating an orphaned edge.
    public async Task UpsertRelationshipAsync(GraphRelationship rel, CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"MERGE (from:Entity {name: $fromName})
                      MERGE (to:Entity {name: $toName})
                      MERGE (from)-[r:RELATES {type: $relType}]->(to)
                      ON CREATE SET r.documentIds = [$documentId]
                      ON MATCH SET  r.documentIds = CASE
                                        WHEN NOT $documentId IN r.documentIds
                                        THEN r.documentIds + $documentId
                                        ELSE r.documentIds END",
                    new
                    {
                        fromName   = rel.FromEntity,
                        toName     = rel.ToEntity,
                        relType    = rel.RelationType,
                        documentId = rel.DocumentId
                    });
            });

            _logger.LogDebug(
                "Neo4j relationship upserted: {From} -{Type}-> {To}",
                rel.FromEntity, rel.RelationType, rel.ToEntity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Neo4j upsert failed for relationship {From}->{To} — skipping",
                rel.FromEntity, rel.ToEntity);
        }
    }

    // ── SearchByEntityAsync ─────────────────────────────────────────────────
    // This is the READ side that powers graph retrieval at query time —
    // called by Neo4jGraphRetriever below, which in turn gets fused
    // alongside dense (Qdrant) and sparse (BM25) results by RRFFusionStrategy.
    //
    // STRATEGY CHOSEN: simple case-insensitive keyword match (CONTAINS) on
    // entity name, then one hop of graph traversal to pull in connected
    // entities/relationships. This was the deliberate, cost-conscious
    // choice over an LLM-assisted query-entity-recognition step — matching
    // the same pre-filter, cost-aware philosophy already used in
    // ClaudeReranker (see its comments on TopKAfterRerank pre-filtering).
    // Trade-off: this will miss true synonyms (e.g. searching "heart
    // disease" won't match a node literally named "Hypertension") — an
    // acceptable limitation for a personal/learning-scale project.
    //
    // WHY RESULTS ARE "SYNTHESIZED" DocumentChunks, NOT REAL ONES:
    // Graph nodes are entities, not text chunks — there is no literal
    // paragraph of document text living on an Entity node. So for each
    // matched entity we build a short, readable sentence describing its
    // relationships (e.g. "NHANES (Survey): NHANES --conducted_by-->
    // National Center for Health Statistics") and wrap THAT sentence in a
    // DocumentChunk. This lets graph results flow through the exact same
    // RetrievedChunk → FusedChunk → RankedChunk → answer-synthesis pipeline
    // as dense/sparse results, with zero special-casing needed downstream.
    public async Task<List<RetrievedChunk>> SearchByEntityAsync(
        string query, int topK, CancellationToken ct = default)
    {
        var results = new List<RetrievedChunk>();

        await using var session = _driver.AsyncSession();
        try
        {
            var records = await session.ExecuteReadAsync(async tx =>
            {
                // OPTIONAL MATCH so entities with zero relationships still
                // come back (as a single row with connected/relType = null)
                // instead of being silently excluded by a plain MATCH.
                var cursor = await tx.RunAsync(
                    @"MATCH (e:Entity)
                      WHERE toLower($searchTerm) CONTAINS toLower(e.name)
                      OPTIONAL MATCH (e)-[r:RELATES]-(connected:Entity)
                      RETURN e.name AS entityName, e.type AS entityType,
                             r.type AS relType, connected.name AS connectedName,
                             e.documentIds AS documentIds
                      LIMIT $limit",
                    new { searchTerm = query, limit = topK * 3 });

                // Fetch more raw rows than topK because one entity can span
                // multiple relationship rows — we group and collapse below.
                return await cursor.ToListAsync(ct);
            });

            // Group the flat rows back into one result per distinct entity,
            // collecting all its relationship lines into one description.
            var byEntity = records.GroupBy(r => r["entityName"].As<string>());

            int rank = 0;
            foreach (var group in byEntity.Take(topK))
            {
                var entityName = group.Key;
                var entityType = group.First()["entityType"].As<string>();
                var docIds = group.First()["documentIds"].As<List<object>>()
                    .Select(d => d.ToString()!).ToList();

                var relationLines = group
                    .Where(r => r["relType"] is not null)
                    .Select(r =>
                        $"{entityName} --{r["relType"].As<string>()}--> {r["connectedName"].As<string>()}")
                    .Distinct()
                    .ToList();

                var description = relationLines.Any()
                    ? $"{entityName} ({entityType}): " + string.Join("; ", relationLines)
                    : $"{entityName} ({entityType}) — no known relationships";

                results.Add(new RetrievedChunk(
                    Chunk: new DocumentChunk(
                        ChunkId:    $"graph_{entityName.Replace(" ", "_")}",
                        DocumentId: docIds.FirstOrDefault() ?? "",
                        FileName:   "",
                        Content:    description,
                        ChunkType:  ChunkType.Text,
                        PageNumber: 0,
                        ChunkIndex: rank,
                        Metadata: new Dictionary<string, string> { ["source"] = "graph" }),
                    // Simple rank-based score (1st match=1.0, 2nd=0.5, ...)
                    // rather than a similarity score, since graph matches
                    // aren't vector-comparable to dense/sparse scores —
                    // RRFFusionStrategy only needs relative ORDER within
                    // each retriever's own result list, not a comparable
                    // absolute scale across retrievers.
                    Score:           1.0f / (rank + 1),
                    RetrievalSource: "graph"));

                rank++;
            }

            _logger.LogInformation(
                "Neo4j graph search: '{Query}' → {Count} entities found",
                query, results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Neo4j search failed for query '{Query}' — returning empty", query);
        }

        return results;
    }

    // ── DeleteByDocumentAsync ───────────────────────────────────────────────
    // Called by the smart-delete flow (see DeleteDocumentsUseCase) so that
    // removing a document also cleans up its footprint in the graph — not
    // just its vectors (Qdrant) and keyword index (BM25).
    //
    // IMPORTANT NUANCE: an entity/relationship might be shared by MULTIPLE
    // documents (e.g. "CDC" appears in 20 different ingested PDFs). So we
    // don't blindly delete the node — we first REMOVE this one documentId
    // from its documentIds list, and only DETACH DELETE the node/edge
    // entirely once that list becomes empty (meaning no remaining document
    // references it). This mirrors the same "shared resource" caution from
    // the shared-Stream bug in Phase 4 — don't destroy something that
    // other consumers still depend on.
    public async Task DeleteByDocumentAsync(string documentId, CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                // Strip documentId from entities; delete only fully-orphaned nodes
                await tx.RunAsync(
                    @"MATCH (e:Entity)
                      WHERE $documentId IN e.documentIds
                      SET e.documentIds = [d IN e.documentIds WHERE d <> $documentId]
                      WITH e WHERE size(e.documentIds) = 0
                      DETACH DELETE e",
                    new { documentId });

                // Same idea for relationships
                await tx.RunAsync(
                    @"MATCH ()-[r:RELATES]->()
                      WHERE $documentId IN r.documentIds
                      SET r.documentIds = [d IN r.documentIds WHERE d <> $documentId]
                      WITH r WHERE size(r.documentIds) = 0
                      DELETE r",
                    new { documentId });
            });

            _logger.LogInformation(
                "Neo4j: cleaned up entities/relationships for document {DocId}", documentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Neo4j delete failed for document {DocId}", documentId);
        }
    }

    // ── DisposeAsync ────────────────────────────────────────────────────────
    // Releases the driver's connection pool cleanly on app shutdown.
    // Because this class is registered as a Singleton, .NET's DI container
    // will call this automatically when the host shuts down — no manual
    // wiring needed elsewhere.
    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
    }
}

// ─── Neo4j Graph Retriever ────────────────────────────────────────────────────
//
// WHY THIS CLASS EXISTS SEPARATELY FROM Neo4jGraphStore:
// IGraphStore is the full read/write contract (upsert, delete, search) used
// during INGESTION. IGraphRetriever is a narrower, read-only contract
// (just RetrieveAsync) used during QUERY time, matching the shape of
// IDenseRetriever and ISparseRetriever so RRFFusionStrategy can treat all
// three retrieval sources identically without knowing which database is
// behind each one. This class is a thin adapter — it does no real work
// itself, it just forwards to the graph store's search method, translating
// between the two interface shapes so the query pipeline stays symmetric
// with its dense/sparse counterparts.
public class Neo4jGraphRetriever : IGraphRetriever
{
    private readonly IGraphStore _graphStore;
    public Neo4jGraphRetriever(IGraphStore graphStore) => _graphStore = graphStore;

    public Task<List<RetrievedChunk>> RetrieveAsync(
        string query, int topK, CancellationToken ct = default)
        => _graphStore.SearchByEntityAsync(query, topK, ct);
}

// ─── Claude Reranker (Phase 3 — Real Batch Implementation) ───────────────────
//
// Design decisions:
// 1. PRE-FILTER: Takes only top 5 from RRF before scoring
//    (cost-conscious choice for personal project — see Phase 3 design notes)
// 2. BATCH CALL: Scores ALL chunks in ONE Claude API call
//    (avoids "lost in middle" problem by keeping prompt small)
// 3. FALLBACK: If Claude returns invalid JSON, falls back to RRF score
//    (resilient — never crashes the query pipeline)

public class ClaudeReranker : IReranker
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptsOptions _prompts;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<ClaudeReranker> _logger;

    public ClaudeReranker(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        IOptions<PromptsOptions> prompts,
        IOptions<RagOptions> ragOptions,
        ILogger<ClaudeReranker> logger)
    {
        _llmClient   = llmClient;
        _promptLoader = promptLoader;
        _prompts     = prompts.Value;
        _ragOptions  = ragOptions.Value;
        _logger      = logger;
    }

    public async Task<List<RankedChunk>> RerankAsync(
        string query,
        List<FusedChunk> candidates,
        int topK,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return new List<RankedChunk>();

        // ── Step 1: Pre-filter top N before sending to Claude ─────────────────
        // Cost-conscious design: only send top 5 from RRF to Claude
        // This keeps the prompt small and focused
        var preFiltered = candidates
            .OrderByDescending(c => c.FusedScore)
            .Take(_ragOptions.TopKAfterRerank)
            .ToList();

        _logger.LogInformation(
            "Reranker: pre-filtered {Total} → {Filtered} chunks for batch scoring",
            candidates.Count, preFiltered.Count);

        // ── Step 2: Build ONE batch prompt with ALL pre-filtered chunks ────────
        var systemPrompt = _promptLoader.Load(_prompts.Rerank);

        var chunksText = new System.Text.StringBuilder();
        for (int i = 0; i < preFiltered.Count; i++)
        {
            var chunk = preFiltered[i];
            chunksText.AppendLine($"CHUNK_{i + 1} (id: {chunk.Chunk.ChunkId}):");
            chunksText.AppendLine(chunk.Chunk.Content.Length > 300
                ? chunk.Chunk.Content[..300] + "..."
                : chunk.Chunk.Content);
            chunksText.AppendLine();
        }

        // Build expected JSON format string for Claude
        var expectedFormat = "{" + string.Join(", ",
            preFiltered.Select((_, i) =>
                $"\"chunk_{i + 1}\": <score 1-10>")) + "}";

        var userPrompt =
            $"Question: {query}\n\n" +
            $"Score each chunk 1-10 for how well it answers the question.\n" +
            $"10 = directly answers the question\n" +
            $"1  = completely irrelevant\n\n" +
            $"{chunksText}\n" +
            $"Respond ONLY with valid JSON in this exact format:\n" +
            $"{expectedFormat}";

        // ── Step 3: ONE Claude API call scores ALL chunks ─────────────────────
        List<RankedChunk> ranked;
        try
        {
            var response = await _llmClient.CompleteAsync(
                systemPrompt, userPrompt, ct);

            _logger.LogDebug("Reranker raw response: {Response}", response);

            ranked = ParseBatchScores(response, preFiltered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Batch reranking failed — falling back to RRF scores");

            // Fallback: use RRF scores as reranker scores
            ranked = preFiltered
                .Select(c => new RankedChunk(c.Chunk, c.FusedScore, "rrf-fallback"))
                .ToList();
        }

        var result = ranked
            .OrderByDescending(r => r.RerankerScore)
            .Take(topK)
            .ToList();

        _logger.LogInformation(
            "Reranker complete: {Input} chunks → top {Output} selected | " +
            "Scores: [{Scores}]",
            preFiltered.Count,
            result.Count,
            string.Join(", ", result.Select(r => $"{r.RerankerScore:F1}")));

        return result;
    }

    // ── Parse Claude's batch JSON response ────────────────────────────────────

    private List<RankedChunk> ParseBatchScores(
        string response,
        List<FusedChunk> chunks)
    {
        var ranked = new List<RankedChunk>();

        try
        {
            // Strip any markdown code fences Claude might add
            var cleaned = response.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();
            }

            using var doc = JsonDocument.Parse(cleaned);

            for (int i = 0; i < chunks.Count; i++)
            {
                var key = $"chunk_{i + 1}";
                float score = chunks[i].FusedScore; // default = RRF score

                if (doc.RootElement.TryGetProperty(key, out var scoreElement))
                {
                    score = scoreElement.ValueKind == JsonValueKind.Number
                        ? scoreElement.GetSingle()
                        : float.TryParse(
                            scoreElement.GetString(),
                            out var parsed) ? parsed : chunks[i].FusedScore;
                }

                ranked.Add(new RankedChunk(chunks[i].Chunk, score, "batch-scored"));

                _logger.LogDebug(
                    "Chunk {Key}: score {Score:F1} | {Preview}",
                    key, score,
                    chunks[i].Chunk.Content.Length > 50
                        ? chunks[i].Chunk.Content[..50] + "..."
                        : chunks[i].Chunk.Content);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse reranker JSON response: {Response}",
                response);

            // Fallback: return all chunks with RRF scores
            ranked = chunks
                .Select(c => new RankedChunk(c.Chunk, c.FusedScore, "parse-failed"))
                .ToList();
        }

        return ranked;
    }
}

// ─── Claude Query Rewriter ────────────────────────────────────────────────────

public class ClaudeQueryRewriter : IQueryRewriter
{
    private readonly ILlmClient _llmClient;
    private readonly ILogger<ClaudeQueryRewriter> _logger;

    public ClaudeQueryRewriter(ILlmClient llmClient, ILogger<ClaudeQueryRewriter> logger)
    {
        _llmClient = llmClient;
        _logger    = logger;
    }

    public async Task<string> RewriteAsync(string originalQuery, CancellationToken ct = default)
    {
        var systemPrompt =
            "You are a search query optimizer. Rewrite the user's question to improve " +
            "document retrieval. Make it more specific and keyword-rich. " +
            "Return ONLY the rewritten query — no explanation, no quotes.";

        var rewritten = await _llmClient.CompleteAsync(systemPrompt, originalQuery, ct);
        _logger.LogDebug("Query rewritten: '{Original}' → '{Rewritten}'",
            originalQuery, rewritten.Trim());
        return rewritten.Trim();
    }
}

// ─── Claude Entity Extractor (Phase 5) ───────────────────────────────────────
//
// Reads a chunk's text content and asks Claude to extract entities +
// relationships as structured JSON, for the knowledge graph.
// Applies to ALL chunk types (Text, Table, Chart) — numeric/relational
// facts often live in tables and chart descriptions, not just prose.

public class ClaudeEntityExtractor : IEntityExtractor
{
    private readonly ILlmClient _llmClient;
    private readonly IPromptLoader _promptLoader;
    private readonly ILogger<ClaudeEntityExtractor> _logger;

    private const string SystemPrompt =
        "You are a precise, conservative entity-relationship extraction " +
        "system. You only output valid JSON, never prose.";

    public ClaudeEntityExtractor(
        ILlmClient llmClient,
        IPromptLoader promptLoader,
        ILogger<ClaudeEntityExtractor> logger)
    {
        _llmClient = llmClient;
        _promptLoader = promptLoader;
        _logger = logger;
    }

    public async Task<(List<GraphEntity> Entities, List<GraphRelationship> Relationships)> ExtractAsync(
        DocumentChunk chunk, CancellationToken ct = default)
    {
        // Skip near-empty chunks — nothing worth extracting
        if (string.IsNullOrWhiteSpace(chunk.Content) || chunk.Content.Length < 20)
            return (new List<GraphEntity>(), new List<GraphRelationship>());

        var userPrompt = _promptLoader.LoadAndFill("entity_extraction.txt",
            new Dictionary<string, string> { ["content"] = chunk.Content });

        string response;
        try
        {
            response = await _llmClient.CompleteAsync(SystemPrompt, userPrompt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Entity extraction call failed for chunk {ChunkId} — skipping",
                chunk.ChunkId);
            return (new List<GraphEntity>(), new List<GraphRelationship>());
        }

        return ParseResponse(response, chunk);
    }

    private (List<GraphEntity>, List<GraphRelationship>) ParseResponse(
        string response, DocumentChunk chunk)
    {
        try
        {
            // Claude sometimes wraps JSON in ```json fences despite instructions —
            // strip defensively
            var cleaned = response.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned
                    .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("```", "")
                    .Trim();
            }

            using var doc = JsonDocument.Parse(cleaned);

            var entities = doc.RootElement.GetProperty("entities")
                .EnumerateArray()
                .Select(e => new GraphEntity(
                    Name: e.GetProperty("name").GetString() ?? "",
                    Type: e.GetProperty("type").GetString() ?? "",
                    DocumentId: chunk.DocumentId,
                    PageNumber: chunk.PageNumber))
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .ToList();

            var relationships = doc.RootElement.GetProperty("relationships")
                .EnumerateArray()
                .Select(r => new GraphRelationship(
                    FromEntity: r.GetProperty("from").GetString() ?? "",
                    ToEntity: r.GetProperty("to").GetString() ?? "",
                    RelationType: r.GetProperty("type").GetString() ?? "",
                    DocumentId: chunk.DocumentId))
                .Where(r => !string.IsNullOrWhiteSpace(r.FromEntity)
                         && !string.IsNullOrWhiteSpace(r.ToEntity))
                .ToList();

            _logger.LogInformation(
                "Chunk {ChunkId}: extracted {EntityCount} entities, {RelCount} relationships",
                chunk.ChunkId, entities.Count, relationships.Count);

            return (entities, relationships);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse entity extraction JSON for chunk {ChunkId} — raw response: {Response}",
                chunk.ChunkId, response);
            return (new List<GraphEntity>(), new List<GraphRelationship>());
        }
    }
}

// ─── Azure Doc Intelligence Table Extractor (Phase 4 — Real) ─────────────────

public class AzureDocIntelligenceTableExtractor : ITableExtractor
{
    private readonly AzureDocIntelligenceOptions _options;
    private readonly ILogger<AzureDocIntelligenceTableExtractor> _logger;
    private DocumentAnalysisClient? _client;

    public AzureDocIntelligenceTableExtractor(
        IOptions<AzureDocIntelligenceOptions> options,
        ILogger<AzureDocIntelligenceTableExtractor> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    private DocumentAnalysisClient GetClient() =>
        _client ??= new DocumentAnalysisClient(
            new Uri(_options.Endpoint),
            new AzureKeyCredential(_options.ApiKey));

    public async Task<List<ExtractedTable>> ExtractTablesAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return new List<ExtractedTable>();

        try
        {
            _logger.LogInformation(
                "Azure Doc Intelligence: analyzing {File} for tables", fileName);

            if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);

            var operation = await GetClient().AnalyzeDocumentAsync(
                WaitUntil.Completed,
                "prebuilt-layout",
                stream,
                cancellationToken: ct);

            var result = operation.Value;
            var tables = new List<ExtractedTable>();

            _logger.LogInformation(
                "Azure Doc Intelligence: found {Count} tables in {File}",
                result.Tables.Count, fileName);

            foreach (var table in result.Tables)
            {
                var markdown  = ConvertToMarkdown(table);
                var firstRegion = table.BoundingRegions.FirstOrDefault();
                var pageNumber = firstRegion.PageNumber;

                // Proximity linking — find any paragraph on this page to use as caption
                var caption = result.Paragraphs
                    .Where(p => p.BoundingRegions.Any(r => r.PageNumber == pageNumber))
                    .OrderByDescending(p => p.Content.Length)
                    .FirstOrDefault()?.Content ?? string.Empty;

                // Truncate caption to first 200 chars — just enough for context
                if (caption.Length > 200)
                    caption = caption[..200] + "...";

                tables.Add(new ExtractedTable(
                    PageNumber:      pageNumber,
                    MarkdownContent: markdown,
                    Caption:         caption));

                _logger.LogDebug(
                    "Extracted table {Rows}×{Cols} on page {Page}",
                    table.RowCount, table.ColumnCount, pageNumber);
            }

            return tables;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Azure Doc Intelligence failed for {File} — skipping table extraction",
                fileName);
            return new List<ExtractedTable>();
        }
    }

    private static string ConvertToMarkdown(DocumentTable table)
    {
        // Build a 2D grid — handles merged cells gracefully
        var grid = new string[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
            grid[cell.RowIndex, cell.ColumnIndex] = cell.Content.Trim();

        var sb = new System.Text.StringBuilder();

        for (int row = 0; row < table.RowCount; row++)
        {
            sb.Append("| ");
            for (int col = 0; col < table.ColumnCount; col++)
            {
                sb.Append(grid[row, col] ?? "");
                sb.Append(" | ");
            }
            sb.AppendLine();

            // Separator after header row
            if (row == 0)
            {
                sb.Append("| ");
                for (int col = 0; col < table.ColumnCount; col++)
                    sb.Append("--- | ");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}


// ─── Claude Vision Chart Extractor (Phase 4b) ────────────────────────────────
//
// Renders each PDF page to PNG (PDFtoImage/PDFium), sends it to Claude Vision,
// and captures a text description for pages containing charts/diagrams or
// scanned text. One mechanism covers BOTH chart description AND OCR.

public class ClaudeVisionChartExtractor : IChartExtractor
{
    private readonly ILlmClient _llmClient;
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
        ILogger<ClaudeVisionChartExtractor> logger)
    {
        _llmClient = llmClient;
        _logger = logger;
    }

    public async Task<List<ExtractedChart>> ExtractChartsAsync(
        Stream stream, string fileName, CancellationToken ct = default)
    {
        // Only PDFs can be rasterized by PDFium
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return new List<ExtractedChart>();

        var results = new List<ExtractedChart>();

        // Buffer PDF bytes — PDFium needs the full document
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

        for (int i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Render page i → PNG bytes (120 DPI keeps images well under API limits)
            byte[] pngBytes;
            using (var bitmap = Conversion.ToImage(pdfBytes, page: i,
                       options: new RenderOptions(Dpi: 120)))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var encoded = image.Encode(SKEncodedImageFormat.Png, 85))
            {
                pngBytes = encoded.ToArray();
            }

            try
            {
                var description = await _llmClient.CompleteWithVisionAsync(
                    SystemPrompt, UserPrompt, pngBytes, ct);

                if (string.IsNullOrWhiteSpace(description) ||
                    description.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Page {Page}: no visual content", i + 1);
                    continue;
                }

                results.Add(new ExtractedChart(
                    PageNumber: i + 1,
                    Description: description.Trim(),
                    ImageBytes: pngBytes));

                _logger.LogInformation(
                    "Page {Page}: visual content captured ({Length} chars)",
                    i + 1, description.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Vision analysis failed for page {Page} of {File} — skipping page",
                    i + 1, fileName);
            }
        }

        _logger.LogInformation(
            "Claude Vision: {Count} pages with charts/scanned content in {File}",
            results.Count, fileName);
        return results;
    }
}

// ─── Composite Document Repository ───────────────────────────────────────────

public class CompositeDocumentRepository : IDocumentRepository
{
    private readonly IVectorStore _vectorStore;
    private readonly IKeywordIndex _keywordIndex;
    private readonly ILogger<CompositeDocumentRepository> _logger;

    public CompositeDocumentRepository(
        IVectorStore vectorStore, IKeywordIndex keywordIndex,
        ILogger<CompositeDocumentRepository> logger)
    {
        _vectorStore  = vectorStore;
        _keywordIndex = keywordIndex;
        _logger       = logger;
    }

    public async Task SaveAsync(
        List<DocumentChunk> chunks, DocumentMetadata metadata,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Saving {Count} chunks for {DocumentId}",
            chunks.Count, metadata.DocumentId);

        await Task.WhenAll(
            _vectorStore.UpsertBatchAsync(chunks, ct),
            _keywordIndex.IndexBatchAsync(chunks, ct));
    }

    public async Task DeleteAsync(string documentId, CancellationToken ct = default)
    {
        await Task.WhenAll(
            _vectorStore.DeleteByDocumentAsync(documentId, ct),
            _keywordIndex.DeleteByDocumentAsync(documentId, ct));
    }

    public Task<List<DocumentMetadata>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(new List<DocumentMetadata>());
}


