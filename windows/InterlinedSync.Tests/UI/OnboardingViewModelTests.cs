using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.Auth;
using InterlinedSync.Storage;
using InterlinedSync.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InterlinedSync.Tests.UI;

public class OnboardingViewModelTests
{
    private static OnboardingViewModel Build(Mock<IAuthProvider> auth, Mock<IAccountStore>? account = null)
    {
        account ??= new Mock<IAccountStore>();
        return new OnboardingViewModel(auth.Object, account.Object, NullLogger<OnboardingViewModel>.Instance);
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
}
