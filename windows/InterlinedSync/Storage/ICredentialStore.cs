namespace InterlinedSync.Storage;

/// <summary>
/// Persists the session token outside of the process so it survives restarts.
/// On Windows this is backed by <c>PasswordVault</c>; in tests an in-memory
/// double is substituted.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Stores or replaces the session token. Implementations must overwrite any
    /// previously stored value for the same resource/user pair.
    /// </summary>
    Task SaveTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the previously stored token or <c>null</c> if none is present.
    /// </summary>
    Task<string?> LoadTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the stored token. Safe to call when no token is present.
    /// </summary>
    Task DeleteTokenAsync(CancellationToken cancellationToken = default);
}
