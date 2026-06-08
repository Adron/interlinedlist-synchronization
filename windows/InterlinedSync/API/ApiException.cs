using System.Net;

namespace InterlinedSync.API;

/// <summary>
/// Raised when the API returns a non-success status code or an unexpected payload.
/// Callers can inspect <see cref="StatusCode"/> to react to specific failures
/// (e.g. <see cref="HttpStatusCode.Unauthorized"/> triggers a re-auth flow).
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}
