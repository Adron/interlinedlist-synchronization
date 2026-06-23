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
/// Bidirectional sync engine. Runs as a background <see cref="IHostedService"/>:
/// a poll loop pulls server state on an interval, and a watcher loop pushes
/// local file changes to the server. Pull and push are serialized through a
/// single <see cref="SemaphoreSlim"/> so they never collide on the same record.
/// </summary>
/// <remarks>
/// Cross-platform — depends only on <see cref="IInterlinedListClient"/>,
/// <see cref="ISyncStateRepository"/>, <see cref="IFileMapper"/>,
/// <see cref="IFileSystem"/>, and <see cref="IFileWatcher"/> — so it can be
/// exercised in unit tests on macOS or Linux without WPF or Windows APIs.
/// </remarks>
public sealed class SyncEngine : BackgroundService
{
    private readonly IInterlinedListClient _client;
    private readonly ISyncStateRepository _repository;
    private readonly IFileMapper _fileMapper;
    private readonly ISyncStateNotifier _notifier;
    private readonly IFileSystem _fileSystem;
    private readonly IFileWatcher _fileWatcher;
    private readonly IOptionsMonitor<SyncPreferences> _preferences;
    private readonly ILogger<SyncEngine> _logger;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public SyncEngine(
        IInterlinedListClient client,
        ISyncStateRepository repository,
        IFileMapper fileMapper,
        ISyncStateNotifier notifier,
        IFileSystem fileSystem,
        IFileWatcher fileWatcher,
        IOptionsMonitor<SyncPreferences> preferences,
        ILogger<SyncEngine> logger)
    {
        _client = client;
        _repository = repository;
        _fileMapper = fileMapper;
        _notifier = notifier;
        _fileSystem = fileSystem;
        _fileWatcher = fileWatcher;
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

        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Pushes a single local change to the server. Exposed for unit tests; in
    /// production this is called by <see cref="ExecuteAsync"/> as it drains the
    /// watcher's channel.
    /// </summary>
    public async Task<PushResult> PushOnceAsync(LocalChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

            _notifier.SetState(SyncState.Syncing);
            try
            {
                var result = change.Kind switch
                {
                    LocalChangeKind.Deleted => await PushDeleteAsync(change.Path, cancellationToken).ConfigureAwait(false),
                    LocalChangeKind.Renamed => await PushRenameAsync(change, cancellationToken).ConfigureAwait(false),
                    _ => await PushUpsertAsync(change.Path, cancellationToken).ConfigureAwait(false),
                };

                _notifier.SetState(SyncState.Idle);
                await _repository.AppendLogAsync(
                    "push." + result.Action,
                    $"path={change.Path}",
                    cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Push failed for {Path}.", change.Path);
                _notifier.SetState(SyncState.Error);
                await _repository.AppendLogAsync("push.error", ex.Message, cancellationToken).ConfigureAwait(false);
                return PushResult.ForFailure(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error pushing {Path}.", change.Path);
                _notifier.SetState(SyncState.Error);
                await _repository.AppendLogAsync("push.error", ex.Message, cancellationToken).ConfigureAwait(false);
                return PushResult.ForFailure(ex.Message);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SyncEngine started.");

        var prefs = _preferences.CurrentValue;
        if (!string.IsNullOrEmpty(prefs.SyncFolder))
        {
            try
            {
                await _fileWatcher.StartAsync(prefs.SyncFolder, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not start file watcher for {Folder}.", prefs.SyncFolder);
            }
        }

        var pushTask = ConsumePushChannelAsync(stoppingToken);
        var pullTask = RunPullLoopAsync(stoppingToken);

        await Task.WhenAll(pushTask, pullTask).ConfigureAwait(false);

        await _fileWatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("SyncEngine stopped.");
    }

    private async Task RunPullLoopAsync(CancellationToken stoppingToken)
    {
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
    }

    private async Task ConsumePushChannelAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in _fileWatcher.Changes.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await PushOnceAsync(change, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception escaped push for {Path}.", change.Path);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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

    private async Task<PushResult> PushUpsertAsync(string path, CancellationToken cancellationToken)
    {
        if (!_fileSystem.File.Exists(path))
        {
            _logger.LogDebug("Push upsert skipped: {Path} no longer exists.", path);
            return PushResult.ForSkipped("file disappeared");
        }

        var bytes = await _fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var content = Encoding.UTF8.GetString(bytes);
        var sha = ComputeSha256(bytes);
        var title = ExtractTitle(path);

        var existing = await _repository.GetByPathAsync(path, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            var created = await _client.CreateDocumentAsync(title, content, cancellationToken).ConfigureAwait(false);
            var localModified = _fileSystem.File.GetLastWriteTimeUtc(path);
            var record = new SyncStateRecord(
                Id: created.Id,
                LocalPath: path,
                ServerUpdatedAt: created.UpdatedAt,
                LocalModifiedAt: new DateTimeOffset(localModified, TimeSpan.Zero),
                Sha256: sha);
            await _repository.UpsertDocumentAsync(record, cancellationToken).ConfigureAwait(false);
            return PushResult.ForCreated(created.Id);
        }

        if (string.Equals(existing.Sha256, sha, StringComparison.Ordinal))
        {
            _logger.LogDebug("Push upsert no-op for {Path}: content unchanged.", path);
            return PushResult.ForSkipped("hash unchanged");
        }

        var updated = await _client.UpdateDocumentAsync(existing.Id, title, content, cancellationToken).ConfigureAwait(false);
        var localModifiedAt = _fileSystem.File.GetLastWriteTimeUtc(path);
        var refreshed = existing with
        {
            ServerUpdatedAt = updated.UpdatedAt,
            LocalModifiedAt = new DateTimeOffset(localModifiedAt, TimeSpan.Zero),
            Sha256 = sha,
        };
        await _repository.UpsertDocumentAsync(refreshed, cancellationToken).ConfigureAwait(false);
        return PushResult.ForUpdated(existing.Id);
    }

    private async Task<PushResult> PushDeleteAsync(string path, CancellationToken cancellationToken)
    {
        var record = await _repository.GetByPathAsync(path, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            _logger.LogDebug("Push delete skipped: {Path} was not tracked.", path);
            return PushResult.ForSkipped("not tracked");
        }

        await _client.DeleteDocumentAsync(record.Id, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteByIdAsync(record.Id, cancellationToken).ConfigureAwait(false);
        return PushResult.ForDeleted(record.Id);
    }

    private async Task<PushResult> PushRenameAsync(LocalChange change, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(change.OldPath))
        {
            return await PushUpsertAsync(change.Path, cancellationToken).ConfigureAwait(false);
        }

        var oldRecord = await _repository.GetByPathAsync(change.OldPath, cancellationToken).ConfigureAwait(false);
        if (oldRecord is not null)
        {
            await _client.DeleteDocumentAsync(oldRecord.Id, cancellationToken).ConfigureAwait(false);
            await _repository.DeleteByIdAsync(oldRecord.Id, cancellationToken).ConfigureAwait(false);
        }

        return await PushUpsertAsync(change.Path, cancellationToken).ConfigureAwait(false);
    }

    private static string ExtractTitle(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? "untitled" : name;
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

/// <summary>
/// Outcome of pushing a single <see cref="LocalChange"/> to the server.
/// </summary>
public sealed record PushResult(string Action, string? DocumentId, bool Skipped, string? Error)
{
    public static PushResult ForCreated(string id) => new("created", id, false, null);
    public static PushResult ForUpdated(string id) => new("updated", id, false, null);
    public static PushResult ForDeleted(string id) => new("deleted", id, false, null);
    public static PushResult ForSkipped(string reason) => new("skipped", null, true, reason);
    public static PushResult ForFailure(string error) => new("error", null, false, error);
}
