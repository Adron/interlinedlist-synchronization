namespace InterlinedSync.Notifications;

/// <summary>
/// Posts user-facing toast notifications for the standard sync lifecycle events.
/// Implementations decide the delivery mechanism (Windows App SDK on the desktop,
/// a no-op stub in tests).
/// </summary>
public interface INotificationManager
{
    Task NotifySyncCompletedAsync(int changedCount, CancellationToken cancellationToken = default);

    Task NotifySyncFailedAsync(string reason, CancellationToken cancellationToken = default);

    Task NotifyConflictCopyCreatedAsync(string filename, CancellationToken cancellationToken = default);

    Task NotifyAuthExpiredAsync(CancellationToken cancellationToken = default);
}
