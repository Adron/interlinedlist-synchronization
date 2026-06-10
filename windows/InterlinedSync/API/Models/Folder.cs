namespace InterlinedSync.API.Models;

/// <summary>
/// Server folder representation. The folder tree is reconstructed locally by
/// joining <see cref="ParentId"/> chains.
/// </summary>
public sealed record Folder(
    string Id,
    string Name,
    string? ParentId);
