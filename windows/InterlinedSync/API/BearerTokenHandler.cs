using System.Net.Http.Headers;
using InterlinedSync.Auth;

namespace InterlinedSync.API;

/// <summary>
/// HTTP message handler that attaches the current Bearer token to every outgoing
/// request. Resolved from <see cref="IAuthProvider"/> so the handler stays unaware
/// of where the token is stored.
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly IAuthProvider _authProvider;

    public BearerTokenHandler(IAuthProvider authProvider)
    {
        _authProvider = authProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _authProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
