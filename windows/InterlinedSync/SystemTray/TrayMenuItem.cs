namespace InterlinedSync.SystemTray;

/// <summary>
/// Logical representation of a tray menu entry. The builder produces these as
/// plain data so the menu layout can be unit-tested without a WPF dependency.
/// </summary>
public sealed record TrayMenuItem(
    string Header,
    TrayCommand Command,
    bool IsEnabled = true);

public enum TrayCommand
{
    SignIn,
    OpenSyncFolder,
    Settings,
    TogglePause,
    SignOut,
    Exit,
}
