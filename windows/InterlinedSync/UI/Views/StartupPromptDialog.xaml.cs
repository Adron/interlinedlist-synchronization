using System.Windows;
using InterlinedSync.UI.ViewModels;

namespace InterlinedSync.UI.Views;

/// <summary>
/// One-time non-modal prompt asking whether to register the app for auto-start
/// on Windows sign-in. All logic lives in <see cref="StartupPromptViewModel"/>;
/// the code-behind only routes the VM's <see cref="StartupPromptViewModel.Decided"/>
/// event into <see cref="Window.Close"/>.
/// </summary>
public partial class StartupPromptDialog : Window
{
    private readonly StartupPromptViewModel _viewModel;

    public StartupPromptDialog(StartupPromptViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _viewModel.Decided += OnDecided;
        Closed += (_, _) => _viewModel.Decided -= OnDecided;
    }

    /// <summary>
    /// The outcome that closed the dialog. <c>null</c> when the user dismissed
    /// the window via the title-bar close button without picking an option —
    /// callers should treat that as <see cref="StartupPromptResult.DeclinedOnce"/>.
    /// </summary>
    public StartupPromptResult? Outcome { get; private set; }

    private void OnDecided(object? sender, StartupPromptResult result)
    {
        Outcome = result;
        Close();
    }
}
