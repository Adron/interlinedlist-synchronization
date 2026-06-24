using FluentAssertions;
using InterlinedSync.Storage;
using InterlinedSync.UI;
using Xunit;

namespace InterlinedSync.Tests.UI;

/// <summary>
/// Pure-logic tests for the "should the autostart prompt show now?" matrix.
/// Uses <see cref="InMemoryAutoStartManager"/> so we exercise the real
/// interface contract without touching HKCU. Each fact corresponds to one
/// row of the decision table in <see cref="StartupPromptDecisionService"/>.
/// </summary>
public class StartupPromptDecisionServiceTests
{
    [Fact]
    public async Task ShouldPrompt_True_WhenNoEntryAndNotSuppressedAndInteractiveLaunch()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptDecisionService(autoStart);

        var actual = await sut.ShouldPromptAsync(Array.Empty<string>());

        actual.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldPrompt_False_WhenRunEntryAlreadyExists()
    {
        var autoStart = new InMemoryAutoStartManager();
        await autoStart.SetEnabledAsync(true);
        var sut = new StartupPromptDecisionService(autoStart);

        var actual = await sut.ShouldPromptAsync(Array.Empty<string>());

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldPrompt_False_WhenSuppressedFlagSet()
    {
        var autoStart = new InMemoryAutoStartManager();
        await autoStart.SetStartupPromptSuppressedAsync(true);
        var sut = new StartupPromptDecisionService(autoStart);

        var actual = await sut.ShouldPromptAsync(Array.Empty<string>());

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldPrompt_False_WhenLaunchedByInstaller()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptDecisionService(autoStart);

        var actual = await sut.ShouldPromptAsync(new[] { StartupPromptDecisionService.LaunchedByInstallerArg });

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldPrompt_False_WhenLaunchedByInstaller_CaseInsensitive()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptDecisionService(autoStart);

        var actual = await sut.ShouldPromptAsync(new[] { "--FROM-INSTALLER" });

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldPrompt_False_WhenInstallerArgPresentEvenWithOtherArgs()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptDecisionService(autoStart);

        var actual = await sut.ShouldPromptAsync(
            new[] { "--debug", StartupPromptDecisionService.LaunchedByInstallerArg, "--verbose" });

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldPrompt_False_WhenBothRunEntryAndSuppressedFlagSet()
    {
        var autoStart = new InMemoryAutoStartManager();
        await autoStart.SetEnabledAsync(true);
        await autoStart.SetStartupPromptSuppressedAsync(true);
        var sut = new StartupPromptDecisionService(autoStart);

        var actual = await sut.ShouldPromptAsync(Array.Empty<string>());

        actual.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldPrompt_ThrowsArgumentNullException_WhenArgsNull()
    {
        var autoStart = new InMemoryAutoStartManager();
        var sut = new StartupPromptDecisionService(autoStart);

        var act = async () => await sut.ShouldPromptAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
