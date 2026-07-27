using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartDocQA.Application.Configuration;
using SmartDocQA.Domain.Interfaces;

namespace SmartDocQA.Infrastructure.Registry;

/// <summary>
/// SQLite-based document registry.
/// Stores ingestion history in a lightweight local database.
/// Database file is created automatically on first run.
/// </summary>
public class SqliteDocumentRegistry : IDocumentRegistry, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDocumentRegistry> _logger;
    private bool _initialized = false;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public SqliteDocumentRegistry(
        IOptions<RegistryOptions> options,
        ILogger<SqliteDocumentRegistry> logger)
    {
        _logger = logger;

        var dbPath = Path.IsPathRooted(options.Value.DatabasePath)
            ? options.Value.DatabasePath
            : Path.Combine(AppContext.BaseDirectory, options.Value.DatabasePath);

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        _logger.LogInformation("Document registry database: {Path}", dbPath);
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
                CREATE TABLE IF NOT EXISTS ingested_documents (
                    file_path       TEXT    NOT NULL PRIMARY KEY,
                    document_id     TEXT    NOT NULL,
                    file_name       TEXT    NOT NULL,
                    file_size_bytes INTEGER NOT NULL,
                    ingested_at     TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_document_id
                    ON ingested_documents(document_id);
                """;

            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
            _logger.LogInformation("Document registry initialized");
        }
        finally { _initLock.Release(); }
    }

    // ── IsIngested ────────────────────────────────────────────────────────────

    public async Task<bool> IsIngestedAsync(
        string filePath, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1) FROM ingested_documents
            WHERE file_path = @filePath
            """;
        cmd.Parameters.AddWithValue("@filePath", NormalizePath(filePath));

        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }

    // ── Register ──────────────────────────────────────────────────────────────

    public async Task RegisterAsync(
        string filePath, string documentId, string fileName,
        long fileSizeBytes, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO ingested_documents
                (file_path, document_id, file_name, file_size_bytes, ingested_at)
            VALUES
                (@filePath, @documentId, @fileName, @fileSizeBytes, @ingestedAt)
            """;

        cmd.Parameters.AddWithValue("@filePath",      NormalizePath(filePath));
        cmd.Parameters.AddWithValue("@documentId",    documentId);
        cmd.Parameters.AddWithValue("@fileName",      fileName);
        cmd.Parameters.AddWithValue("@fileSizeBytes", fileSizeBytes);
        cmd.Parameters.AddWithValue("@ingestedAt",    DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("Registered: {FileName} → {DocumentId}", fileName, documentId);
    }

    // ── GetAll ────────────────────────────────────────────────────────────────

    public async Task<List<RegistryEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT file_path, document_id, file_name, file_size_bytes, ingested_at
            FROM ingested_documents
            ORDER BY ingested_at DESC
            """;

        var entries = new List<RegistryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            entries.Add(new RegistryEntry(
                FilePath:      reader.GetString(0),
                DocumentId:    reader.GetString(1),
                FileName:      reader.GetString(2),
                FileSizeBytes: reader.GetInt64(3),
                IngestedAt:    DateTime.Parse(reader.GetString(4))));
        }

        return entries;
    }

    // ── Unregister ────────────────────────────────────────────────────────────

    public async Task UnregisterAsync(
        string documentId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM ingested_documents
            WHERE document_id = @documentId
            """;
        cmd.Parameters.AddWithValue("@documentId", documentId);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("Unregistered document: {DocumentId}", documentId);
    }

    // ── ClearAll ──────────────────────────────────────────────────────────────

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ingested_documents";
        var deleted = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogWarning(
            "Registry cleared — {Count} records deleted", deleted);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
