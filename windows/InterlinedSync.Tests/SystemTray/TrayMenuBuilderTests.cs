using FluentAssertions;
using InterlinedSync.Sync;
using InterlinedSync.SystemTray;
using Xunit;

namespace InterlinedSync.Tests.SystemTray;

public class TrayMenuBuilderTests
{
    [Fact]
    public void Build_ProducesExpectedCommands_InOrder()
    {
        var builder = new TrayMenuBuilder();
        var items = builder.Build(SyncState.Idle);

        items.Select(i => i.Command).Should().Equal(
            TrayCommand.OpenSyncFolder,
            TrayCommand.Settings,
            TrayCommand.TogglePause,
            TrayCommand.SignOut,
            TrayCommand.Exit);
    }

    [Fact]
    public void Build_PauseHeaderIsResume_WhenPaused()
    {
        var builder = new TrayMenuBuilder();
        var items = builder.Build(SyncState.Paused);
        items.Single(i => i.Command == TrayCommand.TogglePause).Header.Should().Be("Resume Sync");
    }

    [Fact]
    public void Build_PauseHeaderIsPause_WhenIdle()
    {
        var builder = new TrayMenuBuilder();
        var items = builder.Build(SyncState.Idle);
        items.Single(i => i.Command == TrayCommand.TogglePause).Header.Should().Be("Pause Sync");
    }

    [Fact]
    public void Build_DisablesAuthenticatedCommands_WhenSignedOut()
    {
        var builder = new TrayMenuBuilder();
        var items = builder.Build(SyncState.SignedOut);

        items.Single(i => i.Command == TrayCommand.OpenSyncFolder).IsEnabled.Should().BeFalse();
        items.Single(i => i.Command == TrayCommand.Settings).IsEnabled.Should().BeFalse();
        items.Single(i => i.Command == TrayCommand.SignOut).IsEnabled.Should().BeFalse();
        items.Single(i => i.Command == TrayCommand.TogglePause).IsEnabled.Should().BeFalse();
        items.Single(i => i.Command == TrayCommand.Exit).IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(SyncState.Idle)]
    [InlineData(SyncState.Syncing)]
    [InlineData(SyncState.Error)]
    [InlineData(SyncState.Paused)]
    public void Build_EnablesAuthenticatedCommands_WhenSignedIn(SyncState state)
    {
        var builder = new TrayMenuBuilder();
        var items = builder.Build(state);

        items.Single(i => i.Command == TrayCommand.OpenSyncFolder).IsEnabled.Should().BeTrue();
        items.Single(i => i.Command == TrayCommand.Settings).IsEnabled.Should().BeTrue();
        items.Single(i => i.Command == TrayCommand.SignOut).IsEnabled.Should().BeTrue();
    }
}
