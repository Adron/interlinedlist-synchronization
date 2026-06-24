using System.Text.Json.Serialization;

namespace InterlinedSync.API.Models;

public sealed record DeltaResponse(
    [property: JsonPropertyName("lastSyncAt")] DateTimeOffset SyncedAt,
    [property: JsonPropertyName("folders")] IReadOnlyList<Folder> Folders,
    [property: JsonPropertyName("documents")] IReadOnlyList<DocumentDelta> Documents);

public sealed record DocumentDelta(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("folderId")] string? FolderId,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("deletedAt")] DateTimeOffset? DeletedAt = null)
{
    [JsonIgnore]
    public bool IsDeleted => DeletedAt.HasValue;
}
