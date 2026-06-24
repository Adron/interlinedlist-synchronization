using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using InterlinedSync.API;
using InterlinedSync.API.Models;
using InterlinedSync.Configuration;
using InterlinedSync.Errors;
using InterlinedSync.FileSystem;
using InterlinedSync.Network;
using InterlinedSync.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterlinedSync.Sync;

public sealed class SyncEngine : BackgroundService
{
    private const string PullLockKey = "__pull__";
    private const int MaxRateLimitRetries = 3;
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRateLimitBackoff = TimeSpan.FromSeconds(30);

    private readonly IInterlinedListClient _client;
    private readonly ISyncStateRepository _repository;
    private readonly IFileMapper _fileMapper;
    private readonly ISyncStateNotifier _notifier;
    private readonly IFileSystem _fileSystem;
    private readonly IFileWatcher _fileWatcher;
    private readonly IConflictResolver _conflictResolver;
    private readonly INotificationManager _notifications;
    private readonly INetworkMonitor _networkMonitor;
    private readonly IOptionsMonitor<SyncPreferences> _preferences;
    private readonly ILogger<SyncEngine> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _jitter = new();
    private readonly Lock _pauseGate = new();

    private bool _authPaused;
    private bool _offlinePaused;

    public SyncEngine(
        IInterlinedListClient client,
        ISyncStateRepository repository,
        IFileMapper fileMapper,
        ISyncStateNotifier notifier,
        IFileSystem fileSystem,
        IFileWatcher fileWatcher,
        IConflictResolver conflictResolver,
        INotificationManager notifications,
        INetworkMonitor networkMonitor,
        IOptionsMonitor<SyncPreferences> preferences,
        ILogger<SyncEngine> logger)
    {
        _client = client;
        _repository = repository;
        _fileMapper = fileMapper;
        _notifier = notifier;
        _fileSystem = fileSystem;
        _fileWatcher = fileWatcher;
        _conflictResolver = conflictResolver;
        _notifications = notifications;
        _networkMonitor = networkMonitor;
        _preferences = preferences;
        _logger = logger;

        _offlinePaused = !_networkMonitor.IsOnline;
        _networkMonitor.ConnectivityChanged += OnConnectivityChanged;
    }

