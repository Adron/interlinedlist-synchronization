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
}
