namespace InterlinedSync.API.Models;

/// <summary>
/// Body posted to <c>/api/auth/login</c>.
/// </summary>
public sealed record LoginRequest(string Username, string Password);
