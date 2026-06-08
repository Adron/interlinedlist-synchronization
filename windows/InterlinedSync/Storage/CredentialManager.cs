using InterlinedSync.Configuration;
using Microsoft.Extensions.Logging;
using Windows.Security.Credentials;

namespace InterlinedSync.Storage;

/// <summary>
/// Windows Credential Manager-backed implementation of <see cref="ICredentialStore"/>.
/// Uses <see cref="PasswordVault"/> so the token is encrypted at rest by the OS and
/// scoped to the current Windows user account.
/// </summary>
/// <remarks>
/// PasswordVault throws when no credentials exist for the lookup — those exceptions
/// are caught and translated to a <c>null</c> result rather than bubbling up.
/// </remarks>
public sealed class CredentialManager : ICredentialStore
{
    private readonly ILogger<CredentialManager> _logger;
    private readonly string _resource;
    private readonly string _userName;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CredentialManager(ILogger<CredentialManager> logger)
        : this(logger, AppConstants.CredentialVaultResource, AppConstants.CredentialVaultTokenUser)
    {
    }

    internal CredentialManager(ILogger<CredentialManager> logger, string resource, string userName)
    {
        _logger = logger;
        _resource = resource;
        _userName = userName;
    }

    public async Task SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vault = new PasswordVault();
            // Remove any existing entry so we never accumulate duplicates.
            RemoveExisting(vault);
            vault.Add(new PasswordCredential(_resource, _userName, token));
            _logger.LogInformation("Stored session token in PasswordVault under resource {Resource}.", _resource);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> LoadTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vault = new PasswordVault();
            try
            {
                var credential = vault.Retrieve(_resource, _userName);
                credential.RetrievePassword();
                return credential.Password;
            }
            catch (Exception ex)
            {
                // PasswordVault throws a COMException when the entry is missing.
                _logger.LogDebug(ex, "No token found in PasswordVault for resource {Resource}.", _resource);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vault = new PasswordVault();
            RemoveExisting(vault);
            _logger.LogInformation("Removed session token from PasswordVault.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RemoveExisting(PasswordVault vault)
    {
        try
        {
            var credential = vault.Retrieve(_resource, _userName);
            vault.Remove(credential);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No existing credential to remove for {Resource}.", _resource);
        }
    }
}
