namespace InterlinedSync.Sync;

public sealed record SyncStateRecord(
    string Id,
    string LocalPath,
    DateTimeOffset ServerUpdatedAt,
    DateTimeOffset LocalModifiedAt,
    string Sha256,
    DateTimeOffset? LastConflictAt = null);
