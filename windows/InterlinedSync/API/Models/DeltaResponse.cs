namespace InterlinedSync.API.Models;

/// <summary>
/// Response envelope from the delta endpoint. <see cref="Cursor"/> is sent back on
/// the next request to fetch only documents changed since the last sync.
/// </summary>
public sealed record DeltaResponse(
    IReadOnlyList<Document> Documents,
    IReadOnlyList<Folder> Folders,
    IReadOnlyList<string> DeletedDocumentIds,
    string? Cursor);
