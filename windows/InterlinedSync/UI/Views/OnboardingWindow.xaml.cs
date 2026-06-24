using System.IO;
using System.Windows;
using System.Windows.Controls;
using InterlinedSync.Configuration;
using InterlinedSync.Storage;
using InterlinedSync.UI.ViewModels;
using Microsoft.Win32;

namespace InterlinedSync.UI.Views;

/// <summary>
/// Sign-in window shown on first launch (and after sign-out). Code-behind is
/// limited to PasswordBox glue, folder selection, and close-on-success wiring —
/// all auth logic lives in <see cref="OnboardingViewModel"/>.
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;
    private readonly IPreferencesStore? _preferencesStore;

    public OnboardingWindow(OnboardingViewModel viewModel)
        : this(viewModel, preferencesStore: null)
    {
    }

    public OnboardingWindow(OnboardingViewModel viewModel, IPreferencesStore? preferencesStore)
    {
        _viewModel = viewModel;
        _preferencesStore = preferencesStore;
        DataContext = viewModel;
        InitializeComponent();
        _viewModel.SignInCompleted += OnSignInCompleted;
        Closed += (_, _) => _viewModel.SignInCompleted -= OnSignInCompleted;
    }

    private async void OnSignInCompleted(object? sender, EventArgs e)
    {
        await PromptForSyncFolderAsync().ConfigureAwait(true);
        DialogResult = true;
        Close();
    }

    private async Task PromptForSyncFolderAsync()
    {
        if (_preferencesStore is null)
        {
            return;
        }

        var prefs = await _preferencesStore.LoadAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(prefs.SyncFolder) && Directory.Exists(prefs.SyncFolder))
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to sync your InterlinedList documents",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            prefs.SyncFolder = dialog.FolderName;
        }
        else if (string.IsNullOrEmpty(prefs.SyncFolder))
        {
            prefs.SyncFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AppConstants.DefaultSyncFolderName);
        }

        Directory.CreateDirectory(prefs.SyncFolder);
        await _preferencesStore.SaveAsync(prefs).ConfigureAwait(true);
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _viewModel.Password = box.Password;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
