using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.API.Models;
using Xunit;

namespace InterlinedSync.IntegrationTests;

public sealed class InterlinedListClientIntegrationTests : IClassFixture<IntegrationTestFixture>
{
    private const string TitlePrefix = "__windows-integ-";

    private readonly IntegrationTestFixture _fixture;

    public InterlinedListClientIntegrationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationFact]
    public async Task Login_ReturnsToken()
    {
        _fixture.CredentialsAvailable.Should().BeTrue();
        _fixture.Token.Should().NotBeNullOrWhiteSpace("sync-token endpoint must hand back a Bearer token");

        var login = new InterlinedListClient(
            new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl!, UriKind.Absolute) },
            Microsoft.Extensions.Options.Options.Create(new InterlinedSync.Configuration.ApiOptions { BaseUrl = _fixture.BaseUrl! }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InterlinedListClient>.Instance);

        var response = await login.LoginAsync(_fixture.Email!, _fixture.Password!);
        response.Token.Should().NotBeNullOrWhiteSpace("production LoginAsync should return the same Bearer token as sync-token");
    }

    [IntegrationFact]
    public async Task FetchDocuments_WithBearer()
    {
        var client = _fixture.RequireClient();

        var documents = await client.GetDocumentsAsync();

        documents.Should().NotBeNull();
    }

    [IntegrationFact]
    public async Task CreateUpdateDelete_RoundTrip()
    {
        var client = _fixture.RequireClient();
        var title = TitlePrefix + Guid.NewGuid().ToString("N");
        const string initialContent = "# Initial\n\nWritten by windows integ test.";
        const string updatedContent = "# Updated\n\nPatched by windows integ test.";

        string? createdId = null;
        try
        {
            var created = await client.CreateDocumentAsync(title, initialContent);
            created.Should().NotBeNull();
            created.Id.Should().NotBeNullOrWhiteSpace();
            created.Title.Should().Be(title);
            created.Content.Should().Be(initialContent);
            createdId = created.Id;

            var updated = await client.UpdateDocumentAsync(created.Id, title, updatedContent);
            updated.Id.Should().Be(created.Id);
            updated.Content.Should().Be(updatedContent);

            await client.DeleteDocumentAsync(created.Id);
            createdId = null;

            var fetch = async () => await client.GetDocumentAsync(created.Id);
            var apiEx = await fetch.Should().ThrowAsync<ApiException>();
            apiEx.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await TryDeleteAsync(createdId);
        }
    }

    [IntegrationFact]
    public async Task FetchDelta_Initial()
    {
        var client = _fixture.RequireClient();

        var delta = await client.FetchDeltaAsync(null);

        delta.Should().NotBeNull();
        delta.SyncedAt.Should().NotBe(default);
        delta.Documents.Should().NotBeNull();
    }

    [IntegrationFact]
    public async Task FetchDelta_WithTombstone()
    {
        var client = _fixture.RequireClient();
        var title = TitlePrefix + Guid.NewGuid().ToString("N");

        string? createdId = null;
        try
        {
            var created = await client.CreateDocumentAsync(title, "tombstone-probe");
            createdId = created.Id;

            var afterCreate = await client.FetchDeltaAsync(null);
            afterCreate.SyncedAt.Should().NotBe(default);
            afterCreate.Documents.Should().Contain(d => d.Id == created.Id && !d.IsDeleted,
                "a freshly created document should appear in the full delta listing");

            await client.DeleteDocumentAsync(created.Id);
            createdId = null;

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            bool removed = false;
            DocumentDelta? tombstone = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = await client.FetchDeltaAsync(null);
                tombstone = snapshot.Documents.FirstOrDefault(d => d.Id == created.Id && d.IsDeleted);
                if (tombstone is not null)
                {
                    break;
                }

                if (snapshot.Documents.All(d => d.Id != created.Id))
                {
                    removed = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            if (tombstone is not null)
            {
                tombstone.IsDeleted.Should().BeTrue();
                tombstone.DeletedAt.Should().NotBeNull();
            }
            else
            {
                removed.Should().BeTrue(
                    "if the server omits tombstones it must at least drop the deleted document from subsequent delta responses");
            }
        }
        finally
        {
            await TryDeleteAsync(createdId);
        }
    }

    private async Task TryDeleteAsync(string? documentId)
    {
        if (string.IsNullOrEmpty(documentId) || _fixture.Client is null)
        {
            return;
        }

        try
        {
            await _fixture.Client.DeleteDocumentAsync(documentId);
        }
        catch (ApiException)
        {
        }
    }
}
