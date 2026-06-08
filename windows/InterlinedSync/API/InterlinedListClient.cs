using System.Net.Http.Json;
using System.Text.Json;
using InterlinedSync.API.Models;
using InterlinedSync.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterlinedSync.API;

/// <summary>
/// Stub implementation for Phase 2: only the login endpoint is wired up.
/// Phase 3+ adds delta sync, document upload and folder operations.
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> is supplied by <c>IHttpClientFactory</c> so we
/// never new one up directly and tests can inject <c>MockHttpMessageHandler</c>.
/// </remarks>
public sealed class InterlinedListClient : IInterlinedListClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;
    private readonly ApiOptions _options;
    private readonly ILogger<InterlinedListClient> _logger;

    public InterlinedListClient(HttpClient httpClient, IOptions<ApiOptions> options, ILogger<InterlinedListClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress is null && !string.IsNullOrEmpty(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl, UriKind.Absolute);
        }
    }

    public async Task<LoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        _logger.LogInformation("Sending login request for user {Username}.", username);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .PostAsJsonAsync(_options.LoginEndpoint, new LoginRequest(username, password), JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network failure during login.");
            throw new ApiException("Could not reach the InterlinedList server.", null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Login request timed out.");
            throw new ApiException("Login request timed out.", null, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Login failed with status {Status}.", response.StatusCode);
            throw new ApiException($"Login failed with status {(int)response.StatusCode}.", response.StatusCode);
        }

        LoginResponse? payload;
        try
        {
            payload = await response.Content
                .ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Login response was not valid JSON.");
            throw new ApiException("Login response was not valid JSON.", response.StatusCode, ex);
        }

        if (payload is null || string.IsNullOrEmpty(payload.Token))
        {
            throw new ApiException("Login response did not contain a token.", response.StatusCode);
        }

        return payload;
    }
}
