using InterlinedSync.Sync;

namespace InterlinedSync.SystemTray;

/// <summary>
/// Builds a flat list of <see cref="TrayMenuItem"/>s for a given sync state.
/// The actual WPF <c>ContextMenu</c> is constructed in <see cref="TrayIconController"/>
/// from this declarative output so the layout is testable in isolation.
/// </summary>
public sealed class TrayMenuBuilder
{
    public IReadOnlyList<TrayMenuItem> Build(SyncState state)
    {
        var signedIn = state != SyncState.SignedOut;
        var pauseHeader = state == SyncState.Paused ? "Resume Sync" : "Pause Sync";

        return new[]
        {
            new TrayMenuItem("Open Sync Folder", TrayCommand.OpenSyncFolder, IsEnabled: signedIn),
            new TrayMenuItem("Settings", TrayCommand.Settings, IsEnabled: signedIn),
            new TrayMenuItem(pauseHeader, TrayCommand.TogglePause, IsEnabled: signedIn),
            new TrayMenuItem("Sign Out", TrayCommand.SignOut, IsEnabled: signedIn),
            new TrayMenuItem("Exit", TrayCommand.Exit),
        };
    }
}
