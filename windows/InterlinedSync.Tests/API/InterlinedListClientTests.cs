using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.API.Models;
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

    [Fact]
    public async Task FetchDeltaAsync_parsesResponse()
    {
        var (client, handler) = Build();
        const string json = """
            {
              "syncedAt": "2026-06-22T12:00:00+00:00",
              "folders": [
                { "id": "f-1", "name": "Inbox", "parentId": null }
              ],
              "documents": [
                {
                  "id": "doc-1",
                  "title": "Hello",
                  "content": "# Hello",
                  "folderId": "f-1",
                  "updatedAt": "2026-06-22T11:59:00+00:00",
                  "deleted": false
                }
              ]
            }
            """;
        handler.When(HttpMethod.Get, "https://interlinedlist.example/api/documents/sync*")
               .Respond("application/json", json);

        var delta = await client.FetchDeltaAsync(new DateTimeOffset(2026, 6, 22, 10, 0, 0, TimeSpan.Zero));

        delta.SyncedAt.Should().Be(new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero));
        delta.Folders.Should().HaveCount(1);
        delta.Folders[0].Name.Should().Be("Inbox");
        delta.Documents.Should().HaveCount(1);
        delta.Documents[0].Id.Should().Be("doc-1");
        delta.Documents[0].Content.Should().Be("# Hello");
        delta.Documents[0].Deleted.Should().BeFalse();
    }

    [Fact]
    public async Task FetchDeltaAsync_nullSinceOmitsQuery()
    {
        var (client, handler) = Build();
        const string emptyPayload = """
            { "syncedAt": "2026-06-22T12:00:00+00:00", "folders": [], "documents": [] }
            """;
        var matched = handler.When(HttpMethod.Get, "https://interlinedlist.example/api/documents/sync")
                             .Respond("application/json", emptyPayload);

        var delta = await client.FetchDeltaAsync(null);

        delta.Documents.Should().BeEmpty();
        handler.GetMatchCount(matched).Should().Be(1);
    }

    [Fact]
    public async Task FetchDeltaAsync_withTombstones()
    {
        var (client, handler) = Build();
        const string json = """
            {
              "syncedAt": "2026-06-22T12:00:00+00:00",
              "folders": [],
              "documents": [
                {
                  "id": "doc-live",
                  "title": "Alive",
                  "content": "still here",
                  "folderId": null,
                  "updatedAt": "2026-06-22T11:00:00+00:00",
                  "deleted": false
                },
                {
                  "id": "doc-dead",
                  "title": "Gone",
                  "folderId": null,
                  "updatedAt": "2026-06-22T11:30:00+00:00",
                  "deleted": true
                }
              ]
            }
            """;
        handler.When(HttpMethod.Get, "https://interlinedlist.example/api/documents/sync*")
               .Respond("application/json", json);

        var delta = await client.FetchDeltaAsync(new DateTimeOffset(2026, 6, 22, 10, 0, 0, TimeSpan.Zero));

        delta.Documents.Should().HaveCount(2);
        var tombstone = delta.Documents.Single(d => d.Deleted);
        tombstone.Id.Should().Be("doc-dead");
        tombstone.Content.Should().BeNull();
        delta.Documents.Single(d => !d.Deleted).Content.Should().Be("still here");
    }
}
