using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterlinedSync.Auth;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.UI.ViewModels;

/// <summary>
/// ViewModel for the first-run sign-in window. Wraps <see cref="IAuthProvider"/>
/// with observable properties so the WPF view can bind directly.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly IAuthProvider _authProvider;
    private readonly ILogger<OnboardingViewModel> _logger;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _signInSucceeded;

    public OnboardingViewModel(IAuthProvider authProvider, ILogger<OnboardingViewModel> logger)
    {
        _authProvider = authProvider;
        _logger = logger;
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Raised when sign-in succeeds so the hosting window can close itself.
    /// </summary>
    public event EventHandler? SignInCompleted;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var ok = await _authProvider.SignInAsync(Username, Password, cancellationToken).ConfigureAwait(true);
            if (ok)
            {
                SignInSucceeded = true;
                SignInCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ErrorMessage = "Incorrect username or password.";
            }
        }
        catch (AuthException ex)
        {
            _logger.LogError(ex, "Sign-in failed for user {Username}.", Username);
            ErrorMessage = "Could not reach InterlinedList. Check your connection and try again.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during sign-in.");
            ErrorMessage = "Something went wrong. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSignIn() => !IsBusy
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);

    partial void OnUsernameChanged(string value) => SignInCommand.NotifyCanExecuteChanged();
    partial void OnPasswordChanged(string value) => SignInCommand.NotifyCanExecuteChanged();
}
