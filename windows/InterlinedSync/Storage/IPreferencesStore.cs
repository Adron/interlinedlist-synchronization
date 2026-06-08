using InterlinedSync.Configuration;

namespace InterlinedSync.Storage;

/// <summary>
/// Read/write access to <see cref="SyncPreferences"/> persisted as JSON.
/// Kept separate from <see cref="ICredentialStore"/> per ISP: the settings UI
/// needs preferences but never the token, and vice versa.
/// </summary>
public interface IPreferencesStore
{
    /// <summary>
    /// Loads the user's preferences, returning sensible defaults if no file exists yet.
    /// </summary>
    Task<SyncPreferences> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the preferences atomically (write to temp file + replace).
    /// </summary>
    Task SaveAsync(SyncPreferences preferences, CancellationToken cancellationToken = default);

    /// <summary>
    /// Absolute path to the JSON file backing the preferences. Useful for diagnostics
    /// and for the settings UI "open config folder" affordance.
    /// </summary>
    string PreferencesFilePath { get; }
}
