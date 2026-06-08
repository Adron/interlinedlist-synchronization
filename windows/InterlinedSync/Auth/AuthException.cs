namespace InterlinedSync.Auth;

/// <summary>
/// Raised by the auth layer when sign-in fails or the stored token is rejected.
/// </summary>
public sealed class AuthException : Exception
{
    public AuthException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
