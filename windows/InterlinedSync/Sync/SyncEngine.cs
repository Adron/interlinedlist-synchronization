using System.IO;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using InterlinedSync.API;
using InterlinedSync.API.Models;
using InterlinedSync.Configuration;
using InterlinedSync.FileSystem;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterlinedSync.Sync;

/// <summary>
/// Pull-only sync engine. Runs as a background <see cref="IHostedService"/>:
/// once on start, then every <see cref="SyncPreferences.PollIntervalSeconds"/>
/// seconds, it asks the server for the document list and reconciles disk state
/// against the SQLite-backed <see cref="ISyncStateRepository"/>.
/// </summary>
/// <remarks>
/// This class deliberately depends only on cross-platform abstractions —
/// <see cref="IInterlinedListClient"/>, <see cref="ISyncStateRepository"/>,
/// <see cref="IFileMapper"/>, <see cref="IFileSystem"/> — so it can be exercised
/// in unit tests on macOS or Linux without ever touching WPF or Windows APIs.
/// </remarks>
public sealed class SyncEngine : BackgroundService
{
    private readonly IInterlinedListClient _client;
    private readonly ISyncStateRepository _repository;
    private readonly IFileMapper _fileMapper;
    private readonly ISyncStateNotifier _notifier;
    private readonly IFileSystem _fileSystem;
    private readonly IOptionsMonitor<SyncPreferences> _preferences;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        IInterlinedListClient client,
        ISyncStateRepository repository,
        IFileMapper fileMapper,
        ISyncStateNotifier notifier,
        IFileSystem fileSystem,
        IOptionsMonitor<SyncPreferences> preferences,
        ILogger<SyncEngine> logger)
    {
        _client = client;
        _repository = repository;
        _fileMapper = fileMapper;
        _notifier = notifier;
        _fileSystem = fileSystem;
        _preferences = preferences;
        _logger = logger;
    }

    /// <summary>
    /// Performs a single pull cycle. Exposed for unit tests so they can drive
    /// the engine deterministically rather than waiting on the poll timer.
    /// </summary>
    public async Task<SyncCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var prefs = _preferences.CurrentValue;
        if (string.IsNullOrEmpty(prefs.SyncFolder))
        {
            _logger.LogDebug("Sync folder is not configured; skipping pull cycle.");
            return SyncCycleResult.ForSkipped("sync folder not configured");
        }

        _notifier.SetState(SyncState.Syncing);
        try
        {
            _fileSystem.Directory.CreateDirectory(prefs.SyncFolder);
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<Document> remoteDocuments;
            try
            {
                remoteDocuments = await _client.GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Pull failed: could not fetch document list.");
                _notifier.SetState(SyncState.Error);
                await _repository.AppendLogAsync("pull.error", ex.Message, cancellationToken).ConfigureAwait(false);
                return SyncCycleResult.ForFailure(ex.Message);
            }

            var result = await ReconcileAsync(prefs.SyncFolder, remoteDocuments, cancellationToken).ConfigureAwait(false);

            _notifier.SetState(SyncState.Idle);
            await _repository.AppendLogAsync(
                "pull.complete",
                $"downloaded={result.Downloaded}, updated={result.Updated}, deleted={result.Deleted}",
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during pull cycle.");
            _notifier.SetState(SyncState.Error);
            await _repository.AppendLogAsync("pull.error", ex.Message, cancellationToken).ConfigureAwait(false);
            return SyncCycleResult.ForFailure(ex.Message);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SyncEngine started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception escaped pull cycle.");
                _notifier.SetState(SyncState.Error);
            }

            var interval = GetPollInterval();
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("SyncEngine stopped.");
    }

    private TimeSpan GetPollInterval()
    {
        var prefs = _preferences.CurrentValue;
        var seconds = Math.Max(prefs.PollIntervalSeconds, AppConstants.MinimumPollIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<SyncCycleResult> ReconcileAsync(
        string syncFolder,
        IReadOnlyList<Document> remoteDocuments,
        CancellationToken cancellationToken)
    {
        var existing = await _repository.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var existingById = existing.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var remoteIds = new HashSet<string>(StringComparer.Ordinal);

        var downloaded = 0;
        var updated = 0;
        var deleted = 0;

        foreach (var doc in remoteDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remoteIds.Add(doc.Id);

            var targetPath = _fileMapper.GetLocalPath(syncFolder, doc.Title);

            if (!existingById.TryGetValue(doc.Id, out var record))
            {
                await WriteDocumentAsync(doc, targetPath, cancellationToken).ConfigureAwait(false);
                downloaded++;
                continue;
            }

            if (doc.UpdatedAt > record.ServerUpdatedAt || !_fileSystem.File.Exists(record.LocalPath))
            {
                // Title may have changed — delete the old file before writing the new one.
                if (!string.Equals(record.LocalPath, targetPath, StringComparison.OrdinalIgnoreCase)
                    && _fileSystem.File.Exists(record.LocalPath))
                {
                    TryDelete(record.LocalPath);
                }

                await WriteDocumentAsync(doc, targetPath, cancellationToken).ConfigureAwait(false);
                updated++;
            }
        }

        foreach (var record in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remoteIds.Contains(record.Id))
            {
                continue;
            }

            if (_fileSystem.File.Exists(record.LocalPath))
            {
                TryDelete(record.LocalPath);
            }

            await _repository.DeleteByIdAsync(record.Id, cancellationToken).ConfigureAwait(false);
            deleted++;
        }

        return new SyncCycleResult(downloaded, updated, deleted, Skipped: false, Error: null);
    }

    private async Task WriteDocumentAsync(Document doc, string targetPath, CancellationToken cancellationToken)
    {
        var directory = _fileSystem.Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        var bytes = Encoding.UTF8.GetBytes(doc.Content);
        var tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");

        await _fileSystem.File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);

        if (_fileSystem.File.Exists(targetPath))
        {
            _fileSystem.File.Delete(targetPath);
        }
        _fileSystem.File.Move(tempPath, targetPath);

        var localModified = _fileSystem.File.Exists(targetPath)
            ? new DateTimeOffset(_fileSystem.File.GetLastWriteTimeUtc(targetPath), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;

        var record = new SyncStateRecord(
            Id: doc.Id,
            LocalPath: targetPath,
            ServerUpdatedAt: doc.UpdatedAt,
            LocalModifiedAt: localModified,
            Sha256: ComputeSha256(bytes));

        await _repository.UpsertDocumentAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private void TryDelete(string path)
    {
        try
        {
            _fileSystem.File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied deleting {Path}.", path);
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Outcome of a single pull cycle. Exposed so callers (and tests) can assert
/// what the engine actually did.
/// </summary>
public sealed record SyncCycleResult(int Downloaded, int Updated, int Deleted, bool Skipped, string? Error)
{
    public static SyncCycleResult ForSkipped(string reason) => new(0, 0, 0, true, reason);
    public static SyncCycleResult ForFailure(string error) => new(0, 0, 0, false, error);
}
