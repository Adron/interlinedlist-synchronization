namespace InterlinedSync.Sync;

/// <summary>
/// Snapshot of a single document tracked by <see cref="ISyncStateRepository"/>.
/// Pure value type so it is safe to share across threads.
/// </summary>
/// <param name="Id">Server-assigned document identifier.</param>
/// <param name="LocalPath">Absolute path to the synced file on disk.</param>
/// <param name="ServerUpdatedAt">UTC timestamp of the most recent server revision we have downloaded.</param>
/// <param name="LocalModifiedAt">UTC timestamp of the local file at the time the record was written.</param>
/// <param name="Sha256">Hex-encoded SHA-256 of the document body as it was written to disk.</param>
public sealed record SyncStateRecord(
    string Id,
    string LocalPath,
    DateTimeOffset ServerUpdatedAt,
    DateTimeOffset LocalModifiedAt,
    string Sha256);
