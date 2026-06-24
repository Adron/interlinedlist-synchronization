using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterlinedSync.Auth;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using InterlinedSync.Sync;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.UI.ViewModels;

/// <summary>
/// ViewModel backing the four tabs of the Settings window (General, Account,
/// Notifications, Advanced). All persistence flows through
/// <see cref="IPreferencesStore"/>; auto-start state lives in
/// <see cref="IAutoStartManager"/>; account identity lives in
/// <see cref="IAccountStore"/>. The view binds directly to the observable
/// properties below — no XAML logic.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IPreferencesStore _preferencesStore;
    private readonly IAutoStartManager _autoStartManager;
    private readonly IAccountStore _accountStore;
    private readonly IAuthProvider _authProvider;
    private readonly ISyncStateRepository _stateRepository;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _syncFolder = string.Empty;

    [ObservableProperty]
    private int _pollIntervalSeconds = AppConstants.DefaultPollIntervalSeconds;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private bool _notificationsEnabled = true;

    [ObservableProperty]
    private bool _notifyOnSyncCompletion = true;

    [ObservableProperty]
    private bool _notifyOnErrors = true;

    [ObservableProperty]
    private bool _notifyOnConflicts = true;

    [ObservableProperty]
    private string _signedInEmail = string.Empty;

    [ObservableProperty]
    private string _preferencesFilePath = string.Empty;

    [ObservableProperty]
    private string _logFolderPath = string.Empty;

    [ObservableProperty]
    private string _versionDisplay = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _signedOut;

    [ObservableProperty]
    private string? _statusMessage;

    public SettingsViewModel(
        IPreferencesStore preferencesStore,
        IAutoStartManager autoStartManager,
        IAccountStore accountStore,
        IAuthProvider authProvider,
        ISyncStateRepository stateRepository,
        ILogger<SettingsViewModel> logger)
    {
        _preferencesStore = preferencesStore;
        _autoStartManager = autoStartManager;
        _accountStore = accountStore;
        _authProvider = authProvider;
        _stateRepository = stateRepository;
        _logger = logger;

        PreferencesFilePath = preferencesStore.PreferencesFilePath;
        LogFolderPath = ResolveDefaultLogFolder();
        VersionDisplay = ResolveVersion();
    }

    public int MinimumPollIntervalSeconds => AppConstants.MinimumPollIntervalSeconds;
    public int MaximumPollIntervalSeconds => 300;

    /// <summary>
    /// Raised after a successful sign-out so the host window can close itself
    /// and the app can re-prompt for credentials.
    /// </summary>
    public event EventHandler? SignedOutRequested;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var prefs = await _preferencesStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            SyncFolder = prefs.SyncFolder;
            PollIntervalSeconds = prefs.PollIntervalSeconds;
            AutoStart = prefs.AutoStart;
            NotifyOnSyncCompletion = prefs.NotifyOnSyncCompletion;
            NotifyOnErrors = prefs.NotifyOnErrors;
            NotifyOnConflicts = prefs.NotifyOnConflicts;
            NotificationsEnabled = prefs.NotifyOnSyncCompletion || prefs.NotifyOnErrors || prefs.NotifyOnConflicts;

            var registryAutoStart = await _autoStartManager.IsEnabledAsync(cancellationToken).ConfigureAwait(true);
            AutoStart = AutoStart || registryAutoStart;

            var email = await _accountStore.GetSignedInEmailAsync(cancellationToken).ConfigureAwait(true);
            SignedInEmail = email ?? string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (PollIntervalSeconds < AppConstants.MinimumPollIntervalSeconds)
        {
            StatusMessage = $"Poll interval must be at least {AppConstants.MinimumPollIntervalSeconds} seconds.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SyncFolder))
        {
            StatusMessage = "Sync folder is required.";
            return;
        }

        IsBusy = true;
        try
        {
            var prefs = new SyncPreferences
            {
                SyncFolder = SyncFolder,
                PollIntervalSeconds = PollIntervalSeconds,
                AutoStart = AutoStart,
                NotifyOnSyncCompletion = NotificationsEnabled && NotifyOnSyncCompletion,
                NotifyOnErrors = NotificationsEnabled && NotifyOnErrors,
                NotifyOnConflicts = NotificationsEnabled && NotifyOnConflicts,
            };
            await _preferencesStore.SaveAsync(prefs, cancellationToken).ConfigureAwait(true);
            await _autoStartManager.SetEnabledAsync(AutoStart, cancellationToken).ConfigureAwait(true);
            StatusMessage = "Saved.";
            _logger.LogInformation("Settings saved by user.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save preferences.");
            StatusMessage = "Could not save settings.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _authProvider.SignOutAsync(cancellationToken).ConfigureAwait(true);
            await _accountStore.ClearAsync(cancellationToken).ConfigureAwait(true);
            await _stateRepository.ResetAsync(cancellationToken).ConfigureAwait(true);
            SignedInEmail = string.Empty;
            SignedOut = true;
            StatusMessage = "Signed out.";
            SignedOutRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-out failed.");
            StatusMessage = "Sign out failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetStateAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _stateRepository.ResetAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = "Sync state cleared. Next sync will be a full pull.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset state failed.");
            StatusMessage = "Could not reset sync state.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        TryOpenInExplorer(LogFolderPath);
    }

    [RelayCommand]
    private void OpenSyncFolder()
    {
        TryOpenInExplorer(SyncFolder);
    }

    private void TryOpenInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open folder {Path}.", path);
            StatusMessage = "Could not open folder.";
        }
    }

    private static string ResolveDefaultLogFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, AppConstants.AppDataFolderName, AppConstants.LogsFolderName);
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrEmpty(informational) ? version : informational;
    }
}
