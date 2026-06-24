using System.Net;
using InterlinedSync.API;

namespace InterlinedSync.Errors;

/// <summary>
/// Raised when the server returns HTTP 429. <see cref="RetryAfter"/> carries the
/// server-supplied delay (from the <c>Retry-After</c> header) when present.
/// </summary>
public sealed class RateLimitedException : ApiException
{
    public RateLimitedException(string message, TimeSpan? retryAfter, Exception? inner = null)
        : base(message, HttpStatusCode.TooManyRequests, inner)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
