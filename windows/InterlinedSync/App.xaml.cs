using System.Diagnostics;
using System.IO;
using System.Windows;
using InterlinedSync.Auth;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using InterlinedSync.Sync;
using InterlinedSync.SystemTray;
using InterlinedSync.UI;
using InterlinedSync.UI.ViewModels;
using InterlinedSync.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterlinedSync;

/// <summary>
/// WPF entry point. Hosts the <see cref="IHost"/> built by <see cref="Program"/>
/// and drives the start-up flow: the tray comes up first (so the signed-out icon
/// is visible immediately), then — if no token is in Credential Manager — the
/// onboarding window is shown. Either way the user can later re-open the
/// onboarding window via the tray "Sign in…" menu item.
/// </summary>
public partial class App : Application, ITrayCommandHandler
{
    private IHost? _host;
    private TrayIconController? _tray;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboardingWindow;
    private StartupPromptDialog? _startupPromptDialog;
    private ILogger<App>? _logger;
    private string[] _launchArgs = Array.Empty<string>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _launchArgs = e.Args ?? Array.Empty<string>();
        _host = Program.BuildHost(_launchArgs);
        await _host.StartAsync().ConfigureAwait(true);

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("InterlinedList Sync starting up.");

        var auth = _host.Services.GetRequiredService<IAuthProvider>();
        var notifier = _host.Services.GetRequiredService<ISyncStateNotifier>();

        var token = await auth.GetTokenAsync().ConfigureAwait(true);
        var signedIn = !string.IsNullOrEmpty(token);

        // Seed the initial state before the tray subscribes so the very first
        // icon paint matches reality (signed-out shows the error icon, signed-in
        // shows the idle icon).
        notifier.SetState(signedIn ? SyncState.Idle : SyncState.SignedOut);

        // The tray is brought up unconditionally — this is the user's only
        // re-entry point if they dismiss the onboarding window without signing
        // in. The tray menu's "Sign in…" item is enabled in this state.
        InitializeTray();

        if (!signedIn)
        {
            _logger.LogInformation("No token in Credential Manager; presenting onboarding window.");
            ShowOnboardingWindow();
            // Skip the startup prompt entirely when signed-out — the user
            // hasn't even authenticated yet; pestering them about autostart
            // would be off-putting. We'll re-evaluate the next launch.
            return;
        }

        // Workstream B: one-time "run at startup?" prompt. Non-modal, fire-
        // and-forget — the tray and sync loop are already live so the prompt
        // never blocks anything. Skip when the installer auto-launched us,
        // when the user already opted out, or when the Run entry exists.
        _ = MaybeShowStartupPromptAsync();
    }

    /// <summary>
    /// Shows the onboarding window (modal). Safe to call from any state:
    /// if a window is already visible it is just activated. On a successful
    /// sign-in the sync state is flipped to <see cref="SyncState.Idle"/>.
    /// </summary>
    private void ShowOnboardingWindow()
    {
        if (_host is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (_onboardingWindow is { IsLoaded: true } existing)
            {
                existing.Activate();
                return;
            }

            var vm = _host.Services.GetRequiredService<OnboardingViewModel>();
            var prefs = _host.Services.GetRequiredService<IPreferencesStore>();
            var window = new OnboardingWindow(vm, prefs);
            _onboardingWindow = window;
            window.Closed += (_, _) =>
            {
                var succeeded = window.DialogResult == true;
                if (ReferenceEquals(_onboardingWindow, window))
                {
                    _onboardingWindow = null;
                }
                if (succeeded)
                {
                    var notifier = _host.Services.GetRequiredService<ISyncStateNotifier>();
                    notifier.SetState(SyncState.Idle);
                    // Now that the user is signed in, re-evaluate whether to
                    // surface the autostart prompt. Fire-and-forget — the
                    // prompt itself is non-modal.
                    _ = MaybeShowStartupPromptAsync();
                }
            };
            window.ShowDialog();
        });
    }

    /// <summary>
    /// Evaluates the startup-prompt rules via
    /// <see cref="StartupPromptDecisionService"/> and, if the conditions hold,
    /// pops the <see cref="StartupPromptDialog"/>. Safe to call multiple times —
    /// only one dialog is alive at a time, and after the user picks any option
    /// the rules will short-circuit subsequent calls.
    /// </summary>
    private async Task MaybeShowStartupPromptAsync()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            var decision = _host.Services.GetRequiredService<StartupPromptDecisionService>();
            var shouldPrompt = await decision.ShouldPromptAsync(_launchArgs).ConfigureAwait(true);
            if (!shouldPrompt)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (_startupPromptDialog is { IsLoaded: true } existing)
                {
                    existing.Activate();
                    return;
                }

                var vm = _host.Services.GetRequiredService<StartupPromptViewModel>();
                var dialog = new StartupPromptDialog(vm);
                _startupPromptDialog = dialog;
                dialog.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_startupPromptDialog, dialog))
                    {
                        _startupPromptDialog = null;
                    }
                };
                // Non-modal: Show() not ShowDialog(). Tray + sync engine
                // continue running underneath while the user decides.
                dialog.Show();
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not evaluate startup prompt.");
        }
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

    public Task SignInAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Tray Sign-in invoked.");
        ShowOnboardingWindow();
        return Task.CompletedTask;
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

        // After sign-out the tray stays alive in its signed-out state. Surface
        // the onboarding window once so the user can re-authenticate; if they
        // dismiss it the tray's "Sign in…" entry is still the way back in.
        Dispatcher.Invoke(ShowOnboardingWindow);
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
