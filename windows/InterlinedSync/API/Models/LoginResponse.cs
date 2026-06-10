namespace InterlinedSync.API.Models;

/// <summary>
/// Successful response from <c>/api/auth/login</c>. The server returns a Bearer
/// token used for all subsequent API requests.
/// </summary>
public sealed record LoginResponse(string Token);
