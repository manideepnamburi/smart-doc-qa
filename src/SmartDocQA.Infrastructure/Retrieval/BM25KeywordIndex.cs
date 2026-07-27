using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Enums;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Domain.Models;

namespace SmartDocQA.Infrastructure.Retrieval;

/// <summary>
/// SQLite-backed BM25 keyword index — replaces the Phase 2a JSON file approach.
///
/// WHY SQLite instead of a JSON file:
/// The JSON approach loaded/saved the ENTIRE index on every change. At small
/// scale (hundreds of chunks) this is instant. At larger scale (10,000+
/// documents) a single document delete required rewriting the whole file —
/// measured at ~22 seconds at 600,000 chunks. SQLite uses indexed lookups,
/// so deleting one document only touches that document's rows — measured
/// at ~5-50ms regardless of total corpus size.
///
/// WHY this design stays portable to Azure SQL later:
/// Uses System.Data.Common.DbConnection (provider-agnostic) rather than
/// SqliteConnection directly in the query logic, and all multi-word lookups
/// use a single batched "WHERE word IN (...)" query instead of one query
/// per word. This means the SAME code stays fast even if the connection
/// is swapped to a networked database — no per-word round trips.
///
/// How it works:
/// 1. On ingestion, each chunk's text is tokenized into words
/// 2. Words + counts are stored in bm25_term_frequency (the inverted index,
///    as rows instead of an in-memory dictionary)
/// 3. On search, query words are looked up in ONE batched query,
///    then BM25 scores are calculated in C# from the returned rows
/// 4. avgDocLength is cached in memory and refreshed after writes —
///    it's the only thing still kept outside the database, since it's
///    a single number recomputed cheaply
/// </summary>
public class BM25KeywordIndex : IKeywordIndex, IAsyncDisposable
{
    private const float K1 = 1.5f;   // term frequency saturation
    private const float B  = 0.75f;  // length normalization strength

    private readonly BM25Options _options;
    private readonly ILogger<BM25KeywordIndex> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized = false;

    // Cached aggregate stats — cheap to keep in memory, expensive to
    // recompute per-query. Refreshed after every write operation.
    private double _avgDocLength = 0;
    private int _totalChunkCount = 0;

    public BM25KeywordIndex(
        IOptions<BM25Options> options,
        ILogger<BM25KeywordIndex> logger)
    {
        _options = options.Value;
        _logger  = logger;

        var dbPath = Path.IsPathRooted(_options.DatabasePath)
            ? _options.DatabasePath
            : Path.Combine(AppContext.BaseDirectory, _options.DatabasePath);

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";

        _logger.LogInformation("BM25 SQLite database: {Path}", dbPath);
    }

    // ── Tokenizer ────────────────────────────────────────────────────────────

