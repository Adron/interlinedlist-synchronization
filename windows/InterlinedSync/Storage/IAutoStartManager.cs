namespace InterlinedSync.Storage;

/// <summary>
/// Toggles whether the app launches on user login. Backed by the per-user
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key on Windows;
/// on non-Windows hosts an in-memory double is registered so tests and the
/// cross-platform build still resolve the dependency.
/// </summary>
public interface IAutoStartManager
{
    /// <summary>
    /// Returns the current registration state. <c>false</c> if the registry
    /// value is absent or pointing at a different executable.
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes the registry value. Idempotent.
    /// </summary>
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
