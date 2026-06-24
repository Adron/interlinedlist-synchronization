namespace InterlinedSync.Storage;

/// <summary>
/// In-memory <see cref="IAutoStartManager"/> used on non-Windows hosts (so the
/// DI graph still resolves on macOS/Linux test runners) and in unit tests where
/// touching HKCU would be a side-effect.
/// </summary>
public sealed class InMemoryAutoStartManager : IAutoStartManager
{
    private bool _enabled;
    private bool _managedByInstaller;
    private bool _promptSuppressed;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_enabled);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _enabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> IsManagedByInstallerAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_enabled && _managedByInstaller);

    public Task<bool> IsStartupPromptSuppressedAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_promptSuppressed);

    public Task SetStartupPromptSuppressedAsync(bool suppressed, CancellationToken cancellationToken = default)
    {
        _promptSuppressed = suppressed;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test-only hook so unit tests can stage the "installer set the Run
    /// entry" scenario without writing to HKCU.
    /// </summary>
    internal void SetManagedByInstaller(bool managed)
    {
        _managedByInstaller = managed;
    }
}
