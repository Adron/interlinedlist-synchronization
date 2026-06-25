using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
using Serilog;

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

        // ----- Step 1: Wire global exception handlers FIRST. -----
        // These must be installed before any other startup work runs so a
        // throw during BuildHost / host.StartAsync / DI resolution surfaces
        // a visible MessageBox instead of a silent exit. The v0.2.0 installer
        // shipped without this safety net and any failure here vanished into
        // the void — no tray icon, no onboarding, no error.
        RegisterGlobalExceptionHandlers();

        try
        {
            // ----- Step 2: Build host and seed the logger. -----
            // DI failures throw here; the outer try/catch catches them.
            _host = Program.BuildHost(_launchArgs);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("InterlinedList Sync starting up.");

            var auth = _host.Services.GetRequiredService<IAuthProvider>();
            var notifier = _host.Services.GetRequiredService<ISyncStateNotifier>();

            var token = await auth.GetTokenAsync().ConfigureAwait(true);
            var signedIn = !string.IsNullOrEmpty(token);

            // Seed the initial state before the tray subscribes so the very
            // first icon paint matches reality (signed-out shows the error
            // icon, signed-in shows the idle icon).
            notifier.SetState(signedIn ? SyncState.Idle : SyncState.SignedOut);

            // ----- Step 3: Bring the tray up BEFORE starting background services. -----
            // The tray is the user's only re-entry point if anything else
            // fails. If host startup throws later, the user still sees the
            // tray icon and can use "Sign in…" / "Exit". The earlier
            // OnStartup did host.StartAsync() first and InitializeTray()
            // second; any throw in StartAsync left the user staring at
            // nothing. Order matters here.
            InitializeTray();

            // ----- Step 4: Surface the onboarding window for signed-out users. -----
            // Switched from ShowDialog (modal) to Show (non-modal) so it
            // does NOT block the rest of OnStartup — specifically so we can
            // still call host.StartAsync() below while the user is signing
            // in. The tray's "Sign in…" menu item remains the persistent
            // re-entry point if the user dismisses the window.
            if (!signedIn)
            {
                _logger.LogInformation("No token in Credential Manager; presenting onboarding window.");
                ShowOnboardingWindow();
            }

            // ----- Step 5: Start the host (and the SyncEngine BackgroundService). -----
            // Wrapped in its own try/catch so a sync-engine startup failure
            // (e.g. SQLite open error, missing sync folder) cannot remove
            // the tray. We log the failure, flip the notifier to Error so
            // the tray icon is visibly broken, and leave the user with a
            // working menu so they can sign in, look at settings, or exit.
            try
            {
                await _host.StartAsync().ConfigureAwait(true);
            }
            catch (Exception hostEx)
            {
                _logger.LogError(hostEx, "Host failed to start; tray will stay alive in Error state.");
                try
                {
                    notifier.SetState(SyncState.Error);
                }
                catch (Exception notifierEx)
                {
                    _logger.LogWarning(notifierEx, "Could not flip notifier to Error after host start failure.");
                }

                ShowStartupFailureDialog("starting background services", hostEx);
                // Intentionally do NOT rethrow — the tray is up; the user
                // can sign out / exit cleanly via the menu.
            }

            // ----- Step 6: Skip the autostart prompt for signed-out users. -----
            if (!signedIn)
            {
                // The user hasn't authenticated yet; pestering them about
                // autostart now would be off-putting. We'll re-evaluate on
                // the next launch (and after a successful sign-in below).
                return;
            }

            // Workstream B: one-time "run at startup?" prompt. Non-modal,
            // fire-and-forget — the tray and sync loop are already live so
            // the prompt never blocks anything. Skip when the installer
            // auto-launched us, when the user already opted out, or when
            // the Run entry exists.
            _ = MaybeShowStartupPromptAsync();
        }
        catch (Exception ex)
        {
            // Last-resort catch: a throw escaped every inner try/catch. The
            // DispatcherUnhandledException handler would normally catch this
            // for code dispatched onto the UI thread, but OnStartup is an
            // async void so a synchronous throw here may not route through
            // it cleanly. Surface the error explicitly.
            HandleFatalException("building host", ex, exitAfter: true);
        }
    }

    /// <summary>
    /// Installs handlers for every place an unobserved exception can hide on
    /// .NET: the Dispatcher (UI-thread throws), the AppDomain (non-UI throws),
    /// and the TaskScheduler (forgotten <see cref="Task"/>s). Each handler
    /// logs the failure and shows a MessageBox so the user gets actionable
    /// feedback instead of silence.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (sender, args) =>
        {
            // Mark handled so WPF doesn't tear the process down immediately
            // after we show the dialog — the user gets a chance to read it
            // and decide whether to use the tray menu to exit cleanly.
            args.Handled = true;
            HandleFatalException("Dispatcher", args.Exception, exitAfter: false);
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            // IsTerminating == true means the CLR is on its way out; we can
            // still log + show the dialog, but cannot recover.
            HandleFatalException(
                args.IsTerminating ? "AppDomain (terminating)" : "AppDomain",
                ex,
                exitAfter: false);
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            args.SetObserved();
            HandleFatalException("TaskScheduler (unobserved Task)", args.Exception, exitAfter: false);
        };
    }

    /// <summary>
    /// Logs a fatal startup-time or runtime exception via Serilog (falls back
    /// to <see cref="Log.Logger"/> if the DI container failed before we could
    /// resolve <see cref="ILogger{T}"/>) and shows a MessageBox pointing at
    /// the log directory. Never throws.
    /// </summary>
    /// <param name="stage">Short label for the failing stage; appears in the
    /// MessageBox title and in the log entry.</param>
    /// <param name="exception">The fatal exception. <see langword="null"/> is
    /// tolerated and produces a generic "unknown error" message.</param>
    /// <param name="exitAfter">When <see langword="true"/> the app is shut
    /// down after the dialog closes — used for the last-resort OnStartup
    /// catch where the host never came up. Other surfaces (Dispatcher,
    /// AppDomain, TaskScheduler) keep the tray alive and let the user exit
    /// via the menu when convenient.</param>
    private void HandleFatalException(string stage, Exception? exception, bool exitAfter)
    {
        try
        {
            if (_logger is not null)
            {
                _logger.LogError(exception, "Fatal exception during {Stage}.", stage);
            }
            else
            {
                // Serilog's static logger is configured before Program builds
                // the host (see Program.ConfigureSerilog), so this works even
                // when DI has not produced an ILogger<App> yet — except in
                // the corner case where BuildHost throws BEFORE ConfigureSerilog
                // runs, in which case the catch block below quietly swallows
                // the logging failure rather than recursing.
                Log.Logger.Error(exception, "Fatal exception during {Stage}.", stage);
            }
        }
        catch
        {
            // Never let the failure-reporter itself crash the failure path.
        }

        try
        {
            ShowStartupFailureDialog(stage, exception);
        }
        catch
        {
            // Same defensive principle — if MessageBox.Show throws (e.g. no
            // interactive desktop) we silently skip rather than recurse.
        }

        if (exitAfter)
        {
            try
            {
                Shutdown(1);
            }
            catch
            {
                Environment.Exit(1);
            }
        }
    }

    /// <summary>
    /// Renders the user-facing error message via
    /// <see cref="StartupFailureReporter.FormatMessage"/> and shows it in a
    /// modal <see cref="MessageBox"/> on the UI thread. Marshals via
    /// <see cref="Dispatcher"/> when called from a worker.
    /// </summary>
    private void ShowStartupFailureDialog(string stage, Exception? exception)
    {
        var logDir = StartupFailureReporter.GetDefaultLogDirectory();
        var body = StartupFailureReporter.FormatMessage(stage, exception, logDir);

        void Show()
        {
            MessageBox.Show(
                body,
                StartupFailureReporter.DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        if (Dispatcher.CheckAccess())
        {
            Show();
        }
        else
        {
            Dispatcher.Invoke(Show);
        }
    }

    /// <summary>
    /// Shows the onboarding window (non-modal). Safe to call from any state:
    /// if a window is already visible it is just activated. On a successful
    /// sign-in the sync state is flipped to <see cref="SyncState.Idle"/>.
    /// </summary>
    /// <remarks>
    /// This is intentionally non-modal: <see cref="OnStartup"/> calls it for
    /// signed-out users BEFORE <see cref="IHost.StartAsync"/>, and a modal
    /// <c>ShowDialog</c> there would block host startup until the user
    /// clicked through the window. With <c>Show</c> the sync engine boots in
    /// the background while the user signs in, and the tray icon — the only
    /// guaranteed re-entry point — is already live underneath either way.
    /// The window exposes its own <see cref="OnboardingWindow.SignInSucceeded"/>
    /// flag (set just before <see cref="Window.Close"/>) so the "sign-in
    /// succeeded" branch below still fires correctly; we cannot use
    /// <see cref="Window.DialogResult"/> here because that property is only
    /// settable when the window was opened modally.
    /// </remarks>
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
                // SignInSucceeded replaces DialogResult here — DialogResult
                // is only settable when the window is shown modally and we
                // now use Show() instead of ShowDialog() (see remarks on
                // ShowOnboardingWindow). The OnboardingWindow sets this
                // flag itself before calling Close().
                var succeeded = window.SignInSucceeded;
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
            window.Show();
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
