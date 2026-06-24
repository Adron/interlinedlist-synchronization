using System.Net;
using InterlinedSync.API;

namespace InterlinedSync.Errors;

/// <summary>
/// Raised when the server rejects the current Bearer token with HTTP 401.
/// The sync engine catches this, transitions to <c>AuthExpired</c>, and pauses
/// until the user re-authenticates from the tray UI.
/// </summary>
public sealed class AuthExpiredException : ApiException
{
    public AuthExpiredException(string message, Exception? inner = null)
        : base(message, HttpStatusCode.Unauthorized, inner)
    {
    }
}
