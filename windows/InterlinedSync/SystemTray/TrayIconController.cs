using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using InterlinedSync.Sync;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.SystemTray;

/// <summary>
/// Owns the <see cref="TaskbarIcon"/> and translates <see cref="SyncState"/> changes
/// into icon swaps. Menu clicks are forwarded to <see cref="ITrayCommandHandler"/>;
/// no business logic lives in this class.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly ISyncStateNotifier _notifier;
    private readonly TrayMenuBuilder _menuBuilder;
    private readonly ITrayCommandHandler _commandHandler;
    private readonly ILogger<TrayIconController> _logger;
    private readonly Dictionary<SyncState, string> _iconPaths;

    private TaskbarIcon? _icon;
    private bool _disposed;

    public TrayIconController(
        ISyncStateNotifier notifier,
        TrayMenuBuilder menuBuilder,
        ITrayCommandHandler commandHandler,
        ILogger<TrayIconController> logger)
    {
        _notifier = notifier;
        _menuBuilder = menuBuilder;
        _commandHandler = commandHandler;
        _logger = logger;

        var baseDir = AppContext.BaseDirectory;
        _iconPaths = new Dictionary<SyncState, string>
        {
            [SyncState.Idle] = Path.Combine(baseDir, "Assets", "tray-idle.ico"),
            [SyncState.Syncing] = Path.Combine(baseDir, "Assets", "tray-syncing.ico"),
            [SyncState.Error] = Path.Combine(baseDir, "Assets", "tray-error.ico"),
            [SyncState.Paused] = Path.Combine(baseDir, "Assets", "tray-paused.ico"),
            [SyncState.SignedOut] = Path.Combine(baseDir, "Assets", "tray-idle.ico"),
        };
    }

    public void Initialize()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "InterlinedList Sync",
            Visibility = Visibility.Visible,
        };
        Refresh(_notifier.Current);
        _notifier.StateChanged += OnStateChanged;
        _logger.LogInformation("Tray icon initialized.");
    }

    private void OnStateChanged(object? sender, SyncState state)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Refresh(state));
        }
        else
        {
            Refresh(state);
        }
    }

    private void Refresh(SyncState state)
    {
        if (_icon is null)
        {
            return;
        }

        try
        {
            if (_iconPaths.TryGetValue(state, out var path) && File.Exists(path))
            {
                _icon.IconSource = new BitmapImage(new Uri(path, UriKind.Absolute));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to swap tray icon for state {State}.", state);
        }

        _icon.ContextMenu = BuildContextMenu(state);
        _icon.ToolTipText = $"InterlinedList Sync — {state}";
    }

    private ContextMenu BuildContextMenu(SyncState state)
    {
        var menu = new ContextMenu();
        foreach (var item in _menuBuilder.Build(state))
        {
            var mi = new MenuItem
            {
                Header = item.Header,
                IsEnabled = item.IsEnabled,
                Tag = item.Command,
            };
            mi.Click += async (_, _) => await DispatchAsync(item.Command).ConfigureAwait(false);
            menu.Items.Add(mi);
        }
        return menu;
    }

    private async Task DispatchAsync(TrayCommand command)
    {
        try
        {
            switch (command)
            {
                case TrayCommand.OpenSyncFolder:
                    await _commandHandler.OpenSyncFolderAsync().ConfigureAwait(false);
                    break;
                case TrayCommand.Settings:
                    await _commandHandler.ShowSettingsAsync().ConfigureAwait(false);
                    break;
                case TrayCommand.TogglePause:
                    await _commandHandler.TogglePauseAsync().ConfigureAwait(false);
                    break;
                case TrayCommand.SignOut:
                    await _commandHandler.SignOutAsync().ConfigureAwait(false);
                    break;
                case TrayCommand.Exit:
                    await _commandHandler.ExitAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray command {Command} failed.", command);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _notifier.StateChanged -= OnStateChanged;
        _icon?.Dispose();
    }
}
