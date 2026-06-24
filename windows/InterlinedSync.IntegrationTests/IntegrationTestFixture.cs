using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterlinedSync.API;
using InterlinedSync.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InterlinedSync.IntegrationTests;

public sealed class IntegrationTestFixture : IAsyncLifetime
{
    public const string SyncTokenEndpoint = "/api/auth/sync-token";
    public const string IntegTitlePrefix = "__windows-integ-";

    private HttpClient? _rawHttp;

    public bool CredentialsAvailable { get; private set; }

    public string? BaseUrl { get; private set; }

    public string? Email { get; private set; }

    public string? Password { get; private set; }

    public string? Token { get; private set; }

    public InterlinedListClient? Client { get; private set; }

    public async Task InitializeAsync()
    {
        Email = Environment.GetEnvironmentVariable("INTERLINEDLIST_EMAIL");
        Password = Environment.GetEnvironmentVariable("INTERLINEDLIST_PASSWORD");
        BaseUrl = Environment.GetEnvironmentVariable("INTERLINEDLIST_API_BASE_URL");

        if (string.IsNullOrWhiteSpace(Email)
            || string.IsNullOrWhiteSpace(Password)
            || string.IsNullOrWhiteSpace(BaseUrl))
        {
            CredentialsAvailable = false;
            return;
        }

        CredentialsAvailable = true;

        Token = await AcquireSyncTokenAsync(BaseUrl, Email, Password).ConfigureAwait(false);

        _rawHttp = BuildHttpClient(BaseUrl, Token);

        Client = new InterlinedListClient(
            BuildHttpClient(BaseUrl, Token),
            Options.Create(new ApiOptions { BaseUrl = BaseUrl }),
            NullLogger<InterlinedListClient>.Instance);

        await SweepIntegrationDocumentsAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_rawHttp is null)
        {
            return;
        }

        await SweepIntegrationDocumentsAsync().ConfigureAwait(false);
        _rawHttp.Dispose();
    }

    public InterlinedListClient RequireClient()
    {
        if (Client is null)
        {
            throw new InvalidOperationException("Live-API client was not initialized; credentials missing.");
        }

        return Client;
    }

    public HttpClient RequireRawHttp()
    {
        if (_rawHttp is null)
        {
            throw new InvalidOperationException("Live-API HTTP client was not initialized; credentials missing.");
        }

        return _rawHttp;
    }

    private async Task SweepIntegrationDocumentsAsync()
    {
        if (_rawHttp is null)
        {
            return;
        }

        try
        {
            using var listResponse = await _rawHttp.GetAsync("/api/documents").ConfigureAwait(false);
            if (!listResponse.IsSuccessStatusCode)
            {
                return;
            }

            var envelope = await listResponse.Content
                .ReadFromJsonAsync<DocumentListEnvelope>()
                .ConfigureAwait(false);

            if (envelope?.Documents is null)
            {
                return;
            }

            foreach (var doc in envelope.Documents)
            {
                if (doc.Id is null || doc.Title is null) continue;
                if (!doc.Title.StartsWith(IntegTitlePrefix, StringComparison.Ordinal)) continue;

                try
                {
                    using var del = await _rawHttp.DeleteAsync($"/api/documents/{Uri.EscapeDataString(doc.Id)}").ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                }
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private static async Task<string> AcquireSyncTokenAsync(string baseUrl, string email, string password)
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var body = new SyncTokenRequest(email, password);
        using var response = await http.PostAsJsonAsync(SyncTokenEndpoint, body).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync<SyncTokenResponse>()
            .ConfigureAwait(false);

        if (payload is null || string.IsNullOrEmpty(payload.Token))
        {
            throw new InvalidOperationException("sync-token response missing token field.");
        }

        return payload.Token;
    }

    private static HttpClient BuildHttpClient(string baseUrl, string? token)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };

        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private sealed record SyncTokenRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password);

    private sealed record SyncTokenResponse(
        [property: JsonPropertyName("token")] string Token);

    private sealed record DocumentListEnvelope(
        [property: JsonPropertyName("documents")] IReadOnlyList<DocumentEnvelopeEntry>? Documents);

    private sealed record DocumentEnvelopeEntry(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("title")] string? Title);
}
