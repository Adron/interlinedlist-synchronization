using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.Auth;
using InterlinedSync.Storage;
using InterlinedSync.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InterlinedSync.Tests.UI;

public class OnboardingViewModelTests
{
    private static OnboardingViewModel Build(
        Mock<IAuthProvider> auth,
        Mock<IAccountStore>? account = null,
        ILogger<OnboardingViewModel>? logger = null)
    {
        account ??= new Mock<IAccountStore>();
        return new OnboardingViewModel(
            auth.Object,
            account.Object,
            logger ?? NullLogger<OnboardingViewModel>.Instance);
    }

    [Fact]
    public void SignInCommand_CannotExecute_WhenFieldsEmpty()
    {
        var auth = new Mock<IAuthProvider>();
        var vm = Build(auth);

        vm.SignInCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SignInCommand_CanExecute_WhenFieldsPopulated()
    {
        var auth = new Mock<IAuthProvider>();
        var vm = Build(auth);
        vm.Username = "alice";
        vm.Password = "pwd";

        vm.SignInCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task SignInCommand_SetsError_WhenAuthReturnsFalse()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync("alice", "wrong", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var vm = Build(auth);
        vm.Username = "alice";
        vm.Password = "wrong";

        await vm.SignInCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.SignInSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task SignInCommand_RaisesSignInCompleted_OnSuccess()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync("alice", "pwd", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var vm = Build(auth);
        vm.Username = "alice";
        vm.Password = "pwd";

        var completed = false;
        vm.SignInCompleted += (_, _) => completed = true;

        await vm.SignInCommand.ExecuteAsync(null);

        completed.Should().BeTrue();
        vm.SignInSucceeded.Should().BeTrue();
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task SignInCommand_PersistsEmail_OnSuccess()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync("alice@example.com", "pwd", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var account = new Mock<IAccountStore>();
        var vm = Build(auth, account);
        vm.Username = "alice@example.com";
        vm.Password = "pwd";

        await vm.SignInCommand.ExecuteAsync(null);

        account.Verify(
            a => a.SetSignedInEmailAsync("alice@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SignInCommand_SetsError_WhenAuthThrowsAuthException()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthException("offline", new ApiException("net", HttpStatusCode.ServiceUnavailable)));
        var vm = Build(auth);
        vm.Username = "alice";
        vm.Password = "pwd";

        await vm.SignInCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.ErrorMessage.Should().Contain("connection");
    }

    [Fact]
    public async Task SignInCommand_ClearsBusy_OnError()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));
        var vm = Build(auth);
        vm.Username = "alice";
        vm.Password = "pwd";

        await vm.SignInCommand.ExecuteAsync(null);

        vm.IsBusy.Should().BeFalse();
        vm.HasError.Should().BeTrue();
    }

    // ---------- Unsigned-state launch (Workstream C) ----------

    [Fact]
    public void InitialState_FreshLaunch_HasEmptyCredentialsAndNoError()
    {
        // Simulates "the app launched with no token in Credential Manager and
        // surfaced the onboarding window" — the VM should start completely blank.
        var auth = new Mock<IAuthProvider>();
        var vm = Build(auth);

        vm.Username.Should().BeEmpty();
        vm.Password.Should().BeEmpty();
        vm.ErrorMessage.Should().BeNull();
        vm.HasError.Should().BeFalse();
        vm.SignInSucceeded.Should().BeFalse();
        vm.IsBusy.Should().BeFalse();
        vm.SignInCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task SignInCommand_DoesNotPersistEmail_WhenAuthFails()
    {
        // "Validate before persisting" — on a 401 the email must NOT be written
        // to the account store. Token persistence already lives in AuthManager
        // and is covered by AuthManagerTests, but the VM-side guard matters too.
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var account = new Mock<IAccountStore>(MockBehavior.Strict);
        var vm = Build(auth, account);
        vm.Username = "alice@example.com";
        vm.Password = "wrong";

        await vm.SignInCommand.ExecuteAsync(null);

        account.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SignInCommand_DoesNotPersistEmail_WhenAuthThrows()
    {
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthException("net", new ApiException("x", HttpStatusCode.ServiceUnavailable)));
        var account = new Mock<IAccountStore>(MockBehavior.Strict);
        var vm = Build(auth, account);
        vm.Username = "alice@example.com";
        vm.Password = "pwd";

        await vm.SignInCommand.ExecuteAsync(null);

        account.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SignInCommand_DoesNotLogPassword_OnError()
    {
        // Capture every log message and assert the password string never appears.
        var captured = new List<string>();
        var logger = new CapturingLogger<OnboardingViewModel>(captured);
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));
        var vm = Build(auth, logger: logger);
        const string secret = "hunter2-correct-horse-battery-staple";
        vm.Username = "alice";
        vm.Password = secret;

        await vm.SignInCommand.ExecuteAsync(null);

        captured.Should().NotContain(s => s.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignInCommand_DoesNotLogPassword_OnAuthException()
    {
        var captured = new List<string>();
        var logger = new CapturingLogger<OnboardingViewModel>(captured);
        var auth = new Mock<IAuthProvider>();
        auth.Setup(a => a.SignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthException("offline", new ApiException("net", HttpStatusCode.ServiceUnavailable)));
        var vm = Build(auth, logger: logger);
        const string secret = "totally-not-the-real-password";
        vm.Username = "alice";
        vm.Password = secret;

        await vm.SignInCommand.ExecuteAsync(null);

        captured.Should().NotContain(s => s.Contains(secret, StringComparison.Ordinal));
    }

    /// <summary>
    /// Minimal ILogger that records the rendered message of every log call. The
    /// production code uses ILogger&lt;T&gt; via DI so a structured fake here
    /// gives us a deterministic way to assert "the password never appears".
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _sink;

        public CapturingLogger(List<string> sink) => _sink = sink;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink.Add(formatter(state, exception));
            if (exception is not null)
            {
                _sink.Add(exception.ToString());
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