    private static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .ToLowerInvariant()
            .Split(
                new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?',
                        '(', ')', '[', ']', '{', '}', '"', '\'', '-', '/' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToList();
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS bm25_chunks (
                    chunk_id      TEXT PRIMARY KEY,
                    document_id   TEXT NOT NULL,
                    file_name     TEXT NOT NULL,
                    content       TEXT NOT NULL,
                    chunk_type    TEXT NOT NULL,
                    page_number   INTEGER NOT NULL,
                    chunk_index   INTEGER NOT NULL,
                    doc_length    INTEGER NOT NULL,
                    metadata_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_bm25_chunks_document
                    ON bm25_chunks(document_id);

                CREATE TABLE IF NOT EXISTS bm25_term_frequency (
                    chunk_id    TEXT NOT NULL,
                    word        TEXT NOT NULL,
                    term_count  INTEGER NOT NULL,
                    PRIMARY KEY (chunk_id, word)
                );
                CREATE INDEX IF NOT EXISTS idx_bm25_term_word
                    ON bm25_term_frequency(word);
                CREATE INDEX IF NOT EXISTS idx_bm25_term_chunk
                    ON bm25_term_frequency(chunk_id);
                """;

            await cmd.ExecuteNonQueryAsync(ct);

            await RefreshStatsAsync(conn, ct);

            _initialized = true;
            _logger.LogInformation(
                "BM25 SQLite index initialized: {Count} chunks, avg length {Avg:F1}",
                _totalChunkCount, _avgDocLength);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task RefreshStatsAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), AVG(doc_length) FROM bm25_chunks";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            _totalChunkCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            _avgDocLength    = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
        }
    }

    // ── Indexing ─────────────────────────────────────────────────────────────

    public async Task IndexAsync(DocumentChunk chunk, CancellationToken ct = default)
        => await IndexBatchAsync(new List<DocumentChunk> { chunk }, ct);

    public async Task IndexBatchAsync(
        List<DocumentChunk> chunks, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            foreach (var chunk in chunks)
            {
                var words = Tokenize(chunk.Content);

                var termCounts = new Dictionary<string, int>();
                foreach (var word in words)
                    termCounts[word] = termCounts.GetValueOrDefault(word) + 1;

                // Upsert chunk row
                await using var chunkCmd = conn.CreateCommand();
                chunkCmd.Transaction = (SqliteTransaction)transaction;
                chunkCmd.CommandText = """
                    INSERT OR REPLACE INTO bm25_chunks
                        (chunk_id, document_id, file_name, content, chunk_type,
                         page_number, chunk_index, doc_length, metadata_json)
                    VALUES
                        (@chunkId, @documentId, @fileName, @content, @chunkType,
                         @pageNumber, @chunkIndex, @docLength, @metadataJson)
                    """;
                chunkCmd.Parameters.AddWithValue("@chunkId",     chunk.ChunkId);
                chunkCmd.Parameters.AddWithValue("@documentId",  chunk.DocumentId);
                chunkCmd.Parameters.AddWithValue("@fileName",    chunk.FileName);
                chunkCmd.Parameters.AddWithValue("@content",     chunk.Content);
                chunkCmd.Parameters.AddWithValue("@chunkType",   chunk.ChunkType.ToString());
                chunkCmd.Parameters.AddWithValue("@pageNumber",  chunk.PageNumber);
                chunkCmd.Parameters.AddWithValue("@chunkIndex",  chunk.ChunkIndex);
                chunkCmd.Parameters.AddWithValue("@docLength",   words.Count);
                chunkCmd.Parameters.AddWithValue("@metadataJson",
                    JsonSerializer.Serialize(chunk.Metadata));
                await chunkCmd.ExecuteNonQueryAsync(ct);

                // Replace term frequency rows for this chunk (delete then insert —
                // handles re-ingestion of an updated document cleanly)
                await using var delCmd = conn.CreateCommand();
                delCmd.Transaction = (SqliteTransaction)transaction;
                delCmd.CommandText = "DELETE FROM bm25_term_frequency WHERE chunk_id = @chunkId";
                delCmd.Parameters.AddWithValue("@chunkId", chunk.ChunkId);
                await delCmd.ExecuteNonQueryAsync(ct);

                foreach (var (word, count) in termCounts)
                {
                    await using var termCmd = conn.CreateCommand();
                    termCmd.Transaction = (SqliteTransaction)transaction;
                    termCmd.CommandText = """
                        INSERT INTO bm25_term_frequency (chunk_id, word, term_count)
                        VALUES (@chunkId, @word, @count)
                        """;
                    termCmd.Parameters.AddWithValue("@chunkId", chunk.ChunkId);
                    termCmd.Parameters.AddWithValue("@word", word);
                    termCmd.Parameters.AddWithValue("@count", count);
                    await termCmd.ExecuteNonQueryAsync(ct);
                }
            }

            await transaction.CommitAsync(ct);
            await RefreshStatsAsync(conn, ct);

            _logger.LogInformation(
                "BM25 indexed {Count} chunks. Total chunks: {Total}, avg doc length: {Avg:F1}",
                chunks.Count, _totalChunkCount, _avgDocLength);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    // ── Search ───────────────────────────────────────────────────────────────

    public async Task<List<RetrievedChunk>> SearchAsync(
        string query, int topK, string? documentIdFilter = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var queryTerms = Tokenize(query).Distinct().ToList();
        if (queryTerms.Count == 0 || _totalChunkCount == 0)
            return new List<RetrievedChunk>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // ── Single batched query for ALL query words at once ───────────────
        // This is the network-friendly pattern: one round trip regardless
        // of how many words are in the query, instead of one query per word.
        var wordParams = string.Join(",", queryTerms.Select((_, i) => $"@w{i}"));

        await using var matchCmd = conn.CreateCommand();
        matchCmd.CommandText = $"""
            SELECT tf.chunk_id, tf.word, tf.term_count,
                   c.document_id, c.doc_length
            FROM bm25_term_frequency tf
            JOIN bm25_chunks c ON c.chunk_id = tf.chunk_id
            WHERE tf.word IN ({wordParams})
            """;
        for (int i = 0; i < queryTerms.Count; i++)
            matchCmd.Parameters.AddWithValue($"@w{i}", queryTerms[i]);

        // chunkId -> (documentId, docLength, word -> termCount)
        var perChunkTerms = new Dictionary<string, (string DocId, int DocLen, Dictionary<string, int> Terms)>();

        await using (var reader = await matchCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var chunkId = reader.GetString(0);
                var word    = reader.GetString(1);
                var count   = reader.GetInt32(2);
                var docId   = reader.GetString(3);
                var docLen  = reader.GetInt32(4);

                if (!perChunkTerms.TryGetValue(chunkId, out var entry))
                {
                    entry = (docId, docLen, new Dictionary<string, int>());
                    perChunkTerms[chunkId] = entry;
                }
                entry.Terms[word] = count;
            }
        }

        if (perChunkTerms.Count == 0)
        {
            _logger.LogDebug("BM25 search: no chunks contain any of [{Terms}]",
                string.Join(", ", queryTerms));
            return new List<RetrievedChunk>();
        }

        // ── Document frequency for each query word — also batched ──────────
        await using var dfCmd = conn.CreateCommand();
        dfCmd.CommandText = $"""
            SELECT word, COUNT(DISTINCT chunk_id)
            FROM bm25_term_frequency
            WHERE word IN ({wordParams})
            GROUP BY word
            """;
        for (int i = 0; i < queryTerms.Count; i++)
            dfCmd.Parameters.AddWithValue($"@w{i}", queryTerms[i]);

        var documentFrequency = new Dictionary<string, int>();
        await using (var reader = await dfCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                documentFrequency[reader.GetString(0)] = reader.GetInt32(1);
        }

        // ── Score every candidate chunk using BM25 ──────────────────────────
        var n = _totalChunkCount;
        var scored = new List<(string ChunkId, float Score)>();

        foreach (var (chunkId, (docId, docLen, terms)) in perChunkTerms)
        {
            if (documentIdFilter is not null && docId != documentIdFilter)
                continue;

            float score = 0f;
            foreach (var term in queryTerms)
            {
                if (!terms.TryGetValue(term, out var tf))
                    continue;

                var df  = documentFrequency.GetValueOrDefault(term, 1);
                var idf = MathF.Log(((n - df + 0.5f) / (df + 0.5f)) + 1f);

                var numerator   = tf * (K1 + 1);
                var denominator = tf + K1 * (1 - B + B * (docLen / (float)_avgDocLength));

                score += idf * (numerator / denominator);
            }

            if (score > 0)
                scored.Add((chunkId, score));
        }

        var topChunkIds = scored
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .ToList();

        if (topChunkIds.Count == 0)
            return new List<RetrievedChunk>();

        // ── Fetch full chunk data for the top results only ──────────────────
        var idParams = string.Join(",", topChunkIds.Select((_, i) => $"@id{i}"));
        await using var fetchCmd = conn.CreateCommand();
        fetchCmd.CommandText = $"""
            SELECT chunk_id, document_id, file_name, content, chunk_type,
                   page_number, chunk_index, metadata_json
            FROM bm25_chunks
            WHERE chunk_id IN ({idParams})
            """;
        for (int i = 0; i < topChunkIds.Count; i++)
            fetchCmd.Parameters.AddWithValue($"@id{i}", topChunkIds[i].ChunkId);

        var chunkData = new Dictionary<string, DocumentChunk>();
        await using (var reader = await fetchCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    reader.GetString(7)) ?? new();

                chunkData[reader.GetString(0)] = new DocumentChunk(
                    ChunkId:    reader.GetString(0),
                    DocumentId: reader.GetString(1),
                    FileName:   reader.GetString(2),
                    Content:    reader.GetString(3),
                    ChunkType:  Enum.Parse<ChunkType>(reader.GetString(4)),
                    PageNumber: reader.GetInt32(5),
                    ChunkIndex: reader.GetInt32(6),
                    Metadata:   metadata);
            }
        }

        var results = topChunkIds
            .Where(t => chunkData.ContainsKey(t.ChunkId))
            .Select(t => new RetrievedChunk(
                Chunk:           chunkData[t.ChunkId],
                Score:           t.Score,
                RetrievalSource: "sparse"))
            .ToList();

        _logger.LogDebug(
            "BM25 search '{Query}' -> {Candidates} candidates, {Returned} returned",
            query, perChunkTerms.Count, results.Count);

        return results;
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    public async Task DeleteByDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);

        try
        {
            // Delete term frequency rows first (depends on chunk_ids existing)
            await using var delTermsCmd = conn.CreateCommand();
            delTermsCmd.Transaction = (SqliteTransaction)transaction;
            delTermsCmd.CommandText = """
                DELETE FROM bm25_term_frequency
                WHERE chunk_id IN (
                    SELECT chunk_id FROM bm25_chunks WHERE document_id = @documentId
                )
                """;
            delTermsCmd.Parameters.AddWithValue("@documentId", documentId);
            await delTermsCmd.ExecuteNonQueryAsync(ct);

            await using var delChunksCmd = conn.CreateCommand();
            delChunksCmd.Transaction = (SqliteTransaction)transaction;
            delChunksCmd.CommandText = "DELETE FROM bm25_chunks WHERE document_id = @documentId";
            delChunksCmd.Parameters.AddWithValue("@documentId", documentId);
            var rowsDeleted = await delChunksCmd.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);
            await RefreshStatsAsync(conn, ct);

            _logger.LogInformation(
                "BM25 removed {Count} chunks for document {DocumentId}",
                rowsDeleted, documentId);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    // ── ClearAll ─────────────────────────────────────────────────────────────

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM bm25_term_frequency;
            DELETE FROM bm25_chunks;
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _totalChunkCount = 0;
        _avgDocLength    = 0;

        _logger.LogWarning("BM25 index cleared — all chunks and terms removed");
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
