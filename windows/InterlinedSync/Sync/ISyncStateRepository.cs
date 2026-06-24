namespace InterlinedSync.Sync;

/// <summary>
/// Persistent store of the sync engine's view of every document on disk.
/// Backed by SQLite in production; mocked in tests.
/// </summary>
/// <remarks>
/// Kept narrow (ISP) — the sync engine only needs CRUD over the documents
/// table and an append-only audit log.
/// </remarks>
public interface ISyncStateRepository
{
    /// <summary>
    /// Ensures the schema exists. Safe to call multiple times; uses
    /// <c>CREATE TABLE IF NOT EXISTS</c> internally.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the record for the given document.
    /// </summary>
    Task UpsertDocumentAsync(SyncStateRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the record for the given document, or <c>null</c> if it is not tracked.
    /// </summary>
    Task<SyncStateRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the record whose <see cref="SyncStateRecord.LocalPath"/> matches
    /// the given path (case-insensitive on Windows), or <c>null</c>.
    /// </summary>
    Task<SyncStateRecord?> GetByPathAsync(string localPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the record for the given document id. No-op if absent.
    /// </summary>
    Task DeleteByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every tracked document. Used by the pull engine to detect
    /// server-side deletions.
    /// </summary>
    Task<IReadOnlyList<SyncStateRecord>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a row to the <c>sync_log</c> table for diagnostics.
    /// </summary>
    Task AppendLogAsync(string @event, string detail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <see cref="API.Models.DeltaResponse.SyncedAt"/> persisted by the most
    /// recent successful delta pull, or <c>null</c> on the first ever sync.
    /// </summary>
    Task<DateTimeOffset?> GetLastSyncedAtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the <see cref="API.Models.DeltaResponse.SyncedAt"/> timestamp that should be
    /// sent as <c>?lastSyncAt=</c> on the next delta call.
    /// </summary>
    Task SetLastSyncedAtAsync(DateTimeOffset syncedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears every row from <c>documents</c>, <c>folders</c>, <c>sync_log</c>
    /// and <c>sync_metadata</c>. Used by the settings "Reset state" affordance
    /// to force a clean re-sync without touching auth or local files.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
