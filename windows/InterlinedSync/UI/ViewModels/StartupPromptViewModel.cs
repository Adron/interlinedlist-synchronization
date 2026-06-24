using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterlinedSync.Storage;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.UI.ViewModels;

/// <summary>
/// The one-time "Run InterlinedList Sync automatically when you sign in?"
/// dialog's view-model. The dialog itself is non-modal and surfaced only when
/// <see cref="StartupPromptDecisionService"/> decides the conditions are right
/// (no Run entry, prompt not suppressed, app not auto-launched by installer).
/// </summary>
/// <remarks>
/// Three terminal choices, one each per command — keeps the view side a
/// straight bind:
/// <list type="bullet">
///   <item><description>Yes → write the Run value, dismiss.</description></item>
///   <item><description>No → leave Run untouched, dismiss; the prompt will
///     reappear at next launch.</description></item>
///   <item><description>Don't ask again → set the suppressed flag, dismiss;
///     the prompt is hidden forever (until the user toggles it from
///     Settings).</description></item>
/// </list>
/// </remarks>
public sealed partial class StartupPromptViewModel : ObservableObject
{
    private readonly IAutoStartManager _autoStartManager;
    private readonly ILogger<StartupPromptViewModel> _logger;

    [ObservableProperty]
    private bool _isBusy;

    public StartupPromptViewModel(
        IAutoStartManager autoStartManager,
        ILogger<StartupPromptViewModel> logger)
    {
        _autoStartManager = autoStartManager;
        _logger = logger;
    }

    /// <summary>
    /// Raised when the user picks any of the three options. The hosting
    /// window subscribes to close itself.
    /// </summary>
    public event EventHandler<StartupPromptResult>? Decided;

    [RelayCommand]
    private async Task YesAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _autoStartManager.SetEnabledAsync(true, cancellationToken).ConfigureAwait(true);
            _logger.LogInformation("User opted INTO startup auto-launch via prompt.");
            Decided?.Invoke(this, StartupPromptResult.OptedIn);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void No()
    {
        _logger.LogInformation("User declined startup auto-launch this session.");
        Decided?.Invoke(this, StartupPromptResult.DeclinedOnce);
    }

    [RelayCommand]
    private async Task DontAskAgainAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _autoStartManager.SetStartupPromptSuppressedAsync(true, cancellationToken).ConfigureAwait(true);
            _logger.LogInformation("User suppressed startup auto-launch prompt.");
            Decided?.Invoke(this, StartupPromptResult.Suppressed);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// The three terminal outcomes of <see cref="StartupPromptViewModel"/>.
/// </summary>
public enum StartupPromptResult
{
    /// <summary>User picked "Yes" — the Run entry was written.</summary>
    OptedIn,
    /// <summary>User picked "No" — no Run entry; prompt will reappear next launch.</summary>
    DeclinedOnce,
    /// <summary>User picked "Don't ask again" — suppressed flag was set.</summary>
    Suppressed,
}
