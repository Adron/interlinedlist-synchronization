using FluentAssertions;
using InterlinedSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.Sync;

public class SyncStateNotifierTests
{
    [Fact]
    public void SetState_RaisesEvent_OnTransition()
    {
        var notifier = new SyncStateNotifier(NullLogger<SyncStateNotifier>.Instance);
        var observed = new List<SyncState>();
        notifier.StateChanged += (_, s) => observed.Add(s);

        notifier.SetState(SyncState.Idle);
        notifier.SetState(SyncState.Syncing);

        observed.Should().Equal(SyncState.Idle, SyncState.Syncing);
        notifier.Current.Should().Be(SyncState.Syncing);
    }

    [Fact]
    public void SetState_DoesNotRaise_WhenStateUnchanged()
    {
        var notifier = new SyncStateNotifier(NullLogger<SyncStateNotifier>.Instance);
        notifier.SetState(SyncState.Idle);

        var fired = 0;
        notifier.StateChanged += (_, _) => fired++;
        notifier.SetState(SyncState.Idle);

        fired.Should().Be(0);
    }

    [Fact]
    public void SetState_SwallowsSubscriberExceptions()
    {
        var notifier = new SyncStateNotifier(NullLogger<SyncStateNotifier>.Instance);
        notifier.StateChanged += (_, _) => throw new InvalidOperationException("boom");
        var observed = SyncState.SignedOut;
        notifier.StateChanged += (_, s) => observed = s;

        var act = () => notifier.SetState(SyncState.Idle);

        act.Should().NotThrow();
        observed.Should().Be(SyncState.Idle);
    }

    [Fact]
    public void Current_StartsAsSignedOut()
    {
        var notifier = new SyncStateNotifier(NullLogger<SyncStateNotifier>.Instance);
        notifier.Current.Should().Be(SyncState.SignedOut);
    }
}
