namespace InterlinedSync.FileSystem;

/// <summary>
/// Bidirectional map between a server document and a local Markdown file.
/// The mapping is persisted in <see cref="Sync.ISyncStateRepository"/>, but this
/// interface exposes only the parts the sync engine needs (ISP).
/// </summary>
public interface IFileMapper
{
    /// <summary>
    /// Returns the absolute local path that should be used for the document with
    /// the given title. Re-used during creation and detection of renames.
    /// </summary>
    /// <param name="syncFolder">Absolute path to the user's sync folder.</param>
    /// <param name="title">Server-side document title; may contain characters that
    /// are illegal in Windows filenames.</param>
    string GetLocalPath(string syncFolder, string title);

    /// <summary>
    /// Looks up the document id previously mapped to the given absolute path.
    /// Returns <c>null</c> if the path is not tracked.
    /// </summary>
    Task<string?> GetDocumentIdForPathAsync(string localPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the absolute local path mapped to the given document id.
    /// Returns <c>null</c> if the document is not tracked.
    /// </summary>
    Task<string?> GetPathForDocumentIdAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sanitizes a server-side title into a safe Windows filename without an extension.
    /// Exposed primarily for testing.
    /// </summary>
    string SanitizeTitle(string title);

    /// <summary>
    /// Returns the conflict-copy path that should be used to preserve a divergent
    /// local edit. Format: <c>&lt;basename&gt;.conflict-&lt;yyyyMMddTHHmmss&gt;.md</c>
    /// next to the original file.
    /// </summary>
    string GetConflictPath(string originalPath, DateTimeOffset conflictAt);
}
