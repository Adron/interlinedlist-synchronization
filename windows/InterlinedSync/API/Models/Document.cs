namespace InterlinedSync.API.Models;

/// <summary>
/// Server document representation used by the sync engine.
/// </summary>
public sealed record Document(
    string Id,
    string Title,
    string? FolderId,
    string Content,
    DateTimeOffset UpdatedAt);
