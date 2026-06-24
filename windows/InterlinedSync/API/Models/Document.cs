using System.Text.Json.Serialization;

namespace InterlinedSync.API.Models;

public sealed record Document(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("folderId")] string? FolderId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("contentHash")] string? ContentHash = null);
