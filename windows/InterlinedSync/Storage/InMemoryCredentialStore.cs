namespace InterlinedSync.Storage;

/// <summary>
/// Non-Windows fallback / test double for <see cref="ICredentialStore"/>. The
/// token is held in process memory only and is lost on app shutdown.
/// </summary>
/// <remarks>
/// On Windows the DI container should always prefer <c>CredentialManager</c>;
/// this implementation exists so the core libraries remain testable on
/// non-Windows CI agents.
/// </remarks>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    public async Task SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _token = token;
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
            return _token;
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
            _token = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
