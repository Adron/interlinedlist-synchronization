using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.UI.ViewModels;

/// <summary>
/// ViewModel for the settings window. Loads preferences on construction and
/// writes them back through <see cref="IPreferencesStore"/> on save.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IPreferencesStore _preferencesStore;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _syncFolder = string.Empty;

    [ObservableProperty]
    private int _pollIntervalSeconds = AppConstants.DefaultPollIntervalSeconds;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    public SettingsViewModel(IPreferencesStore preferencesStore, ILogger<SettingsViewModel> logger)
    {
        _preferencesStore = preferencesStore;
        _logger = logger;
    }

    public int MinimumPollIntervalSeconds => AppConstants.MinimumPollIntervalSeconds;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var prefs = await _preferencesStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            SyncFolder = prefs.SyncFolder;
            PollIntervalSeconds = prefs.PollIntervalSeconds;
            AutoStart = prefs.AutoStart;
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
            };
            await _preferencesStore.SaveAsync(prefs, cancellationToken).ConfigureAwait(true);
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
}
