using System.Collections.Concurrent;

namespace InterlinedSync.Notifications.Mocks;

/// <summary>
/// Records every notification call instead of posting to the OS. Used by tests
/// to assert that the sync engine fires toasts at the right moments.
/// </summary>
public sealed class MockNotificationManager : INotificationManager
{
    private readonly ConcurrentQueue<NotificationCall> _calls = new();

    public IReadOnlyCollection<NotificationCall> Calls => _calls.ToArray();

    public Task NotifySyncCompletedAsync(int changedCount, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new NotificationCall(NotificationKind.SyncCompleted, changedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Task.CompletedTask;
    }

    public Task NotifySyncFailedAsync(string reason, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new NotificationCall(NotificationKind.SyncFailed, reason));
        return Task.CompletedTask;
    }

    public Task NotifyConflictCopyCreatedAsync(string filename, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new NotificationCall(NotificationKind.ConflictCopy, filename));
        return Task.CompletedTask;
    }

    public Task NotifyAuthExpiredAsync(CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new NotificationCall(NotificationKind.AuthExpired, null));
        return Task.CompletedTask;
    }
}

public enum NotificationKind
{
    SyncCompleted,
    SyncFailed,
    ConflictCopy,
    AuthExpired,
}

public sealed record NotificationCall(NotificationKind Kind, string? Detail);
