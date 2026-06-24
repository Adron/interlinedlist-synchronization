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

    /// <summary>
    /// Returns <c>true</c> when the HKCU Run entry exists AND points at an
    /// executable inside the standard Program Files install location — i.e.
    /// the installer wrote it, not the user. Used by the one-time startup
    /// prompt to skip itself when the installer already opted the user in.
    /// </summary>
    /// <remarks>
    /// On non-Windows hosts (or when the Run value is absent) this always
    /// returns <c>false</c>. A heuristic — there is no authoritative way to
    /// distinguish installer-written from app-written values — but combined
    /// with the suppressed-flag check it keeps the prompt out of the user's
    /// way after an installer-driven first launch.
    /// </remarks>
    Task<bool> IsManagedByInstallerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when the user has previously chosen "Don't ask
    /// again" on the startup prompt. Backed by
    /// <c>HKCU\Software\InterlinedSync\StartupPromptSuppressed</c>.
    /// </summary>
    Task<bool> IsStartupPromptSuppressedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the "don't ask again" flag. Idempotent.
    /// </summary>
    Task SetStartupPromptSuppressedAsync(bool suppressed, CancellationToken cancellationToken = default);
}
