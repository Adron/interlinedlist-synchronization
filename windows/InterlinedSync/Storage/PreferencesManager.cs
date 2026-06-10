using System.IO.Abstractions;
using System.Text.Json;
using InterlinedSync.Configuration;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.Storage;

/// <summary>
/// JSON-backed implementation of <see cref="IPreferencesStore"/>. Writes to
/// <c>%APPDATA%\interlinedlist-sync\appsettings.json</c> by default.
/// </summary>
/// <remarks>
/// Uses <see cref="IFileSystem"/> rather than <see cref="System.IO.File"/> directly
/// so tests can substitute a <c>MockFileSystem</c>.
/// </remarks>
public sealed class PreferencesManager : IPreferencesStore
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<PreferencesManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PreferencesManager(IFileSystem fileSystem, ILogger<PreferencesManager> logger)
        : this(fileSystem, logger, DefaultPreferencesPath(fileSystem))
    {
    }

    internal PreferencesManager(IFileSystem fileSystem, ILogger<PreferencesManager> logger, string preferencesFilePath)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        PreferencesFilePath = preferencesFilePath;
    }

    public string PreferencesFilePath { get; }

    public async Task<SyncPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_fileSystem.File.Exists(PreferencesFilePath))
            {
                _logger.LogInformation("No preferences file at {Path}; returning defaults.", PreferencesFilePath);
                return BuildDefaults();
            }

            await using var stream = _fileSystem.File.OpenRead(PreferencesFilePath);
            var prefs = await JsonSerializer.DeserializeAsync<SyncPreferences>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return prefs ?? BuildDefaults();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Preferences file {Path} is corrupt; falling back to defaults.", PreferencesFilePath);
            return BuildDefaults();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SyncPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectory();

            var tempPath = PreferencesFilePath + ".tmp";
            await using (var stream = _fileSystem.File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_fileSystem.File.Exists(PreferencesFilePath))
            {
                _fileSystem.File.Delete(PreferencesFilePath);
            }

            _fileSystem.File.Move(tempPath, PreferencesFilePath);
            _logger.LogInformation("Saved preferences to {Path}.", PreferencesFilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDirectory()
    {
        var directory = _fileSystem.Path.GetDirectoryName(PreferencesFilePath);
        if (!string.IsNullOrEmpty(directory) && !_fileSystem.Directory.Exists(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }
    }

    private SyncPreferences BuildDefaults()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = string.IsNullOrEmpty(documents)
            ? AppConstants.DefaultSyncFolderName
            : _fileSystem.Path.Combine(documents, AppConstants.DefaultSyncFolderName);

        return new SyncPreferences
        {
            SyncFolder = folder,
            PollIntervalSeconds = AppConstants.DefaultPollIntervalSeconds,
            AutoStart = false,
        };
    }

    private static string DefaultPreferencesPath(IFileSystem fileSystem)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return fileSystem.Path.Combine(appData, AppConstants.AppDataFolderName, AppConstants.AppSettingsFileName);
    }
}
