using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure;

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
                        ChunkType:  Domain.Enums.ChunkType.Text,
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