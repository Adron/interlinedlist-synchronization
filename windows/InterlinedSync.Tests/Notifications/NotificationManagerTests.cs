using FluentAssertions;
using InterlinedSync.Configuration;
using InterlinedSync.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InterlinedSync.Tests.Notifications;

public sealed class NotificationManagerTests
{
    [Fact]
    public async Task NotifySyncCompletedAsync_ProducesOpenFolderAction()
    {
        var (manager, dispatcher) = CreateManager();

        await manager.NotifySyncCompletedAsync(3);

        var payload = dispatcher.LastPayload.Should().NotBeNull().And.Subject as NotificationPayload;
        payload!.Title.Should().Be(WindowsAppNotificationManager.SyncCompletedTitle);
        payload.Body.Should().Be("3 files changed.");
        payload.Actions.Should().ContainSingle()
            .Which.Argument.Should().Be(WindowsAppNotificationManager.OpenFolderArgument);
    }

    [Fact]
    public async Task NotifySyncCompletedAsync_UsesSingularGrammar()
    {
        var (manager, dispatcher) = CreateManager();

        await manager.NotifySyncCompletedAsync(1);

        dispatcher.LastPayload!.Body.Should().Be("1 file changed.");
    }

    [Fact]
    public async Task NotifySyncFailedAsync_IncludesRetryAndOpenLog()
    {
        var (manager, dispatcher) = CreateManager();

        await manager.NotifySyncFailedAsync("network down");

        var payload = dispatcher.LastPayload!;
        payload.Title.Should().Be(WindowsAppNotificationManager.SyncFailedTitle);
        payload.Body.Should().Be("network down");
        payload.Actions.Should().HaveCount(2);
        payload.Actions[0].Argument.Should().Be(WindowsAppNotificationManager.RetryArgument);
        payload.Actions[1].Argument.Should().Be(WindowsAppNotificationManager.OpenLogArgument);
    }

    [Fact]
    public async Task NotifyConflictCopyCreatedAsync_EncodesPathInAction()
    {
        var (manager, dispatcher) = CreateManager();
        const string path = @"C:\sync\Doc (conflicted copy).md";

        await manager.NotifyConflictCopyCreatedAsync(path);

        var payload = dispatcher.LastPayload!;
        payload.Title.Should().Be(WindowsAppNotificationManager.ConflictTitle);
        payload.Body.Should().Contain("Doc (conflicted copy).md");
        payload.Actions.Should().ContainSingle()
            .Which.Argument.Should().Be(WindowsAppNotificationManager.OpenFileArgumentPrefix + path);
    }

    [Fact]
    public async Task NotifyAuthExpiredAsync_IncludesSignInAction()
    {
        var (manager, dispatcher) = CreateManager();

        await manager.NotifyAuthExpiredAsync();

        var payload = dispatcher.LastPayload!;
        payload.Title.Should().Be(WindowsAppNotificationManager.AuthExpiredTitle);
        payload.Actions.Should().ContainSingle()
            .Which.Argument.Should().Be(WindowsAppNotificationManager.SignInArgument);
    }

    [Fact]
    public async Task NotifySyncCompletedAsync_Suppressed_WhenPreferenceDisabled()
    {
        var prefs = new SyncPreferences { NotifyOnSyncCompletion = false };
        var (manager, dispatcher) = CreateManager(prefs);

        await manager.NotifySyncCompletedAsync(5);

        dispatcher.LastPayload.Should().BeNull();
    }

    [Fact]
    public async Task NotifySyncFailedAsync_Suppressed_WhenErrorsDisabled()
    {
        var prefs = new SyncPreferences { NotifyOnErrors = false };
        var (manager, dispatcher) = CreateManager(prefs);

        await manager.NotifySyncFailedAsync("boom");

        dispatcher.LastPayload.Should().BeNull();
    }

    [Fact]
    public async Task NotifyConflictCopyCreatedAsync_Suppressed_WhenConflictsDisabled()
    {
        var prefs = new SyncPreferences { NotifyOnConflicts = false };
        var (manager, dispatcher) = CreateManager(prefs);

        await manager.NotifyConflictCopyCreatedAsync(@"C:\sync\x.md");

        dispatcher.LastPayload.Should().BeNull();
    }

    [Fact]
    public async Task NotifyAuthExpiredAsync_AlwaysFires()
    {
        var prefs = new SyncPreferences
        {
            NotifyOnSyncCompletion = false,
            NotifyOnErrors = false,
            NotifyOnConflicts = false,
        };
        var (manager, dispatcher) = CreateManager(prefs);

        await manager.NotifyAuthExpiredAsync();

        dispatcher.LastPayload.Should().NotBeNull();
    }

    private static (WindowsAppNotificationManager Manager, RecordingDispatcher Dispatcher) CreateManager(
        SyncPreferences? preferences = null)
    {
        var dispatcher = new RecordingDispatcher();
        var prefsMonitor = new TestOptionsMonitor<SyncPreferences>(preferences ?? new SyncPreferences());
        var manager = new WindowsAppNotificationManager(
            dispatcher,
            prefsMonitor,
            NullLogger<WindowsAppNotificationManager>.Instance);
        return (manager, dispatcher);
    }

    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public NotificationPayload? LastPayload { get; private set; }

        public Task ShowAsync(NotificationPayload payload, CancellationToken cancellationToken = default)
        {
            LastPayload = payload;
            return Task.CompletedTask;
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T current) => CurrentValue = current;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
