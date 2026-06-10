using FluentAssertions;
using InterlinedSync.Sync;
using InterlinedSync.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.UI;

public class SyncStatusViewModelTests
{
    [Fact]
    public void State_TracksNotifierTransitions()
    {
        var notifier = new SyncStateNotifier(NullLogger<SyncStateNotifier>.Instance);
        using var vm = new SyncStatusViewModel(notifier);

        notifier.SetState(SyncState.Syncing);

        vm.State.Should().Be(SyncState.Syncing);
    }

    [Fact]
    public void Dispose_DetachesFromNotifier()
    {
        var notifier = new SyncStateNotifier(NullLogger<SyncStateNotifier>.Instance);
        var vm = new SyncStatusViewModel(notifier);
        vm.Dispose();

        notifier.SetState(SyncState.Idle);

        vm.State.Should().Be(SyncState.SignedOut);
    }
}
