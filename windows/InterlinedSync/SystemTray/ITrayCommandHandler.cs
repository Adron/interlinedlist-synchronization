namespace InterlinedSync.SystemTray;

/// <summary>
/// Decouples the tray controller from the things its menu items do. Each
/// concrete action (open folder, show settings, sign out, exit) is dispatched
/// through this handler so the controller stays UI-only.
/// </summary>
public interface ITrayCommandHandler
{
    Task OpenSyncFolderAsync(CancellationToken cancellationToken = default);
    Task ShowSettingsAsync(CancellationToken cancellationToken = default);
    Task TogglePauseAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
    Task ExitAsync(CancellationToken cancellationToken = default);
}
