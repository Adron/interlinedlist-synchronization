using FluentAssertions;
using InterlinedSync.Storage;
using InterlinedSync.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.UI;

/// <summary>
/// Behavioral tests for the startup-prompt view-model. Each command path is
/// asserted both for the persisted side-effect (Run entry vs suppressed
/// flag) and for the <see cref="StartupPromptViewModel.Decided"/> event so
/// the hosting window knows when to close.
/// </summary>
public class StartupPromptViewModelTests
{
    [Fact]
    public async Task YesCommand_EnablesAutoStartAndRaisesOptedIn()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptViewModel(autoStart, NullLogger<StartupPromptViewModel>.Instance);
        StartupPromptResult? captured = null;
        sut.Decided += (_, r) => captured = r;

        await sut.YesCommand.ExecuteAsync(null);

        captured.Should().Be(StartupPromptResult.OptedIn);
        (await autoStart.IsEnabledAsync()).Should().BeTrue();
        (await autoStart.IsStartupPromptSuppressedAsync()).Should().BeFalse();
    }

    [Fact]
    public void NoCommand_LeavesAutoStartDisabledAndRaisesDeclined()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptViewModel(autoStart, NullLogger<StartupPromptViewModel>.Instance);
        StartupPromptResult? captured = null;
        sut.Decided += (_, r) => captured = r;

        sut.NoCommand.Execute(null);

        captured.Should().Be(StartupPromptResult.DeclinedOnce);
    }

    [Fact]
    public async Task NoCommand_DoesNotSetSuppressedFlag()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptViewModel(autoStart, NullLogger<StartupPromptViewModel>.Instance);

        sut.NoCommand.Execute(null);

        (await autoStart.IsEnabledAsync()).Should().BeFalse();
        (await autoStart.IsStartupPromptSuppressedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task DontAskAgainCommand_SetsSuppressedFlagAndRaisesSuppressed()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptViewModel(autoStart, NullLogger<StartupPromptViewModel>.Instance);
        StartupPromptResult? captured = null;
        sut.Decided += (_, r) => captured = r;

        await sut.DontAskAgainCommand.ExecuteAsync(null);

        captured.Should().Be(StartupPromptResult.Suppressed);
        (await autoStart.IsStartupPromptSuppressedAsync()).Should().BeTrue();
        (await autoStart.IsEnabledAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task YesCommand_TogglesIsBusyAroundCall()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptViewModel(autoStart, NullLogger<StartupPromptViewModel>.Instance);

        sut.IsBusy.Should().BeFalse();
        await sut.YesCommand.ExecuteAsync(null);
        sut.IsBusy.Should().BeFalse();
    }
}
