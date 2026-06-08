namespace InterlinedSync.Configuration;

/// <summary>
/// Configuration for the InterlinedList REST API client.
/// Bound to the "Api" section of appsettings.json.
/// </summary>
public sealed class ApiOptions
{
    /// <summary>
    /// Configuration section name used in appsettings.json.
    /// </summary>
    public const string SectionName = "Api";

    /// <summary>
    /// Base URL of the InterlinedList API, including scheme.
    /// </summary>
    public string BaseUrl { get; set; } = AppConstants.DefaultApiBaseUrl;

    /// <summary>
    /// Path of the login endpoint relative to <see cref="BaseUrl"/>.
    /// </summary>
    public string LoginEndpoint { get; set; } = AppConstants.LoginEndpoint;
}
