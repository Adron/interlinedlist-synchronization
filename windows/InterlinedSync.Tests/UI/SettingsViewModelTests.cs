using FluentAssertions;
using InterlinedSync.Auth;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using InterlinedSync.Sync;
using InterlinedSync.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InterlinedSync.Tests.UI;

public class SettingsViewModelTests
{
    private static SettingsViewModel Build(
        Mock<IPreferencesStore>? store = null,
        Mock<IAutoStartManager>? autoStart = null,
        Mock<IAccountStore>? account = null,
        Mock<IAuthProvider>? auth = null,
        Mock<ISyncStateRepository>? repo = null)
    {
        if (store is null)
        {
            store = new Mock<IPreferencesStore>();
            store.Setup(s => s.PreferencesFilePath).Returns(@"C:\app\appsettings.json");
            store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SyncPreferences { SyncFolder = @"C:\sync" });
        }

        autoStart ??= new Mock<IAutoStartManager>();
        account ??= new Mock<IAccountStore>();
        auth ??= new Mock<IAuthProvider>();
        repo ??= new Mock<ISyncStateRepository>();

        return new SettingsViewModel(
            store.Object,
            autoStart.Object,
            account.Object,
            auth.Object,
            repo.Object,
            NullLogger<SettingsViewModel>.Instance);
    }

    [Fact]
    public async Task LoadAsync_PopulatesObservableProperties()
    {
        var store = new Mock<IPreferencesStore>();
        store.Setup(s => s.PreferencesFilePath).Returns(@"C:\app\appsettings.json");
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SyncPreferences
        {
            SyncFolder = @"C:\sync",
            PollIntervalSeconds = 60,
            AutoStart = true,
            NotifyOnSyncCompletion = true,
            NotifyOnErrors = false,
            NotifyOnConflicts = true,
        });
        var account = new Mock<IAccountStore>();
        account.Setup(a => a.GetSignedInEmailAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync("alice@example.com");
        var vm = Build(store: store, account: account);

        await vm.LoadAsync();

        vm.SyncFolder.Should().Be(@"C:\sync");
        vm.PollIntervalSeconds.Should().Be(60);
        vm.AutoStart.Should().BeTrue();
        vm.NotifyOnSyncCompletion.Should().BeTrue();
        vm.NotifyOnErrors.Should().BeFalse();
        vm.NotifyOnConflicts.Should().BeTrue();
        vm.NotificationsEnabled.Should().BeTrue();
        vm.SignedInEmail.Should().Be("alice@example.com");
        vm.PreferencesFilePath.Should().Be(@"C:\app\appsettings.json");
        vm.LogFolderPath.Should().NotBeNullOrEmpty();
        vm.VersionDisplay.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoadAsync_DerivesAutoStart_FromRegistryWhenPrefSaysFalse()
    {
        var store = new Mock<IPreferencesStore>();
        store.Setup(s => s.PreferencesFilePath).Returns(@"C:\app\appsettings.json");
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SyncPreferences
        {
            SyncFolder = @"C:\sync",
            AutoStart = false,
        });
        var autoStart = new Mock<IAutoStartManager>();
        autoStart.Setup(a => a.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var vm = Build(store: store, autoStart: autoStart);

        await vm.LoadAsync();

        vm.AutoStart.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCommand_PersistsValues_AndUpdatesAutoStart()
    {
        var store = new Mock<IPreferencesStore>();
        store.Setup(s => s.PreferencesFilePath).Returns(@"C:\app\appsettings.json");
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new SyncPreferences { SyncFolder = @"C:\sync" });
        SyncPreferences? captured = null;
        store.Setup(s => s.SaveAsync(It.IsAny<SyncPreferences>(), It.IsAny<CancellationToken>()))
             .Callback<SyncPreferences, CancellationToken>((p, _) => captured = p)
             .Returns(Task.CompletedTask);
        var autoStart = new Mock<IAutoStartManager>();
        var vm = Build(store: store, autoStart: autoStart);
        vm.SyncFolder = @"C:\target";
        vm.PollIntervalSeconds = 45;
        vm.AutoStart = true;
        vm.NotificationsEnabled = true;
        vm.NotifyOnSyncCompletion = false;
        vm.NotifyOnErrors = true;
        vm.NotifyOnConflicts = true;

        await vm.SaveCommand.ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.SyncFolder.Should().Be(@"C:\target");
        captured.PollIntervalSeconds.Should().Be(45);
        captured.AutoStart.Should().BeTrue();
        captured.NotifyOnSyncCompletion.Should().BeFalse();
        captured.NotifyOnErrors.Should().BeTrue();
        captured.NotifyOnConflicts.Should().BeTrue();
        autoStart.Verify(a => a.SetEnabledAsync(true, It.IsAny<CancellationToken>()), Times.Once);
        vm.StatusMessage.Should().Be("Saved.");
    }

    [Fact]
    public async Task SaveCommand_DisablesAllNotifications_WhenMasterToggleOff()
    {
        var store = new Mock<IPreferencesStore>();
        store.Setup(s => s.PreferencesFilePath).Returns(@"C:\app\appsettings.json");
        store.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new SyncPreferences { SyncFolder = @"C:\sync" });
        SyncPreferences? captured = null;
        store.Setup(s => s.SaveAsync(It.IsAny<SyncPreferences>(), It.IsAny<CancellationToken>()))
             .Callback<SyncPreferences, CancellationToken>((p, _) => captured = p)
             .Returns(Task.CompletedTask);
        var vm = Build(store: store);
        vm.SyncFolder = @"C:\target";
        vm.NotificationsEnabled = false;
        vm.NotifyOnSyncCompletion = true;
        vm.NotifyOnErrors = true;
        vm.NotifyOnConflicts = true;

        await vm.SaveCommand.ExecuteAsync(null);

        captured!.NotifyOnSyncCompletion.Should().BeFalse();
        captured.NotifyOnErrors.Should().BeFalse();
        captured.NotifyOnConflicts.Should().BeFalse();
    }

    [Fact]
    public async Task SaveCommand_RejectsBelowMinimumInterval()
    {
        var store = new Mock<IPreferencesStore>();
        store.Setup(s => s.PreferencesFilePath).Returns(@"C:\app\appsettings.json");
        var vm = Build(store: store);
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
        store.Setup(s => s.PreferencesFilePath).Returns(@"C:\app\appsettings.json");
        var vm = Build(store: store);
        vm.SyncFolder = "";

        await vm.SaveCommand.ExecuteAsync(null);

        store.Verify(s => s.SaveAsync(It.IsAny<SyncPreferences>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.StatusMessage.Should().Contain("required");
    }

    [Fact]
    public async Task SignOutCommand_ClearsTokenAccountAndState()
    {
        var auth = new Mock<IAuthProvider>();
        var account = new Mock<IAccountStore>();
        var repo = new Mock<ISyncStateRepository>();
        var vm = Build(auth: auth, account: account, repo: repo);

        var signedOutRaised = false;
        vm.SignedOutRequested += (_, _) => signedOutRaised = true;

        await vm.SignOutCommand.ExecuteAsync(null);

        auth.Verify(a => a.SignOutAsync(It.IsAny<CancellationToken>()), Times.Once);
        account.Verify(a => a.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.ResetAsync(It.IsAny<CancellationToken>()), Times.Once);
        vm.SignedInEmail.Should().BeEmpty();
        signedOutRaised.Should().BeTrue();
    }

    [Fact]
    public async Task ResetStateCommand_CallsRepositoryAndReportsStatus()
    {
        var repo = new Mock<ISyncStateRepository>();
        var vm = Build(repo: repo);

        await vm.ResetStateCommand.ExecuteAsync(null);

        repo.Verify(r => r.ResetAsync(It.IsAny<CancellationToken>()), Times.Once);
        vm.StatusMessage.Should().Contain("cleared");
    }
}
