using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;
using Xunit;

namespace InterlinedSync.Tests.API;

public class InterlinedListClientTests
{
    private static (InterlinedListClient client, MockHttpMessageHandler handler) Build()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = handler.ToHttpClient();
        var options = Options.Create(new ApiOptions
        {
            BaseUrl = "https://interlinedlist.example",
            LoginEndpoint = "/api/auth/login",
        });
        var client = new InterlinedListClient(httpClient, options, NullLogger<InterlinedListClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task LoginAsync_ReturnsToken_OnSuccess()
    {
        var (client, handler) = Build();
        handler.When(HttpMethod.Post, "https://interlinedlist.example/api/auth/login")
               .Respond("application/json", "{\"token\":\"il_tok_abc\"}");

        var response = await client.LoginAsync("alice", "hunter2");

        response.Token.Should().Be("il_tok_abc");
    }

    [Fact]
    public async Task LoginAsync_ThrowsApiException_OnUnauthorized()
    {
        var (client, handler) = Build();
        handler.When(HttpMethod.Post, "*/api/auth/login")
               .Respond(HttpStatusCode.Unauthorized);

        var act = async () => await client.LoginAsync("alice", "wrong");

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ex.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LoginAsync_ThrowsApiException_WhenTokenMissing()
    {
        var (client, handler) = Build();
        handler.When(HttpMethod.Post, "*/api/auth/login")
               .Respond("application/json", "{}");

        var act = async () => await client.LoginAsync("alice", "hunter2");

        await act.Should().ThrowAsync<ApiException>()
            .WithMessage("*did not contain a token*");
    }

    [Fact]
    public async Task LoginAsync_ThrowsApiException_OnNetworkFailure()
    {
        var (client, handler) = Build();
        handler.When(HttpMethod.Post, "*/api/auth/login")
               .Throw(new HttpRequestException("offline"));

        var act = async () => await client.LoginAsync("alice", "hunter2");

        await act.Should().ThrowAsync<ApiException>();
    }

    [Theory]
    [InlineData("", "pwd")]
    [InlineData("user", "")]
    public async Task LoginAsync_RejectsEmptyArguments(string username, string password)
    {
        var (client, _) = Build();
        var act = async () => await client.LoginAsync(username, password);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
