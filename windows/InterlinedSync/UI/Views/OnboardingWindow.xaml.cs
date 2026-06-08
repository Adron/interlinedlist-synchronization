using System.Windows;
using System.Windows.Controls;
using InterlinedSync.UI.ViewModels;

namespace InterlinedSync.UI.Views;

/// <summary>
/// Sign-in window shown on first launch (and after sign-out). Code-behind is
/// limited to PasswordBox glue and close-on-success wiring — all logic lives in
/// <see cref="OnboardingViewModel"/>.
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;

    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _viewModel.SignInCompleted += OnSignInCompleted;
        Closed += (_, _) => _viewModel.SignInCompleted -= OnSignInCompleted;
    }

    private void OnSignInCompleted(object? sender, EventArgs e)
    {
        DialogResult = true;
        Close();
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
