using System.IO;
using System.Windows;
using InterlinedSync.UI.ViewModels;
using Microsoft.Win32;

namespace InterlinedSync.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.LoadAsync().ConfigureAwait(true);
        _viewModel.SignedOutRequested += OnSignedOutRequested;
        Closed += (_, _) => _viewModel.SignedOutRequested -= OnSignedOutRequested;
    }

    private void OnSignedOutRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BrowseSyncFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose sync folder",
            Multiselect = false,
        };

        if (!string.IsNullOrEmpty(_viewModel.SyncFolder) && Directory.Exists(_viewModel.SyncFolder))
        {
            dialog.InitialDirectory = _viewModel.SyncFolder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SyncFolder = dialog.FolderName;
        }
    }

    private void ResetState_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            this,
            "This will clear the local sync database. The next sync will re-download everything from the server. Your local files and credentials are not affected.\n\nContinue?",
            "Reset sync state",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmed == MessageBoxResult.Yes && _viewModel.ResetStateCommand.CanExecute(null))
        {
            _viewModel.ResetStateCommand.Execute(null);
        }
    }
}
