namespace InterlinedSync.Storage;

/// <summary>
/// Persists the email/identity of the currently signed-in account so the
/// settings UI can display "Signed in as <email>". Intentionally separate
/// from <see cref="ICredentialStore"/> (which holds the bearer token) so the
/// UI never has to depend on the credential surface.
/// </summary>
public interface IAccountStore
{
    Task<string?> GetSignedInEmailAsync(CancellationToken cancellationToken = default);

    Task SetSignedInEmailAsync(string email, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
