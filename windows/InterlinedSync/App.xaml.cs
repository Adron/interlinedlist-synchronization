using System.Diagnostics;
using System.IO;
using System.Windows;
using InterlinedSync.Auth;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using InterlinedSync.Sync;
using InterlinedSync.SystemTray;
using InterlinedSync.UI.ViewModels;
using InterlinedSync.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterlinedSync;

/// <summary>
/// WPF entry point. Hosts the <see cref="IHost"/> built by <see cref="Program"/>
/// and drives the start-up flow: if no token is present we show the onboarding
/// window; once signed in we hand off to the tray controller.
/// </summary>
public partial class App : Application, ITrayCommandHandler
{
    private IHost? _host;
    private TrayIconController? _tray;
    private SettingsWindow? _settingsWindow;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = Program.BuildHost(e.Args);
        await _host.StartAsync().ConfigureAwait(true);

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("InterlinedList Sync starting up.");

        var auth = _host.Services.GetRequiredService<IAuthProvider>();
        var notifier = _host.Services.GetRequiredService<ISyncStateNotifier>();

        var token = await auth.GetTokenAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(token))
        {
            notifier.SetState(SyncState.SignedOut);
            if (!ShowOnboardingWindow())
            {
                Shutdown();
                return;
            }
        }

        notifier.SetState(SyncState.Idle);
        InitializeTray();
    }

    private bool ShowOnboardingWindow()
    {
        if (_host is null)
        {
            return false;
        }

        var vm = _host.Services.GetRequiredService<OnboardingViewModel>();
        var prefs = _host.Services.GetRequiredService<Storage.IPreferencesStore>();
        var window = new OnboardingWindow(vm, prefs);
        var result = window.ShowDialog();
        return result == true;
    }

    private void InitializeTray()
    {
        if (_host is null)
        {
            return;
        }

        var notifier = _host.Services.GetRequiredService<ISyncStateNotifier>();
        var menuBuilder = _host.Services.GetRequiredService<TrayMenuBuilder>();
        var logger = _host.Services.GetRequiredService<ILogger<TrayIconController>>();
        _tray = new TrayIconController(notifier, menuBuilder, this, logger);
        _tray.Initialize();
    }

    public Task OpenSyncFolderAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
        {
            return Task.CompletedTask;
        }

        var prefs = _host.Services.GetRequiredService<IOptions<SyncPreferences>>().Value;
        var folder = prefs.SyncFolder;
        if (string.IsNullOrEmpty(folder))
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    public Task ShowSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
        {
            return Task.CompletedTask;
        }

        Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is null || !_settingsWindow.IsLoaded)
            {
                var vm = _host.Services.GetRequiredService<SettingsViewModel>();
                _settingsWindow = new SettingsWindow(vm);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.Activate();
            }
        });
        return Task.CompletedTask;
    }

    public Task TogglePauseAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
        {
            return Task.CompletedTask;
        }

        var notifier = _host.Services.GetRequiredService<ISyncStateNotifier>();
        notifier.SetState(notifier.Current == SyncState.Paused ? SyncState.Idle : SyncState.Paused);
        return Task.CompletedTask;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
        {
            return;
        }

        var auth = _host.Services.GetRequiredService<IAuthProvider>();
        await auth.SignOutAsync(cancellationToken).ConfigureAwait(true);

        var notifier = _host.Services.GetRequiredService<ISyncStateNotifier>();
        notifier.SetState(SyncState.SignedOut);

        Dispatcher.Invoke(() =>
        {
            if (ShowOnboardingWindow())
            {
                notifier.SetState(SyncState.Idle);
            }
            else
            {
                Shutdown();
            }
        });
    }

    public Task ExitAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.Invoke(() => Shutdown());
        return Task.CompletedTask;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("InterlinedList Sync shutting down.");
        _tray?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(true);
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
