namespace InterlinedSync.Auth;

/// <summary>
/// Abstraction over the sign-in / token-storage flow. Kept narrow (ISP) so the
/// HTTP layer only depends on token retrieval, while UI code depends on sign-in.
/// </summary>
public interface IAuthProvider
{
    /// <summary>
    /// Returns the currently stored session token, or <c>null</c> if the user is
    /// not signed in. Does not perform any HTTP requests.
    /// </summary>
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges credentials for a session token and persists it. Returns true on
    /// success, false on any authentication failure. Network errors propagate as
    /// <see cref="AuthException"/>.
    /// </summary>
    Task<bool> SignInAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the cached token. Safe to call when the user is not signed in.
    /// </summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
