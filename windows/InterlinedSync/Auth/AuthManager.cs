using System.Net;
using InterlinedSync.API;
using InterlinedSync.Storage;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.Auth;

/// <summary>
/// Default <see cref="IAuthProvider"/>. Delegates HTTP to
/// <see cref="IInterlinedListClient"/> and token persistence to
/// <see cref="ICredentialStore"/> — neither concern leaks into this class.
/// </summary>
public sealed class AuthManager : IAuthProvider
{
    private readonly IInterlinedListClient _client;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<AuthManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedToken;
    private bool _cacheLoaded;

    public AuthManager(
        IInterlinedListClient client,
        ICredentialStore credentialStore,
        ILogger<AuthManager> logger)
    {
        _client = client;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_cacheLoaded)
            {
                _cachedToken = await _credentialStore.LoadTokenAsync(cancellationToken).ConfigureAwait(false);
                _cacheLoaded = true;
            }
            return _cachedToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SignInAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        try
        {
            var response = await _client.LoginAsync(username, password, cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _credentialStore.SaveTokenAsync(response.Token, cancellationToken).ConfigureAwait(false);
                _cachedToken = response.Token;
                _cacheLoaded = true;
            }
            finally
            {
                _gate.Release();
            }

            _logger.LogInformation("User {Username} signed in successfully.", username);
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            _logger.LogWarning(ex, "Sign-in failed for user {Username}: invalid credentials.", username);
            return false;
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "Sign-in for user {Username} failed with an unexpected API error.", username);
            throw new AuthException("Sign-in failed due to an API error.", ex);
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _credentialStore.DeleteTokenAsync(cancellationToken).ConfigureAwait(false);
            _cachedToken = null;
            _cacheLoaded = true;
            _logger.LogInformation("User signed out.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
