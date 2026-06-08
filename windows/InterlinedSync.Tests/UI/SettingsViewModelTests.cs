using FluentAssertions;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using InterlinedSync.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InterlinedSync.Tests.UI;

public class SettingsViewModelTests
{
    private static SettingsViewModel Build(Mock<IPreferencesStore> store)
        => new(store.Object, NullLogger<SettingsViewModel>.Instance);

    [Fact]
    public async Task LoadAsync_PopulatesObservableProperties()
    {
        var store = new Mock<IPreferencesStore>();
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SyncPreferences
        {
            SyncFolder = @"C:\sync",
            PollIntervalSeconds = 60,
            AutoStart = true,
        });
        var vm = Build(store);

        await vm.LoadAsync();

        vm.SyncFolder.Should().Be(@"C:\sync");
        vm.PollIntervalSeconds.Should().Be(60);
        vm.AutoStart.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCommand_PersistsValues()
    {
        var store = new Mock<IPreferencesStore>();
        SyncPreferences? captured = null;
        store.Setup(s => s.SaveAsync(It.IsAny<SyncPreferences>(), It.IsAny<CancellationToken>()))
             .Callback<SyncPreferences, CancellationToken>((p, _) => captured = p)
             .Returns(Task.CompletedTask);
        var vm = Build(store);
        vm.SyncFolder = @"C:\target";
        vm.PollIntervalSeconds = 45;
        vm.AutoStart = true;

        await vm.SaveCommand.ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.SyncFolder.Should().Be(@"C:\target");
        captured.PollIntervalSeconds.Should().Be(45);
        captured.AutoStart.Should().BeTrue();
        vm.StatusMessage.Should().Be("Saved.");
    }

    [Fact]
    public async Task SaveCommand_RejectsBelowMinimumInterval()
    {
        var store = new Mock<IPreferencesStore>();
        var vm = Build(store);
        vm.SyncFolder = @"C:\target";
        vm.PollIntervalSeconds = AppConstants.MinimumPollIntervalSeconds - 1;

        await vm.SaveCommand.ExecuteAsync(null);

        store.Verify(s => s.SaveAsync(It.IsAny<SyncPreferences>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.StatusMessage.Should().Contain($"{AppConstants.MinimumPollIntervalSeconds}");
    }

    [Fact]
    public async Task SaveCommand_RejectsEmptySyncFolder()
    {
        var store = new Mock<IPreferencesStore>();
        var vm = Build(store);
        vm.SyncFolder = "";

        await vm.SaveCommand.ExecuteAsync(null);

        store.Verify(s => s.SaveAsync(It.IsAny<SyncPreferences>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.StatusMessage.Should().Contain("required");
    }
}
