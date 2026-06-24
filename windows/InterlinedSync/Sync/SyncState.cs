namespace InterlinedSync.Sync;

/// <summary>
/// Coarse-grained state used by the tray UI to pick which icon to display.
/// The full state machine (with error reasons and retry counts) lands in Phase 3.
/// </summary>
public enum SyncState
{
    Idle,
    Syncing,
    Paused,
    Error,
    SignedOut,
    Offline,
    AuthExpired,
}
