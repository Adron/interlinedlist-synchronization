using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterlinedSync.API.Models;
using InterlinedSync.Configuration;
using InterlinedSync.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterlinedSync.API;

public sealed class InterlinedListClient : IInterlinedListClient
{
    public const string DocumentsEndpoint = "/api/documents";
    public const string SyncEndpoint = "/api/documents/sync";

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

        _logger.LogInformation("Sending sync-token request for user {Email}.", username);

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
            ThrowForStatus(response, "Login", path: _options.LoginEndpoint);
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

        _logger.LogDebug("Acquired Bearer token {Masked}.", MaskToken(payload.Token));
        return payload;
    }

    public async Task<IReadOnlyList<Document>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching document list.");

        var response = await SendAsync(HttpMethod.Get, DocumentsEndpoint, cancellationToken).ConfigureAwait(false);

        DocumentListEnvelope? envelope;
        try
        {
            envelope = await response.Content
                .ReadFromJsonAsync<DocumentListEnvelope>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Document list response was not valid JSON.");
            throw new ApiException("Document list response was not valid JSON.", response.StatusCode, ex);
        }
        finally
        {
            response.Dispose();
        }

        return envelope?.Documents ?? Array.Empty<Document>();
    }

    public async Task<Document> GetDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        var path = $"{DocumentsEndpoint}/{Uri.EscapeDataString(documentId)}";
        _logger.LogDebug("Fetching document {DocumentId}.", documentId);

        var response = await SendAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);

        Document? payload;
        try
        {
            payload = await response.Content
                .ReadFromJsonAsync<Document>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Document {DocumentId} response was not valid JSON.", documentId);
            throw new ApiException($"Document {documentId} response was not valid JSON.", response.StatusCode, ex);
        }
        finally
        {
            response.Dispose();
        }

        if (payload is null)
        {
            throw new ApiException($"Document {documentId} response was empty.", System.Net.HttpStatusCode.OK);
        }

        return payload;
    }

    public async Task<Document> CreateDocumentAsync(string title, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        content ??= string.Empty;

        _logger.LogDebug("Creating document {Title}.", title);

        var body = new DocumentMutation(title, content);
        var response = await SendJsonAsync(HttpMethod.Post, DocumentsEndpoint, body, cancellationToken).ConfigureAwait(false);

        return await ReadDocumentEnvelopeAsync(response, "create", cancellationToken).ConfigureAwait(false);
    }

    public async Task<Document> UpdateDocumentAsync(string documentId, string title, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        ArgumentException.ThrowIfNullOrEmpty(title);
        content ??= string.Empty;

        var path = $"{DocumentsEndpoint}/{Uri.EscapeDataString(documentId)}";
        _logger.LogDebug("Updating document {DocumentId}.", documentId);

        var body = new DocumentMutation(title, content);
        var response = await SendJsonAsync(HttpMethod.Patch, path, body, cancellationToken).ConfigureAwait(false);

        return await ReadDocumentEnvelopeAsync(response, "update", cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        var path = $"{DocumentsEndpoint}/{Uri.EscapeDataString(documentId)}";
        _logger.LogDebug("Deleting document {DocumentId}.", documentId);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, path);
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network failure deleting document {DocumentId}.", documentId);
            throw new ApiException($"Could not reach the InterlinedList server to delete {documentId}.", null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Delete request for {DocumentId} timed out.", documentId);
            throw new ApiException($"Delete request for {documentId} timed out.", null, ex);
        }

        try
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Server returned 404 for delete of {DocumentId}; treating as success.", documentId);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Delete {DocumentId} failed with status {Status}.", documentId, response.StatusCode);
                ThrowForStatus(response, "DELETE", path);
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<DeltaResponse> FetchDeltaAsync(DateTimeOffset? lastSyncAt, CancellationToken cancellationToken = default)
    {
        var path = SyncEndpoint;
        if (lastSyncAt is { } since)
        {
            var encoded = Uri.EscapeDataString(since.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            path = $"{SyncEndpoint}?lastSyncAt={encoded}";
        }

        _logger.LogDebug("Fetching delta since {Since}.", lastSyncAt);

        var response = await SendAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        try
        {
            DeltaResponse? payload;
            try
            {
                payload = await response.Content
                    .ReadFromJsonAsync<DeltaResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Delta response was not valid JSON.");
                throw new ApiException("Delta response was not valid JSON.", response.StatusCode, ex);
            }

            if (payload is null)
            {
                throw new ApiException("Delta response was empty.", response.StatusCode);
            }

            return payload;
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network failure calling {Method} {Path}.", method, path);
            throw new ApiException($"Could not reach the InterlinedList server for {method} {path}.", null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Request timed out calling {Method} {Path}.", method, path);
            throw new ApiException($"Request timed out calling {method} {path}.", null, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                _logger.LogWarning("{Method} {Path} failed with status {Status}.", method, path, response.StatusCode);
                ThrowForStatus(response, method.Method, path);
            }
            finally
            {
                response.Dispose();
            }
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendJsonAsync<TBody>(HttpMethod method, string path, TBody body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network failure calling {Method} {Path}.", method, path);
            throw new ApiException($"Could not reach the InterlinedList server for {method} {path}.", null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Request timed out calling {Method} {Path}.", method, path);
            throw new ApiException($"Request timed out calling {method} {path}.", null, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                _logger.LogWarning("{Method} {Path} failed with status {Status}.", method, path, response.StatusCode);
                ThrowForStatus(response, method.Method, path);
            }
            finally
            {
                response.Dispose();
            }
        }

        return response;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowForStatus(HttpResponseMessage response, string operation, string path)
    {
        var status = response.StatusCode;
        if (status == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new AuthExpiredException($"{operation} {path} returned 401 Unauthorized.");
        }
        if (status == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new RateLimitedException(
                $"{operation} {path} returned 429 Too Many Requests.",
                response.Headers.RetryAfter?.Delta);
        }
        throw new ApiException($"{operation} {path} failed with status {(int)status}.", status);
    }

    private async Task<Document> ReadDocumentEnvelopeAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        try
        {
            DocumentEnvelope? envelope;
            try
            {
                envelope = await response.Content
                    .ReadFromJsonAsync<DocumentEnvelope>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "{Operation} response was not valid JSON.", operation);
                throw new ApiException($"{operation} response was not valid JSON.", response.StatusCode, ex);
            }

            if (envelope?.Document is null)
            {
                throw new ApiException($"{operation} response did not contain a document.", response.StatusCode);
            }

            return envelope.Document;
        }
        finally
        {
            response.Dispose();
        }
    }

    private static string MaskToken(string token)
    {
        if (token.Length <= 12)
        {
            return "***";
        }
        return string.Concat(token.AsSpan(0, 8), "...", token.AsSpan(token.Length - 4));
    }

    private sealed record DocumentMutation(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("content")] string Content);

    private sealed record DocumentListEnvelope(
        [property: JsonPropertyName("documents")] IReadOnlyList<Document>? Documents);

    private sealed record DocumentEnvelope(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("document")] Document? Document);
}
