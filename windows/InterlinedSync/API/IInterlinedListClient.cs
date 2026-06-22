using InterlinedSync.API.Models;

namespace InterlinedSync.API;

/// <summary>
/// All HTTP traffic against interlinedlist.com goes through this interface.
/// Kept focused (ISP) and fully testable (LSP) — every async method takes a
/// <see cref="CancellationToken"/> and returns either <see cref="Task"/> or
/// <see cref="Task{TResult}"/>.
/// </summary>
public interface IInterlinedListClient
{
    /// <summary>
    /// Exchanges username/password for a Bearer token. This is the only call that
    /// does not require an existing token; subsequent calls attach it automatically.
    /// </summary>
    Task<LoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the metadata for every document the authenticated user can read.
    /// Used by the pull engine to detect server-side additions and deletions.
    /// </summary>
    /// <remarks>
    /// Phase 3 fetches the full list every poll cycle. Phase 4+ will switch to a
    /// delta endpoint with a cursor once the server exposes one.
    /// </remarks>
    Task<IReadOnlyList<Document>> GetDocumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single document, including its body, by id. Used by the pull
    /// engine when it needs to (re)download content.
    /// </summary>
    Task<Document> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default);
}
