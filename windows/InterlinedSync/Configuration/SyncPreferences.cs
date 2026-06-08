namespace InterlinedSync.Configuration;

/// <summary>
/// User-tweakable preferences bound to the "Sync" section of appsettings.json.
/// Bound via <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> so callers
/// depend on an abstraction, not the configuration system itself.
/// </summary>
public sealed class SyncPreferences
{
    /// <summary>
    /// Configuration section name used in appsettings.json.
    /// </summary>
    public const string SectionName = "Sync";

    /// <summary>
    /// Absolute path to the local sync folder. Defaults to %USERPROFILE%\Documents\InterlinedList.
    /// </summary>
    public string SyncFolder { get; set; } = string.Empty;

    /// <summary>
    /// How often the pull loop polls the server, in seconds.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = AppConstants.DefaultPollIntervalSeconds;

    /// <summary>
    /// Whether the app should launch on user login.
    /// </summary>
    public bool AutoStart { get; set; }
}
