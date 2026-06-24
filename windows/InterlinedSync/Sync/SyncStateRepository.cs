using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.Sync;

/// <summary>
/// SQLite-backed implementation of <see cref="ISyncStateRepository"/>.
/// All access is serialized through a single <see cref="SemaphoreSlim"/>;
/// SQLite itself tolerates concurrent connections but the engine has a single
/// writer so the gate keeps the surface trivially safe.
/// </summary>
/// <remarks>
/// Cross-platform — uses no Windows-only types — so the same class powers tests
/// on macOS / Linux.
/// </remarks>
public sealed class SyncStateRepository : ISyncStateRepository, IAsyncDisposable
{
    // Round-trip ISO-8601 with offset; matches DateTimeOffset.Parse default.
    private const string TimestampFormat = "O";

    private const string LastSyncedAtKey = "last_synced_at";

    private readonly string _connectionString;
    private readonly ILogger<SyncStateRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SyncStateRepository(string databasePath, ILogger<SyncStateRepository> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS documents (
                        id TEXT PRIMARY KEY,
                        local_path TEXT NOT NULL,
                        server_updated_at TEXT NOT NULL,
                        local_modified_at TEXT NOT NULL,
                        sha256 TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_documents_local_path ON documents(local_path);

                    CREATE TABLE IF NOT EXISTS folders (
                        id TEXT PRIMARY KEY,
                        local_path TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS sync_log (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts TEXT NOT NULL,
                        event TEXT NOT NULL,
                        detail TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS sync_metadata (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    """;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await MigrateAddLastConflictAtAsync(connection, cancellationToken).ConfigureAwait(false);

            _initialized = true;
            _logger.LogInformation("Sync state DB initialized at {ConnectionString}.", _connectionString);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertDocumentAsync(SyncStateRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO documents (id, local_path, server_updated_at, local_modified_at, sha256, last_conflict_at)
                VALUES ($id, $path, $server, $local, $sha, $conflict)
                ON CONFLICT(id) DO UPDATE SET
                    local_path = excluded.local_path,
                    server_updated_at = excluded.server_updated_at,
                    local_modified_at = excluded.local_modified_at,
                    sha256 = excluded.sha256,
                    last_conflict_at = excluded.last_conflict_at;
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$path", record.LocalPath);
            cmd.Parameters.AddWithValue("$server", record.ServerUpdatedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$local", record.LocalModifiedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$sha", record.Sha256);
            cmd.Parameters.AddWithValue("$conflict", record.LastConflictAt is { } c
                ? c.ToString(TimestampFormat, CultureInfo.InvariantCulture)
                : (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncStateRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, local_path, server_updated_at, local_modified_at, sha256, last_conflict_at
                FROM documents
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyncStateRecord?> GetByPathAsync(string localPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, local_path, server_updated_at, local_modified_at, sha256, last_conflict_at
                FROM documents
                WHERE local_path = $path COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$path", localPath);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM documents WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncStateRecord>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, local_path, server_updated_at, local_modified_at, sha256, last_conflict_at
                FROM documents;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var list = new List<SyncStateRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(Read(reader));
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendLogAsync(string @event, string detail, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(@event);
        detail ??= string.Empty;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_log (ts, event, detail)
                VALUES ($ts, $event, $detail);
                """;
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$event", @event);
            cmd.Parameters.AddWithValue("$detail", detail);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DateTimeOffset?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM sync_metadata WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", LastSyncedAtKey);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null || result is DBNull)
            {
                return null;
            }
            var text = (string)result;
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetLastSyncedAtAsync(DateTimeOffset syncedAt, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_metadata (key, value)
                VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$key", LastSyncedAtKey);
            cmd.Parameters.AddWithValue("$value", syncedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateAddLastConflictAtAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('documents') WHERE name = 'last_conflict_at';";
        var present = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (present > 0)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE documents ADD COLUMN last_conflict_at TEXT;";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SyncStateRecord Read(SqliteDataReader reader)
    {
        DateTimeOffset? lastConflict = null;
        if (!reader.IsDBNull(5))
        {
            lastConflict = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        return new SyncStateRecord(
            Id: reader.GetString(0),
            LocalPath: reader.GetString(1),
            ServerUpdatedAt: DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            LocalModifiedAt: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Sha256: reader.GetString(4),
            LastConflictAt: lastConflict);
    }
}
