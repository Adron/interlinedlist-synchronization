namespace InterlinedSync.API.Models;

/// <summary>
/// Response envelope from <c>GET /api/documents/sync</c>. <see cref="SyncedAt"/>
/// is persisted by the engine and sent back as <c>?lastSyncAt=</c> on the next
/// call so the server only returns documents that changed since the last sync.
/// </summary>
public sealed record DeltaResponse(
    DateTimeOffset SyncedAt,
    IReadOnlyList<Folder> Folders,
    IReadOnlyList<DocumentDelta> Documents);

/// <summary>
/// A single document entry in a delta response. When <see cref="Deleted"/> is
/// <c>true</c> the server omits <see cref="Content"/> — the entry is a tombstone
/// and the engine should remove the local file and the tracking record.
/// </summary>
public sealed record DocumentDelta(
    string Id,
    string Title,
    string? Content,
    string? FolderId,
    DateTimeOffset UpdatedAt,
    bool Deleted);
