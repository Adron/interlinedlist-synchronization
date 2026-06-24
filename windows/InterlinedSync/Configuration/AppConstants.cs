namespace InterlinedSync.Configuration;

/// <summary>
/// Application-wide constants. Centralizing magic strings here avoids drift and
/// keeps the test surface stable.
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// Folder name used under %APPDATA% and %LOCALAPPDATA% for all app state.
    /// </summary>
    public const string AppDataFolderName = "interlinedlist-sync";

    /// <summary>
    /// Name of the user preferences JSON file inside the app data folder.
    /// </summary>
    public const string AppSettingsFileName = "appsettings.json";

    /// <summary>
    /// Folder name under %LOCALAPPDATA%\{AppDataFolderName} for Serilog log files.
    /// </summary>
    public const string LogsFolderName = "logs";

    /// <summary>
    /// Default file name pattern for the rolling log files.
    /// </summary>
    public const string LogFileName = "interlinedsync-.log";

    /// <summary>
    /// Default sync folder created under the user's Documents directory.
    /// </summary>
    public const string DefaultSyncFolderName = "InterlinedList";

    /// <summary>
    /// Default poll interval in seconds. 30 s matches the macOS sync client.
    /// </summary>
    public const int DefaultPollIntervalSeconds = 30;

    /// <summary>
    /// Minimum allowed poll interval. Below 10 s the server may rate-limit us.
    /// </summary>
    public const int MinimumPollIntervalSeconds = 10;

    /// <summary>
    /// Default base URL for the InterlinedList API.
    /// </summary>
    public const string DefaultApiBaseUrl = "https://interlinedlist.com";

    /// <summary>
    /// Default login endpoint, relative to the API base URL. Uses the sync-token
    /// flow intended for native/CLI/mobile clients (returns a Bearer token).
    /// </summary>
    public const string LoginEndpoint = "/api/auth/sync-token";

    /// <summary>
    /// PasswordVault resource name used to scope the credential to this app.
    /// Mirrors the bundle identifier pattern from the macOS client.
    /// </summary>
    public const string CredentialVaultResource = "com.interlinedlist.sync";

    /// <summary>
    /// PasswordVault user name placeholder for the session token entry.
    /// Multi-account support is out of scope for v1 so a single fixed key is used.
    /// </summary>
    public const string CredentialVaultTokenUser = "session-token";

    /// <summary>
    /// Maximum number of retained log files (one per day).
    /// </summary>
    public const int LogRetainedFileCount = 7;
}
