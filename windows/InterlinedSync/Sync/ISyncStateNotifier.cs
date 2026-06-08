namespace InterlinedSync.Sync;

/// <summary>
/// Publishes sync state transitions to interested observers (tray icon, settings UI).
/// Kept separate from the sync engine itself so the engine can be unit-tested without
/// any UI coupling.
/// </summary>
public interface ISyncStateNotifier
{
    SyncState Current { get; }

    event EventHandler<SyncState>? StateChanged;

    void SetState(SyncState state);
}
