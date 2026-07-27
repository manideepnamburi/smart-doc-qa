using Microsoft.Extensions.Logging;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Application.UseCases;

/// <summary>
/// IngestDocumentUseCase — the single orchestrator for turning one raw file
/// (PDF or Word doc, from local disk / OneDrive / Azure Blob) into fully
/// searchable, multi-modal, graph-aware data.
///
/// FULL PIPELINE (in the order it actually executes):
///   1. Resolve Source  → get a byte[] of the raw file, regardless of where
///                          it physically lives (local, cloud, etc.)
///   2. Parse            → extract plain text + page metadata (PdfPig / Word)
///   3. Chunk             → split text into overlap-aware pieces for embedding
///   4. Table Extraction  → Azure Document Intelligence finds structured tables
///   5. Chart Extraction  → Claude Vision describes charts/diagrams AND
///                          transcribes scanned pages (Phase 4b — one
///                          mechanism does double duty)
///   6. Metadata Enrich   → stamp every chunk with source/ingestion metadata
///   7. Embed             → turn every chunk's text into a vector (batch call)
///   8. Build Metadata     → assemble the DocumentMetadata summary record
///   9. Persist            → write chunks + vectors to Qdrant (dense search)
///                          and BM25 SQLite (keyword search) as one unit
///  10. Graph Extraction   → Claude reads every chunk and pulls out entities
///                          + relationships, written into Neo4j for
///                          multi-hop / relationship-style questions
///                          that plain vector similarity can't answer
///                          (Phase 5)
///
/// DESIGN PRINCIPLE CARRIED THROUGH EVERY STEP AFTER PARSING:
/// every extraction step (tables, charts, entities) is wrapped in its own
/// try/catch and is NON-FATAL. A failure in Azure Doc Intelligence, Claude
/// Vision, or the entity extractor should never abort the whole ingestion —
/// the document still gets ingested with whatever succeeded, and the
/// failure is logged so it's visible, not silent.
/// </summary>
public class IngestDocumentUseCase
{
    // ── Dependencies (all interfaces — Clean Architecture: this class
    //    depends on ABSTRACTIONS, never on concrete Qdrant/Neo4j/Claude
    //    classes directly. Swapping any one of these implementations later
    //    requires zero changes here.) ──────────────────────────────────────
    private readonly IDocumentSourceResolverFactory _resolverFactory;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IChunkingStrategy _chunkingStrategy;
    private readonly IMetadataEnricher _metadataEnricher;
    private readonly IEmbeddingService _embeddingService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IEntityExtractor _entityExtractor;
    private readonly IGraphStore _graphStore;
    private readonly ITableExtractor _tableExtractor;
    private readonly IChartExtractor _chartExtractor;
    private readonly ILogger<IngestDocumentUseCase> _logger;

    public IngestDocumentUseCase(
        IDocumentSourceResolverFactory resolverFactory,
        IEnumerable<IDocumentParser> parsers,
        IChunkingStrategy chunkingStrategy,
        IMetadataEnricher metadataEnricher,
        IEmbeddingService embeddingService,
        IDocumentRepository documentRepository,
        IEntityExtractor entityExtractor,
        IGraphStore graphStore,
        ITableExtractor tableExtractor,
        IChartExtractor chartExtractor,
        ILogger<IngestDocumentUseCase> logger)
    {
        _resolverFactory = resolverFactory;
        _parsers = parsers;
        _chunkingStrategy = chunkingStrategy;
        _metadataEnricher = metadataEnricher;
        _embeddingService = embeddingService;
        _documentRepository = documentRepository;
        _entityExtractor = entityExtractor;
        _graphStore = graphStore;
        _tableExtractor = tableExtractor;
        _chartExtractor = chartExtractor;
        _logger = logger;
    }

