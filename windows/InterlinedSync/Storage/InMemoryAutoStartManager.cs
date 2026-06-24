namespace InterlinedSync.Storage;

/// <summary>
/// In-memory <see cref="IAutoStartManager"/> used on non-Windows hosts (so the
/// DI graph still resolves on macOS/Linux test runners) and in unit tests where
/// touching HKCU would be a side-effect.
/// </summary>
public sealed class InMemoryAutoStartManager : IAutoStartManager
{
    private bool _enabled;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_enabled);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _enabled = enabled;
        return Task.CompletedTask;
    }
}