    /// <summary>
    /// True while the engine has been paused for an actionable reason
    /// (auth expired or network offline). Push/pull cycles short-circuit
    /// while this is set.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_pauseGate)
            {
                return _authPaused || _offlinePaused;
            }
        }
    }

    /// <summary>
    /// Clears the auth-expired pause flag. Called after the user signs in
    /// again from the tray UI.
    /// </summary>
    public void ResumeAfterReauth()
    {
        bool wasPaused;
        lock (_pauseGate)
        {
            wasPaused = _authPaused;
            _authPaused = false;
        }

        if (wasPaused)
        {
            _logger.LogInformation("Resuming sync after re-authentication.");
            RecomputeState();
        }
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

        if (IsPaused)
        {
            _logger.LogDebug("Pull skipped while engine is paused.");
            return SyncCycleResult.ForSkipped(_authPaused ? "auth expired" : "offline");
        }

        var gate = GetLock(PullLockKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _notifier.SetState(SyncState.Syncing);
            try
            {
                _fileSystem.Directory.CreateDirectory(prefs.SyncFolder);
                await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

                var lastSyncedAt = await _repository.GetLastSyncedAtAsync(cancellationToken).ConfigureAwait(false);

                SyncCycleResult result;
                if (lastSyncedAt is null)
                {
                    var startedAt = DateTimeOffset.UtcNow;
                    IReadOnlyList<Document> remoteDocuments;
                    try
                    {
                        remoteDocuments = await RunWithRetryAsync(
                            ct => _client.GetDocumentsAsync(ct),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (AuthExpiredException ex)
                    {
                        return await HandleAuthExpiredAsync(ex, "pull", cancellationToken).ConfigureAwait(false);
                    }
                    catch (ApiException ex)
                    {
                        return await HandlePullFailureAsync(ex, cancellationToken).ConfigureAwait(false);
                    }

                    result = await ReconcileAsync(prefs.SyncFolder, remoteDocuments, cancellationToken).ConfigureAwait(false);
                    await _repository.SetLastSyncedAtAsync(startedAt, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    DeltaResponse delta;
                    try
                    {
                        delta = await RunWithRetryAsync(
                            ct => _client.FetchDeltaAsync(lastSyncedAt, ct),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (AuthExpiredException ex)
                    {
                        return await HandleAuthExpiredAsync(ex, "pull", cancellationToken).ConfigureAwait(false);
                    }
                    catch (ApiException ex)
                    {
                        return await HandlePullFailureAsync(ex, cancellationToken).ConfigureAwait(false);
                    }

                    result = await ReconcileDeltaAsync(prefs.SyncFolder, delta, cancellationToken).ConfigureAwait(false);
                    await _repository.SetLastSyncedAtAsync(delta.SyncedAt, cancellationToken).ConfigureAwait(false);
                }

                _notifier.SetState(SyncState.Idle);
                await _repository.AppendLogAsync(
                    "pull.complete",
                    $"downloaded={result.Downloaded}, updated={result.Updated}, deleted={result.Deleted}",
                    cancellationToken).ConfigureAwait(false);

                var changed = result.Downloaded + result.Updated + result.Deleted;
                if (changed > 0)
                {
                    await _notifications.NotifySyncCompletedAsync(changed, cancellationToken).ConfigureAwait(false);
                }

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
                await _notifications.NotifySyncFailedAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                return SyncCycleResult.ForFailure(ex.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PushResult> PushOnceAsync(LocalChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (IsPaused)
        {
            _logger.LogDebug("Push skipped while engine is paused.");
            return PushResult.ForSkipped(_authPaused ? "auth expired" : "offline");
        }

        var lockKey = await ResolveLockKeyAsync(change, cancellationToken).ConfigureAwait(false);
        var gate = GetLock(lockKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

                if (result.Action == "conflict" && !string.IsNullOrEmpty(result.ConflictCopyPath))
                {
                    await _notifications.NotifyConflictCopyCreatedAsync(result.ConflictCopyPath, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AuthExpiredException ex)
            {
                await MarkAuthExpiredAsync(ex, "push", change.Path, cancellationToken).ConfigureAwait(false);
                return PushResult.ForFailure(ex.Message);
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Push failed for {Path}.", change.Path);
                _notifier.SetState(SyncState.Error);
                await _repository.AppendLogAsync("push.error", ex.Message, cancellationToken).ConfigureAwait(false);
                await _notifications.NotifySyncFailedAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                return PushResult.ForFailure(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error pushing {Path}.", change.Path);
                _notifier.SetState(SyncState.Error);
                await _repository.AppendLogAsync("push.error", ex.Message, cancellationToken).ConfigureAwait(false);
                await _notifications.NotifySyncFailedAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                return PushResult.ForFailure(ex.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetLock(string key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private async Task<string> ResolveLockKeyAsync(LocalChange change, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetByPathAsync(change.Path, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        if (!string.IsNullOrEmpty(change.OldPath))
        {
            var renamed = await _repository.GetByPathAsync(change.OldPath, cancellationToken).ConfigureAwait(false);
            if (renamed is not null)
            {
                return renamed.Id;
            }
        }

        return "path:" + change.Path;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SyncEngine started.");
        _networkMonitor.Start();

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
        _networkMonitor.Stop();
        _networkMonitor.ConnectivityChanged -= OnConnectivityChanged;
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

    private async Task<SyncCycleResult> ReconcileDeltaAsync(
        string syncFolder,
        DeltaResponse delta,
        CancellationToken cancellationToken)
    {
        var downloaded = 0;
        var updated = 0;
        var deleted = 0;

        foreach (var entry in delta.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDeleted)
            {
                var existingPath = await _fileMapper.GetPathForDocumentIdAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                if (existingPath is not null && _fileSystem.File.Exists(existingPath))
                {
                    TryDelete(existingPath);
                }
                await _repository.DeleteByIdAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                deleted++;
                continue;
            }

            var doc = new Document(entry.Id, entry.Title, entry.FolderId, entry.Content ?? string.Empty, entry.UpdatedAt);
            var targetPath = _fileMapper.GetLocalPath(syncFolder, doc.Title);
            var record = await _repository.GetByIdAsync(entry.Id, cancellationToken).ConfigureAwait(false);

            if (record is null)
            {
                await WriteDocumentAsync(doc, targetPath, cancellationToken).ConfigureAwait(false);
                downloaded++;
                continue;
            }

            if (!string.Equals(record.LocalPath, targetPath, StringComparison.OrdinalIgnoreCase)
                && _fileSystem.File.Exists(record.LocalPath))
            {
                TryDelete(record.LocalPath);
            }

            await WriteDocumentAsync(doc, targetPath, cancellationToken).ConfigureAwait(false);
            updated++;
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
            var created = await RunWithRetryAsync(
                ct => _client.CreateDocumentAsync(title, content, ct),
                cancellationToken).ConfigureAwait(false);
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

        Document remote;
        try
        {
            remote = await RunWithRetryAsync(
                ct => _client.GetDocumentAsync(existing.Id, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthExpiredException)
        {
            throw;
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex, "Could not fetch remote {DocumentId} for conflict check; proceeding with PATCH.", existing.Id);
            return await PushUpdateAsync(existing, title, content, sha, path, cancellationToken).ConfigureAwait(false);
        }

        if (remote.UpdatedAt > existing.ServerUpdatedAt)
        {
            return await ApplyConflictAsync(existing, bytes, remote, cancellationToken).ConfigureAwait(false);
        }

        return await PushUpdateAsync(existing, title, content, sha, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PushResult> PushUpdateAsync(
        SyncStateRecord existing,
        string title,
        string content,
        string sha,
        string path,
        CancellationToken cancellationToken)
    {
        var updated = await RunWithRetryAsync(
            ct => _client.UpdateDocumentAsync(existing.Id, title, content, ct),
            cancellationToken).ConfigureAwait(false);
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

    private async Task<PushResult> ApplyConflictAsync(
        SyncStateRecord existing,
        byte[] localBytes,
        Document remote,
        CancellationToken cancellationToken)
    {
        var decision = await _conflictResolver.ResolveConflictAsync(existing, localBytes, remote, cancellationToken).ConfigureAwait(false);

        var canonicalPath = _fileMapper.GetLocalPath(
            _fileSystem.Path.GetDirectoryName(existing.LocalPath) ?? string.Empty,
            remote.Title);

        await WriteDocumentAsync(remote, canonicalPath, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(existing.LocalPath, canonicalPath, StringComparison.OrdinalIgnoreCase)
            && _fileSystem.File.Exists(existing.LocalPath))
        {
            TryDelete(existing.LocalPath);
        }

        var stored = await _repository.GetByIdAsync(existing.Id, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            var stamped = stored with { LastConflictAt = DateTimeOffset.UtcNow };
            await _repository.UpsertDocumentAsync(stamped, cancellationToken).ConfigureAwait(false);
        }

        return PushResult.ForConflict(existing.Id, decision.ConflictCopyPath);
    }

    private async Task<PushResult> PushDeleteAsync(string path, CancellationToken cancellationToken)
    {
        var record = await _repository.GetByPathAsync(path, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            _logger.LogDebug("Push delete skipped: {Path} was not tracked.", path);
            return PushResult.ForSkipped("not tracked");
        }

        await RunWithRetryAsync(
            async ct =>
            {
                await _client.DeleteDocumentAsync(record.Id, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
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
            await RunWithRetryAsync(
                async ct =>
                {
                    await _client.DeleteDocumentAsync(oldRecord.Id, ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            await _repository.DeleteByIdAsync(oldRecord.Id, cancellationToken).ConfigureAwait(false);
        }

        return await PushUpsertAsync(change.Path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (RateLimitedException ex) when (attempt < MaxRateLimitRetries)
            {
                attempt++;
                var delay = ComputeRateLimitDelay(ex.RetryAfter, attempt);
                _logger.LogWarning(
                    "Rate limited; retrying in {Delay} ms (attempt {Attempt}/{Max}).",
                    delay.TotalMilliseconds,
                    attempt,
                    MaxRateLimitRetries);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeRateLimitDelay(TimeSpan? retryAfter, int attempt)
    {
        if (retryAfter is { } supplied && supplied > TimeSpan.Zero)
        {
            return supplied;
        }

        var baseSeconds = DefaultRateLimitBackoff.TotalSeconds * Math.Pow(2, attempt - 1);
        var jitterSeconds = _jitter.NextDouble();
        var totalSeconds = Math.Min(baseSeconds + jitterSeconds, MaxRateLimitBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(totalSeconds);
    }

    private async Task<SyncCycleResult> HandlePullFailureAsync(ApiException ex, CancellationToken cancellationToken)
    {
        _logger.LogWarning(ex, "Pull failed.");
        _notifier.SetState(SyncState.Error);
        await _repository.AppendLogAsync("pull.error", ex.Message, cancellationToken).ConfigureAwait(false);
        await _notifications.NotifySyncFailedAsync(ex.Message, cancellationToken).ConfigureAwait(false);
        return SyncCycleResult.ForFailure(ex.Message);
    }

    private async Task<SyncCycleResult> HandleAuthExpiredAsync(AuthExpiredException ex, string operation, CancellationToken cancellationToken)
    {
        await MarkAuthExpiredAsync(ex, operation, target: null, cancellationToken).ConfigureAwait(false);
        return SyncCycleResult.ForFailure(ex.Message);
    }

    private async Task MarkAuthExpiredAsync(AuthExpiredException ex, string operation, string? target, CancellationToken cancellationToken)
    {
        lock (_pauseGate)
        {
            _authPaused = true;
        }

        _logger.LogWarning(ex, "Server returned 401 during {Operation} for {Target}; pausing sync.", operation, target ?? "n/a");
        _notifier.SetState(SyncState.AuthExpired);
        await _repository.AppendLogAsync($"{operation}.auth", ex.Message, cancellationToken).ConfigureAwait(false);
        await _notifications.NotifyAuthExpiredAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnConnectivityChanged(object? sender, bool isOnline)
    {
        bool wasPaused;
        lock (_pauseGate)
        {
            wasPaused = _offlinePaused;
            _offlinePaused = !isOnline;
        }

        if (!isOnline)
        {
            _logger.LogWarning("Connectivity lost; pausing sync.");
            _notifier.SetState(SyncState.Offline);
        }
        else if (wasPaused)
        {
            _logger.LogInformation("Connectivity restored; resuming sync.");
            RecomputeState();
        }
    }

    private void RecomputeState()
    {
        lock (_pauseGate)
        {
            if (_authPaused)
            {
                _notifier.SetState(SyncState.AuthExpired);
                return;
            }
            if (_offlinePaused)
            {
                _notifier.SetState(SyncState.Offline);
                return;
            }
        }
        _notifier.SetState(SyncState.Idle);
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
public sealed record PushResult(string Action, string? DocumentId, bool Skipped, string? Error, string? ConflictCopyPath = null)
{
    public static PushResult ForCreated(string id) => new("created", id, false, null);
    public static PushResult ForUpdated(string id) => new("updated", id, false, null);
    public static PushResult ForDeleted(string id) => new("deleted", id, false, null);
    public static PushResult ForSkipped(string reason) => new("skipped", null, true, reason);
    public static PushResult ForFailure(string error) => new("error", null, false, error);
    public static PushResult ForConflict(string id, string? conflictCopyPath) =>
        new("conflict", id, false, null, conflictCopyPath);
}