    public async Task<DocumentMetadata> ExecuteAsync(DocumentSource source, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var documentId = Guid.NewGuid().ToString();
        var fileName = source.FileName ?? Path.GetFileName(source.Path);

        _logger.LogInformation("Starting ingestion: {FileName} from {SourceType}", fileName, source.SourceType);

        // ── Step 1 — Resolve source to a Stream, then buffer into byte[] ONCE ──
        // WHY buffer to byte[] instead of passing the live Stream around:
        // A Stream is a single-use, forward-only resource — the moment more
        // than one downstream consumer (parser, table extractor, chart
        // extractor) needs to read the SAME bytes, sharing one live Stream
        // causes an ObjectDisposedException the second time anyone tries to
        // read/rewind it (this bit us for real during Phase 4 testing).
        // Reading everything into memory once, then handing each consumer
        // its own `new MemoryStream(fileBytes)`, eliminates that entire bug
        // class — nobody can accidentally dispose a resource someone else
        // still needs.
        var resolver = _resolverFactory.GetResolver(source);
        byte[] fileBytes;
        await using (var sourceStream = await resolver.ResolveAsync(source, ct))
        await using (var buffer = new MemoryStream())
        {
            await sourceStream.CopyToAsync(buffer, ct);
            fileBytes = buffer.ToArray();
        }

        // ── Step 2 — Pick the right parser (PDF or Word) and extract text ──────
        // _parsers is an IEnumerable<IDocumentParser> — every registered
        // parser implementation is injected, and CanParse(fileName) lets
        // each one self-select based on file extension. This means adding
        // support for a new file type later (e.g. .pptx) is just "write a
        // new IDocumentParser and register it" — no changes needed here.
        var parser = _parsers.FirstOrDefault(p => p.CanParse(fileName))
            ?? throw new InvalidOperationException($"No parser registered for file: {fileName}");

        ParsedDocument parsedDocument;
        await using (var parseStream = new MemoryStream(fileBytes))
        {
            parsedDocument = await parser.ParseAsync(parseStream, fileName, ct);
        }
        _logger.LogInformation("Parsed {Pages} pages from {FileName}", parsedDocument.TotalPages, fileName);

        // ── Step 3 — Chunk text content ─────────────────────────────────────────
        // Splits the parsed text into overlap-aware pieces sized for
        // embedding (see ChunkingOptions: TokenSize / Overlap in config).
        // These are the FIRST chunks added to the list — Text chunks.
        // Table and Chart chunks get appended to this same list below,
        // so by the time we reach embedding (Step 7), `chunks` contains
        // ALL modalities in one unified collection.
        var chunks = await _chunkingStrategy.ChunkAsync(parsedDocument, documentId, ct);
        _logger.LogInformation("Created {ChunkCount} text chunks", chunks.Count);

        // ── Step 4 — Extract tables (Azure Document Intelligence) ──────────────
        // Non-fatal: if Azure Doc Intelligence is disabled, misconfigured,
        // or the request fails (e.g. file-size tier limits — see Phase 4
        // debugging notes), we log a warning and continue with zero table
        // chunks rather than aborting the whole ingestion.
        try
        {
            List<ExtractedTable> tables;
            await using (var tableStream = new MemoryStream(fileBytes))
            {
                tables = await _tableExtractor.ExtractTablesAsync(tableStream, fileName, ct);
            }
            var tableChunks = tables.Select((t, i) => new DocumentChunk(
                ChunkId: $"{documentId}_table_{i}",
                DocumentId: documentId,
                FileName: fileName,
                Content: t.MarkdownContent,
                ChunkType: ChunkType.Table,
                PageNumber: t.PageNumber,
                ChunkIndex: chunks.Count + i,
                Metadata: new Dictionary<string, string> { ["caption"] = t.Caption ?? "" }
            )).ToList();
            chunks.AddRange(tableChunks);
            _logger.LogInformation("Extracted {TableCount} tables", tables.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Table extraction failed — continuing without tables");
        }

        // ── Step 5 — Extract charts/images + OCR scanned pages (Claude Vision) ──
        // One mechanism, two jobs: ClaudeVisionChartExtractor rasterizes
        // every PDF page to an image and asks Claude Vision to either
        // describe a chart/diagram OR transcribe scanned text — whichever
        // applies. This is why scanned pages don't need a separate OCR
        // pipeline (see Phase 4b design notes). Also non-fatal.
        try
        {
            List<ExtractedChart> charts;
            await using (var chartStream = new MemoryStream(fileBytes))
            {
                charts = await _chartExtractor.ExtractChartsAsync(chartStream, fileName, ct);
            }
            var chartChunks = charts.Select((c, i) => new DocumentChunk(
                ChunkId: $"{documentId}_chart_{i}",
                DocumentId: documentId,
                FileName: fileName,
                Content: c.Description,
                ChunkType: ChunkType.Chart,
                PageNumber: c.PageNumber,
                ChunkIndex: chunks.Count + i,
                Metadata: new Dictionary<string, string>()
            )).ToList();
            chunks.AddRange(chartChunks);
            _logger.LogInformation("Extracted {ChartCount} charts", charts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chart extraction failed — continuing without charts");
        }

        // ── Step 6 — Enrich metadata ─────────────────────────────────────────────
        // Stamps every chunk (Text + Table + Chart, all together at this
        // point) with source-level metadata: source_type, ingested_at
        // timestamp, file_name, etc. Runs AFTER all three modalities are
        // combined so nothing is missed.
        chunks = await _metadataEnricher.EnrichAsync(chunks, source, ct);

        // ── Step 7 — Generate embeddings in ONE batch call ──────────────────────
        // Batching (rather than one embedding call per chunk) is both
        // faster and cheaper — most embedding providers charge per call
        // AND per token, so fewer, larger calls reduce overhead.
        var texts = chunks.Select(c => c.Content).ToList();
        var embeddings = await _embeddingService.EmbedBatchAsync(texts, ct);
        var chunksWithEmbeddings = chunks.Select((c, i) => c with { Embedding = embeddings[i] }).ToList();
        _logger.LogInformation("Generated embeddings for {Count} chunks", chunksWithEmbeddings.Count);

        // ── Step 8 — Build metadata record ───────────────────────────────────────
        // A summary record for this ingestion — used by the API response,
        // the document registry, and anywhere the app needs to answer
        // "what documents do we have and what's in them" without re-reading
        // every chunk.
        var metadata = new DocumentMetadata(
            DocumentId: documentId,
            FileName: fileName,
            SourcePath: source.Path,
            SourceType: source.SourceType,
            IngestedAt: DateTime.UtcNow,
            TotalPages: parsedDocument.TotalPages,
            TotalChunks: chunksWithEmbeddings.Count
        );

        // ── Step 9 — Persist to Vector store (Qdrant) + Keyword index (BM25) ────
        // IDocumentRepository.SaveAsync fans out to BOTH stores as a single
        // unit of work (see CompositeDocumentRepository) — every chunk
        // becomes searchable by dense (vector similarity) AND sparse
        // (BM25 keyword) retrieval from this point forward.
        await _documentRepository.SaveAsync(chunksWithEmbeddings, metadata, ct);
        _logger.LogInformation("Saved to Vector + BM25 stores");

        // ── Step 10 — Extract entities/relationships and populate the Graph (Neo4j) ──
        //
        // WHY THIS RUNS ON EVERY CHUNK TYPE (Text + Table + Chart), NOT JUST TEXT:
        // Numeric, relational facts often live in table rows and chart
        // descriptions just as much as — sometimes more than — prose
        // paragraphs (e.g. "Hepatitis B vaccination increased from 12.3%
        // in 1999-2002 to 25.2% in 2015-2018" is exactly the kind of
        // Metric → TimePeriod → Value relationship a graph should capture,
        // and that sentence is far more likely to appear in a Chart
        // description or Table row than in narrative text). See Phase 5
        // design discussion for the full reasoning.
        //
        // WHY THIS IS CONCURRENT (SemaphoreSlim) INSTEAD OF A SIMPLE
        // SEQUENTIAL foreach LOOP:
        // A document can easily produce 100+ chunks once tables and charts
        // are included. Each chunk needs its OWN Claude API call for entity
        // extraction — awaiting them one at a time (as this step originally
        // did) means total time = (number of chunks) × (~2 seconds per
        // call), which turns a 135-chunk document into several minutes of
        // dead sequential waiting. Since no chunk's extraction depends on
        // any other chunk's result, they are perfectly safe to run in
        // parallel. A SemaphoreSlim(5) caps concurrency at 5 simultaneous
        // Claude calls — fast, but polite to Anthropic's rate limits
        // (an unbounded Task.WhenAll firing 135 calls at once would risk
        // 429 rate-limit rejections). The SAME throttled-concurrency
        // pattern is used for the Neo4j writes immediately after, since
        // writing 100+ entities/relationships one at a time would
        // reintroduce the same sequential bottleneck on the database side.
        //
        // WHY EXTRACTION AND WRITING ARE TWO SEPARATE CONCURRENT PHASES
        // (not one combined "extract-then-immediately-write" loop per
        // chunk): keeping them separate means a slow/failed Neo4j write
        // for one entity never blocks or delays the Claude extraction call
        // for a completely unrelated chunk — the two concerns (LLM call
        // latency vs. database write latency) are decoupled and each gets
        // its own concurrency budget.
        //
        // Non-fatal at every level: a single chunk's extraction failing,
        // or a single entity/relationship's write failing, is logged and
        // skipped — it never aborts the ingestion or the graph step for
        // every OTHER chunk/entity that succeeded.
        try
        {
            const int MaxConcurrentClaudeCalls = 5;
            var extractionSemaphore = new SemaphoreSlim(MaxConcurrentClaudeCalls);

            // Phase A — run entity/relationship extraction for every chunk,
            // at most 5 Claude calls in flight at any moment.
            var extractionTasks = chunksWithEmbeddings.Select(async chunk =>
            {
                await extractionSemaphore.WaitAsync(ct);
                try
                {
                    return await _entityExtractor.ExtractAsync(chunk, ct);
                }
                catch (Exception ex)
                {
                    // Per-chunk failure — log which chunk, keep going.
                    // Returning empty lists here means this chunk simply
                    // contributes nothing to the graph, without stopping
                    // extraction for the remaining chunks.
                    _logger.LogWarning(ex,
                        "Entity extraction failed for chunk {ChunkId} — skipping",
                        chunk.ChunkId);
                    return (Entities: new List<GraphEntity>(),
                            Relationships: new List<GraphRelationship>());
                }
                finally
                {
                    extractionSemaphore.Release();
                }
            });

            var extractionResults = await Task.WhenAll(extractionTasks);

            // Flatten every chunk's individual (entities, relationships)
            // pair into two single lists ready for writing to Neo4j.
            var allEntities = extractionResults.SelectMany(r => r.Entities).ToList();
            var allRelationships = extractionResults.SelectMany(r => r.Relationships).ToList();

            // Phase B — write everything to Neo4j, again throttled to 5
            // concurrent operations. Entities are written before
            // relationships complete (both fire under Task.WhenAll below,
            // but see Neo4jGraphStore.UpsertRelationshipAsync — it also
            // MERGEs its own endpoint nodes as a safety net, so relationship
            // writes don't strictly require their entities to have
            // succeeded first).
            var writeSemaphore = new SemaphoreSlim(MaxConcurrentClaudeCalls);

            var entityWriteTasks = allEntities.Select(async entity =>
            {
                await writeSemaphore.WaitAsync(ct);
                try
                {
                    await _graphStore.UpsertEntityAsync(entity, ct);
                }
                finally
                {
                    writeSemaphore.Release();
                }
            });

            var relationshipWriteTasks = allRelationships.Select(async rel =>
            {
                await writeSemaphore.WaitAsync(ct);
                try
                {
                    await _graphStore.UpsertRelationshipAsync(rel, ct);
                }
                finally
                {
                    writeSemaphore.Release();
                }
            });

            // Run entity writes and relationship writes concurrently with
            // each other too — both sets share the same semaphore, so the
            // total in-flight Neo4j operations still never exceeds 5.
            await Task.WhenAll(entityWriteTasks.Concat(relationshipWriteTasks));

            _logger.LogInformation(
                "Graph extraction complete: {EntityCount} entities, {RelCount} relationships " +
                "written from {ChunkCount} chunks",
                allEntities.Count, allRelationships.Count, chunksWithEmbeddings.Count);
        }
        catch (Exception ex)
        {
            // Catches anything unexpected at the orchestration level itself
            // (e.g. semaphore/task setup issues) — the per-chunk and
            // per-entity try/catch blocks above already handle the far
            // more likely failure cases (a single bad Claude response or
            // a single failed Neo4j write).
            _logger.LogWarning(ex, "Entity/graph extraction step failed — continuing without graph data");
        }

        sw.Stop();
        _logger.LogInformation(
            "Ingestion complete: {FileName} | {Chunks} chunks | {Time}ms",
            fileName, chunksWithEmbeddings.Count, sw.ElapsedMilliseconds);

        return metadata;
    }
}
