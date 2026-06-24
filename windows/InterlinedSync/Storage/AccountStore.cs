using System.IO.Abstractions;
using System.Text.Json;
using InterlinedSync.Configuration;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.Storage;

/// <summary>
/// JSON-backed implementation of <see cref="IAccountStore"/>. Writes to
/// <c>%APPDATA%\interlinedlist-sync\account.json</c> by default.
/// </summary>
public sealed class AccountStore : IAccountStore
{
    private const string FileName = "account.json";

    private readonly IFileSystem _fileSystem;
    private readonly ILogger<AccountStore> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AccountStore(IFileSystem fileSystem, ILogger<AccountStore> logger)
        : this(fileSystem, logger, DefaultPath(fileSystem))
    {
    }

    internal AccountStore(IFileSystem fileSystem, ILogger<AccountStore> logger, string path)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _path = path;
    }

    public async Task<string?> GetSignedInEmailAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_fileSystem.File.Exists(_path))
            {
                return null;
            }

            await using var stream = _fileSystem.File.OpenRead(_path);
            var record = await JsonSerializer.DeserializeAsync<AccountRecord>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(record?.Email) ? null : record!.Email;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Account file at {Path} is corrupt; ignoring.", _path);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSignedInEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            var record = new AccountRecord(email);
            await using var stream = _fileSystem.File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Persisted signed-in account {Email}.", email);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_fileSystem.File.Exists(_path))
            {
                _fileSystem.File.Delete(_path);
                _logger.LogInformation("Cleared signed-in account.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDirectory()
    {
        var directory = _fileSystem.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory) && !_fileSystem.Directory.Exists(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }
    }

    private static string DefaultPath(IFileSystem fileSystem)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return fileSystem.Path.Combine(appData, AppConstants.AppDataFolderName, FileName);
    }

    private sealed record AccountRecord(string Email);
}
