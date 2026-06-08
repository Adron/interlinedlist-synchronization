using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.API.Models;
using InterlinedSync.Auth;
using InterlinedSync.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InterlinedSync.Tests.Auth;

public class AuthManagerTests
{
    private static (AuthManager auth, Mock<IInterlinedListClient> client, ICredentialStore store) Build()
    {
        var client = new Mock<IInterlinedListClient>();
        var store = new InMemoryCredentialStore();
        var auth = new AuthManager(client.Object, store, NullLogger<AuthManager>.Instance);
        return (auth, client, store);
    }

    [Fact]
    public async Task GetTokenAsync_ReturnsStoredToken()
    {
        var (auth, _, store) = Build();
        await store.SaveTokenAsync("stored-token");

        var token = await auth.GetTokenAsync();

        token.Should().Be("stored-token");
    }

    [Fact]
    public async Task GetTokenAsync_ReturnsNull_WhenNoTokenStored()
    {
        var (auth, _, _) = Build();
        var token = await auth.GetTokenAsync();
        token.Should().BeNull();
    }

    [Fact]
    public async Task SignInAsync_StoresTokenAndReturnsTrue_OnSuccess()
    {
        var (auth, client, store) = Build();
        client.Setup(c => c.LoginAsync("alice", "pwd", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new LoginResponse("new-token"));

        var ok = await auth.SignInAsync("alice", "pwd");

        ok.Should().BeTrue();
        (await store.LoadTokenAsync()).Should().Be("new-token");
        (await auth.GetTokenAsync()).Should().Be("new-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task SignInAsync_ReturnsFalse_ForCredentialErrors(HttpStatusCode status)
    {
        var (auth, client, store) = Build();
        client.Setup(c => c.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new ApiException("bad creds", status));

        var ok = await auth.SignInAsync("alice", "wrong");

        ok.Should().BeFalse();
        (await store.LoadTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SignInAsync_ThrowsAuthException_OnServerError()
    {
        var (auth, client, _) = Build();
        client.Setup(c => c.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new ApiException("boom", HttpStatusCode.InternalServerError));

        var act = async () => await auth.SignInAsync("alice", "pwd");

        await act.Should().ThrowAsync<AuthException>();
    }

    [Fact]
    public async Task SignOutAsync_RemovesStoredToken()
    {
        var (auth, client, store) = Build();
        client.Setup(c => c.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new LoginResponse("tok"));
        await auth.SignInAsync("alice", "pwd");

        await auth.SignOutAsync();

        (await store.LoadTokenAsync()).Should().BeNull();
        (await auth.GetTokenAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("", "pwd")]
    [InlineData("alice", "")]
    public async Task SignInAsync_RejectsEmptyArguments(string username, string password)
    {
        var (auth, _, _) = Build();
        var act = async () => await auth.SignInAsync(username, password);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
