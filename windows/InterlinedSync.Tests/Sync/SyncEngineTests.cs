using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Text;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.API.Models;
using InterlinedSync.Configuration;
using InterlinedSync.FileSystem;
using InterlinedSync.Network.Mocks;
using InterlinedSync.Notifications.Mocks;
using InterlinedSync.Sync;
using InterlinedSync.Tests.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;
using Xunit;

namespace InterlinedSync.Tests.Sync;

public sealed class SyncEngineTests : IAsyncLifetime
{
    private const string SyncFolder = @"C:\sync";

    private string _dbPath = string.Empty;
    private SyncStateRepository _repository = null!;
    private FileMapper _fileMapper = null!;
    private SyncStateNotifier _notifier = null!;
    private MockFileSystem _fileSystem = null!;
    private MockHttpMessageHandler _http = null!;
    private InterlinedListClient _client = null!;
    private StubFileWatcher _watcher = null!;
    private ConflictResolver _conflictResolver = null!;
    private MockNotificationManager _notifications = null!;
    private StubNetworkMonitor _networkMonitor = null!;
    private SyncEngine _engine = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync-engine-{Guid.NewGuid():N}.db");
        _repository = new SyncStateRepository(_dbPath, NullLogger<SyncStateRepository>.Instance);
        await _repository.InitializeAsync();

        _fileMapper = new FileMapper(_repository);
        _notifier = new SyncStateNotifier(NullLogger<SyncStateNotifier>.Instance);
        _fileSystem = new MockFileSystem();
        _fileSystem.AddDirectory(SyncFolder);

        _http = new MockHttpMessageHandler();
        var apiOptions = Options.Create(new ApiOptions
        {
            BaseUrl = "https://test.invalid",
            LoginEndpoint = "/api/auth/sync-token",
        });
        var httpClient = _http.ToHttpClient();
        _client = new InterlinedListClient(httpClient, apiOptions, NullLogger<InterlinedListClient>.Instance);

        var prefs = new SyncPreferences
        {
            SyncFolder = SyncFolder,
            PollIntervalSeconds = 30,
        };
        var monitor = new TestOptionsMonitor<SyncPreferences>(prefs);

        _watcher = new StubFileWatcher();
        _conflictResolver = new ConflictResolver(_fileSystem, _fileMapper, _repository, NullLogger<ConflictResolver>.Instance);
        _notifications = new MockNotificationManager();
        _networkMonitor = new StubNetworkMonitor();

        _engine = new SyncEngine(
            _client,
            _repository,
            _fileMapper,
            _notifier,
            _fileSystem,
            _watcher,
            _conflictResolver,
            _notifications,
            _networkMonitor,
            monitor,
            NullLogger<SyncEngine>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _watcher.DisposeAsync();
        await _repository.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch (IOException) { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch (IOException) { }
        _http.Dispose();
    }

    [Fact]
    public async Task RunOnceAsync_DownloadsNewDocuments()
    {
        var doc = new Document("doc-1", "Hello World", null, "# Hello\n", new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        StubDocuments(doc);

        var result = await _engine.RunOnceAsync(default);

        result.Downloaded.Should().Be(1);
        result.Updated.Should().Be(0);
        result.Deleted.Should().Be(0);

        var path = Path.Combine(SyncFolder, "Hello World.md");
        _fileSystem.File.Exists(path).Should().BeTrue();
        _fileSystem.File.ReadAllText(path).Should().Be("# Hello\n");

        (await _repository.GetByIdAsync("doc-1")).Should().NotBeNull();
        _notifier.Current.Should().Be(SyncState.Idle);
    }

    [Fact]
    public async Task RunOnceAsync_OverwritesWhenServerNewer()
    {
        var path = Path.Combine(SyncFolder, "Doc.md");
        _fileSystem.AddFile(path, new MockFileData("old content"));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1",
            path,
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            "oldsha"));

        var updated = new Document("doc-1", "Doc", null, "new content", new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        StubDocuments(updated);

        var result = await _engine.RunOnceAsync(default);

        result.Updated.Should().Be(1);
        _fileSystem.File.ReadAllText(path).Should().Be("new content");
    }

    [Fact]
    public async Task RunOnceAsync_SkipsWhenLocalUpToDate()
    {
        var path = Path.Combine(SyncFolder, "Doc.md");
        var serverTime = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

        _fileSystem.AddFile(path, new MockFileData("synced content"));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", path, serverTime, serverTime, "sha"));

        var sameDoc = new Document("doc-1", "Doc", null, "different but ignored", serverTime);
        StubDocuments(sameDoc);

        var result = await _engine.RunOnceAsync(default);

        result.Downloaded.Should().Be(0);
        result.Updated.Should().Be(0);
        _fileSystem.File.ReadAllText(path).Should().Be("synced content");
    }

    [Fact]
    public async Task RunOnceAsync_DeletesRemovedDocuments()
    {
        var path = Path.Combine(SyncFolder, "Goner.md");
        _fileSystem.AddFile(path, new MockFileData("doomed"));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-gone", path,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "sha"));

        StubDocuments(); // empty list

        var result = await _engine.RunOnceAsync(default);

        result.Deleted.Should().Be(1);
        _fileSystem.File.Exists(path).Should().BeFalse();
        (await _repository.GetByIdAsync("doc-gone")).Should().BeNull();
    }

    [Fact]
    public async Task RunOnceAsync_ReWritesWhenLocalFileMissing()
    {
        var path = Path.Combine(SyncFolder, "Resurrected.md");
        var serverTime = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", path, serverTime, serverTime, "sha"));

        // Note: file is NOT on disk, but server reports same updatedAt.
        var doc = new Document("doc-1", "Resurrected", null, "back from the dead", serverTime);
        StubDocuments(doc);

        var result = await _engine.RunOnceAsync(default);

        result.Updated.Should().Be(1);
        _fileSystem.File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task RunOnceAsync_RenamesLocalFile_WhenTitleChanges()
    {
        var oldPath = Path.Combine(SyncFolder, "Old.md");
        var newPath = Path.Combine(SyncFolder, "New.md");
        _fileSystem.AddFile(oldPath, new MockFileData("body"));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", oldPath,
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            "sha"));

        var renamed = new Document("doc-1", "New", null, "body", new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        StubDocuments(renamed);

        await _engine.RunOnceAsync(default);

        _fileSystem.File.Exists(oldPath).Should().BeFalse();
        _fileSystem.File.Exists(newPath).Should().BeTrue();
        var record = await _repository.GetByIdAsync("doc-1");
        record!.LocalPath.Should().Be(newPath);
    }

    [Fact]
    public async Task RunOnceAsync_SetsErrorState_OnApiFailure()
    {
        _http.When(HttpMethod.Get, "*/api/documents")
             .Respond(HttpStatusCode.InternalServerError);

        var result = await _engine.RunOnceAsync(default);

        result.Error.Should().NotBeNullOrEmpty();
        _notifier.Current.Should().Be(SyncState.Error);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsWhenSyncFolderUnconfigured()
    {
        var prefs = new SyncPreferences { SyncFolder = string.Empty };
        var monitor = new TestOptionsMonitor<SyncPreferences>(prefs);
        var engine = new SyncEngine(
            _client, _repository, _fileMapper, _notifier, _fileSystem, _watcher, _conflictResolver,
            _notifications, _networkMonitor, monitor,
            NullLogger<SyncEngine>.Instance);

        var result = await engine.RunOnceAsync(default);

        result.Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task PushOnceAsync_CreatesDocument_WhenPathUntracked()
    {
        var path = Path.Combine(SyncFolder, "Brand New.md");
        _fileSystem.AddFile(path, new MockFileData("hello world"));

        var created = new Document("doc-new", "Brand New", null, "hello world",
            new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero));
        StubCreate(created);

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Created, path), default);

        result.Action.Should().Be("created");
        result.DocumentId.Should().Be("doc-new");
        (await _repository.GetByIdAsync("doc-new")).Should().NotBeNull();
    }

    [Fact]
    public async Task PushOnceAsync_UpdatesDocument_WhenContentChanged()
    {
        var path = Path.Combine(SyncFolder, "Existing.md");
        _fileSystem.AddFile(path, new MockFileData("updated body"));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", path,
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            "oldsha"));

        var unchangedRemote = new Document("doc-1", "Existing", null, "server body",
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero));
        StubGetDocument("doc-1", unchangedRemote);

        var updated = new Document("doc-1", "Existing", null, "updated body",
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        StubUpdate("doc-1", updated);

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Modified, path), default);

        result.Action.Should().Be("updated");
        var record = await _repository.GetByIdAsync("doc-1");
        record!.Sha256.Should().NotBe("oldsha");
        record.ServerUpdatedAt.Should().Be(updated.UpdatedAt);
    }

    [Fact]
    public async Task PushOnceAsync_SkipsUpdate_WhenHashUnchanged()
    {
        var path = Path.Combine(SyncFolder, "Unchanged.md");
        var content = "stable body"u8.ToArray();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

        _fileSystem.AddFile(path, new MockFileData(content));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", path,
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero),
            sha));

        _http.When(HttpMethod.Patch, "*/api/documents/doc-1")
             .Respond(HttpStatusCode.InternalServerError); // would fail if invoked

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Modified, path), default);

        result.Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task PushOnceAsync_DeletesDocument_OnLocalDelete()
    {
        var path = Path.Combine(SyncFolder, "Goner.md");
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", path,
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero),
            "sha"));

        StubDelete("doc-1", HttpStatusCode.NoContent);

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Deleted, path), default);

        result.Action.Should().Be("deleted");
        (await _repository.GetByIdAsync("doc-1")).Should().BeNull();
    }

    [Fact]
    public async Task PushOnceAsync_DeleteIsNoOp_WhenPathUntracked()
    {
        var path = Path.Combine(SyncFolder, "Phantom.md");

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Deleted, path), default);

        result.Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task PushOnceAsync_RenameDeletesOldAndCreatesNew()
    {
        var oldPath = Path.Combine(SyncFolder, "Old.md");
        var newPath = Path.Combine(SyncFolder, "New.md");
        _fileSystem.AddFile(newPath, new MockFileData("body"));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", oldPath,
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero),
            "oldsha"));

        StubDelete("doc-1", HttpStatusCode.NoContent);
        var created = new Document("doc-2", "New", null, "body",
            new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero));
        StubCreate(created);

        var result = await _engine.PushOnceAsync(
            new LocalChange(LocalChangeKind.Renamed, newPath, oldPath),
            default);

        result.Action.Should().Be("created");
        (await _repository.GetByIdAsync("doc-1")).Should().BeNull();
        (await _repository.GetByIdAsync("doc-2")).Should().NotBeNull();
    }

    [Fact]
    public async Task PushOnceAsync_ReportsError_OnApiFailure()
    {
        var path = Path.Combine(SyncFolder, "Boom.md");
        _fileSystem.AddFile(path, new MockFileData("data"));

        _http.When(HttpMethod.Post, "*/api/documents")
             .Respond(HttpStatusCode.InternalServerError);

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Created, path), default);

        result.Error.Should().NotBeNullOrEmpty();
        _notifier.Current.Should().Be(SyncState.Error);
    }

    [Fact]
    public async Task Sync_usesFullListOnFirstSync()
    {
        var doc = new Document("doc-1", "Initial", null, "first body",
            new DateTimeOffset(2026, 6, 22, 10, 0, 0, TimeSpan.Zero));
        StubDocuments(doc);
        var deltaCalls = StubDeltaCounter();

        await _engine.RunOnceAsync(default);

        deltaCalls().Should().Be(0);
        (await _repository.GetByIdAsync("doc-1")).Should().NotBeNull();
        (await _repository.GetLastSyncedAtAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Sync_persistsLastSyncedAtFromDelta()
    {
        await _repository.SetLastSyncedAtAsync(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero));

        var syncedAt = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        StubDelta(syncedAt,
            new DocumentDelta("doc-1", "Fresh", "body", null,
                new DateTimeOffset(2026, 6, 22, 11, 0, 0, TimeSpan.Zero), null));

        var result = await _engine.RunOnceAsync(default);

        result.Downloaded.Should().Be(1);
        var stored = await _repository.GetLastSyncedAtAsync();
        stored.Should().NotBeNull();
        stored!.Value.Should().BeCloseTo(syncedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Sync_appliesDeltaTombstones()
    {
        var path = Path.Combine(SyncFolder, "Doomed.md");
        _fileSystem.AddFile(path, new MockFileData("doomed body"));
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-dead", path,
            new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero),
            "sha"));
        await _repository.SetLastSyncedAtAsync(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero));

        StubDelta(new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero),
            new DocumentDelta("doc-dead", "Doomed", null, null,
                new DateTimeOffset(2026, 6, 22, 11, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 22, 11, 45, 0, TimeSpan.Zero)));

        var result = await _engine.RunOnceAsync(default);

        result.Deleted.Should().Be(1);
        _fileSystem.File.Exists(path).Should().BeFalse();
        (await _repository.GetByIdAsync("doc-dead")).Should().BeNull();
    }

    [Fact]
    public async Task PushOnceAsync_ProducesConflictCopy_WhenRemoteChangedSinceLastSync()
    {
        var path = Path.Combine(SyncFolder, "Conflicted.md");
        _fileSystem.AddFile(path, new MockFileData("local edits"));
        var lastSyncedServerTime = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", path, lastSyncedServerTime, lastSyncedServerTime, "oldsha"));

        var newerRemote = new Document("doc-1", "Conflicted", null, "server-wins body",
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        StubGetDocument("doc-1", newerRemote);

        _http.When(HttpMethod.Patch, "*/api/documents/doc-1")
             .Respond(System.Net.HttpStatusCode.InternalServerError);

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Modified, path), default);

        result.Action.Should().Be("conflict");
        result.ConflictCopyPath.Should().NotBeNullOrEmpty();
        _fileSystem.File.Exists(result.ConflictCopyPath!).Should().BeTrue();
        _fileSystem.File.ReadAllText(result.ConflictCopyPath!).Should().Be("local edits");
        _fileSystem.File.ReadAllText(path).Should().Be("server-wins body");

        var record = await _repository.GetByIdAsync("doc-1");
        record!.LastConflictAt.Should().NotBeNull();
        record.ServerUpdatedAt.Should().Be(newerRemote.UpdatedAt);
    }

    [Fact]
    public async Task PushOnceAsync_RunsInParallel_AcrossDifferentDocuments()
    {
        var pathA = Path.Combine(SyncFolder, "DocA.md");
        var pathB = Path.Combine(SyncFolder, "DocB.md");
        _fileSystem.AddFile(pathA, new MockFileData("body A"));
        _fileSystem.AddFile(pathB, new MockFileData("body B"));

        var docA = new Document("doc-A", "DocA", null, "body A",
            new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero));
        var docB = new Document("doc-B", "DocB", null, "body B",
            new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero));

        var inflight = 0;
        var maxInflight = 0;
        var sync = new object();
        _http.When(HttpMethod.Post, "*/api/documents")
             .Respond(async _ =>
             {
                 int current;
                 lock (sync)
                 {
                     inflight++;
                     current = inflight;
                     if (current > maxInflight) { maxInflight = current; }
                 }
                 await Task.Delay(80);
                 lock (sync) { inflight--; }

                 var envelope = new { message = "Document created", document = docA };
                 var json = System.Text.Json.JsonSerializer.Serialize(envelope, StubJson);
                 return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                 {
                     Content = new StringContent(json, Encoding.UTF8, "application/json"),
                 };
             });

        var taskA = _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Created, pathA), default);
        var taskB = _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Created, pathB), default);
        await Task.WhenAll(taskA, taskB);

        maxInflight.Should().BeGreaterThan(1, "two unrelated docs must not serialize through a global lock");
    }

    [Fact]
    public async Task RunOnceAsync_PausesEngine_OnAuthExpired()
    {
        _http.When(HttpMethod.Get, "*/api/documents")
             .Respond(HttpStatusCode.Unauthorized);

        var result = await _engine.RunOnceAsync(default);

        result.Error.Should().NotBeNullOrEmpty();
        _notifier.Current.Should().Be(SyncState.AuthExpired);
        _engine.IsPaused.Should().BeTrue();
        _notifications.Calls.Should().ContainSingle(c => c.Kind == NotificationKind.AuthExpired);
    }

    [Fact]
    public async Task RunOnceAsync_SkipsWhenAuthPaused_UntilResumed()
    {
        _http.When(HttpMethod.Get, "*/api/documents")
             .Respond(HttpStatusCode.Unauthorized);

        await _engine.RunOnceAsync(default);
        _engine.IsPaused.Should().BeTrue();

        var skipped = await _engine.RunOnceAsync(default);
        skipped.Skipped.Should().BeTrue();
        skipped.Error.Should().Be("auth expired");

        _engine.ResumeAfterReauth();
        _engine.IsPaused.Should().BeFalse();
        _notifier.Current.Should().Be(SyncState.Idle);
    }

    [Fact]
    public async Task RunOnceAsync_PausesEngine_WhenOffline()
    {
        _networkMonitor.SetOnline(false);
        _engine.IsPaused.Should().BeTrue();
        _notifier.Current.Should().Be(SyncState.Offline);

        var result = await _engine.RunOnceAsync(default);

        result.Skipped.Should().BeTrue();
        result.Error.Should().Be("offline");
    }

    [Fact]
    public async Task RunOnceAsync_ResumesEngine_OnConnectivityRestored()
    {
        _networkMonitor.SetOnline(false);
        _engine.IsPaused.Should().BeTrue();

        _networkMonitor.SetOnline(true);
        _engine.IsPaused.Should().BeFalse();
        _notifier.Current.Should().Be(SyncState.Idle);

        StubDocuments();
        var result = await _engine.RunOnceAsync(default);
        result.Skipped.Should().BeFalse();
    }

    [Fact]
    public async Task RunOnceAsync_RetriesAfterRateLimit_WithBackoff()
    {
        var attempt = 0;
        var docJson = System.Text.Json.JsonSerializer.Serialize(new { documents = Array.Empty<Document>() }, StubJson);

        _http.When(HttpMethod.Get, "*/api/documents")
             .Respond(_ =>
             {
                 attempt++;
                 if (attempt == 1)
                 {
                     var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                     resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                     return resp;
                 }
                 return new HttpResponseMessage(HttpStatusCode.OK)
                 {
                     Content = new StringContent(docJson, Encoding.UTF8, "application/json"),
                 };
             });

        var result = await _engine.RunOnceAsync(default);

        attempt.Should().BeGreaterOrEqualTo(2);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task RunOnceAsync_NotifiesCompletion_WhenChangesApplied()
    {
        var doc = new Document("doc-1", "Hello", null, "# Hello", new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        StubDocuments(doc);

        await _engine.RunOnceAsync(default);

        _notifications.Calls.Should().ContainSingle(c => c.Kind == NotificationKind.SyncCompleted);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotNotifyCompletion_WhenNothingChanged()
    {
        StubDocuments();

        await _engine.RunOnceAsync(default);

        _notifications.Calls.Should().NotContain(c => c.Kind == NotificationKind.SyncCompleted);
    }

    [Fact]
    public async Task RunOnceAsync_NotifiesFailure_OnApiError()
    {
        _http.When(HttpMethod.Get, "*/api/documents")
             .Respond(HttpStatusCode.InternalServerError);

        await _engine.RunOnceAsync(default);

        _notifications.Calls.Should().ContainSingle(c => c.Kind == NotificationKind.SyncFailed);
    }

    [Fact]
    public async Task PushOnceAsync_NotifiesConflict_WithPath()
    {
        var path = Path.Combine(SyncFolder, "Conflicted.md");
        _fileSystem.AddFile(path, new MockFileData("local edits"));
        var lastSyncedServerTime = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        await _repository.UpsertDocumentAsync(new SyncStateRecord(
            "doc-1", path, lastSyncedServerTime, lastSyncedServerTime, "oldsha"));

        var newerRemote = new Document("doc-1", "Conflicted", null, "server body",
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        StubGetDocument("doc-1", newerRemote);

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Modified, path), default);

        result.Action.Should().Be("conflict");
        var call = _notifications.Calls.Should().ContainSingle(c => c.Kind == NotificationKind.ConflictCopy).Subject;
        call.Detail.Should().Be(result.ConflictCopyPath);
    }

    [Fact]
    public async Task PushOnceAsync_PausesEngine_OnAuthExpired()
    {
        var path = Path.Combine(SyncFolder, "AuthBoom.md");
        _fileSystem.AddFile(path, new MockFileData("data"));

        _http.When(HttpMethod.Post, "*/api/documents")
             .Respond(HttpStatusCode.Unauthorized);

        var result = await _engine.PushOnceAsync(new LocalChange(LocalChangeKind.Created, path), default);

        result.Error.Should().NotBeNullOrEmpty();
        _engine.IsPaused.Should().BeTrue();
        _notifier.Current.Should().Be(SyncState.AuthExpired);
        _notifications.Calls.Should().ContainSingle(c => c.Kind == NotificationKind.AuthExpired);
    }

    private static readonly System.Text.Json.JsonSerializerOptions StubJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private void StubDelta(DateTimeOffset syncedAt, params DocumentDelta[] documents)
    {
        var payload = new DeltaResponse(syncedAt, Array.Empty<Folder>(), documents);
        var json = System.Text.Json.JsonSerializer.Serialize(payload, StubJson);
        _http.When(HttpMethod.Get, "*/api/documents/sync*")
             .Respond("application/json", json);
    }

    private Func<int> StubDeltaCounter()
    {
        var matched = _http.When(HttpMethod.Get, "*/api/documents/sync*")
                           .Respond("application/json",
                               "{\"lastSyncAt\":\"2026-06-22T12:00:00+00:00\",\"folders\":[],\"documents\":[]}");
        return () => _http.GetMatchCount(matched);
    }

    private void StubCreate(Document doc)
    {
        var envelope = new { message = "Document created", document = doc };
        var json = System.Text.Json.JsonSerializer.Serialize(envelope, StubJson);
        _http.When(HttpMethod.Post, "*/api/documents")
             .Respond("application/json", json);
    }

    private void StubUpdate(string id, Document doc)
    {
        var envelope = new { message = "Document updated", document = doc };
        var json = System.Text.Json.JsonSerializer.Serialize(envelope, StubJson);
        _http.When(HttpMethod.Patch, $"*/api/documents/{id}")
             .Respond("application/json", json);
    }

    private void StubGetDocument(string id, Document doc)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(doc, StubJson);
        _http.When(HttpMethod.Get, $"*/api/documents/{id}")
             .Respond("application/json", json);
    }

    private void StubDelete(string id, HttpStatusCode status)
    {
        _http.When(HttpMethod.Delete, $"*/api/documents/{id}")
             .Respond(status);
    }

    private void StubDocuments(params Document[] docs)
    {
        var envelope = new { documents = docs };
        var json = System.Text.Json.JsonSerializer.Serialize(envelope, StubJson);
        _http.When(HttpMethod.Get, "*/api/documents")
             .Respond("application/json", json);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = new();
        public TestOptionsMonitor(T current) => CurrentValue = current;
        public T CurrentValue { get; private set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return new Unsubscribe(() => _listeners.Remove(listener));
        }

        public void Set(T value)
        {
            CurrentValue = value;
            foreach (var l in _listeners.ToArray())
            {
                l(value, null);
            }
        }

        private sealed class Unsubscribe : IDisposable
        {
            private readonly Action _onDispose;
            public Unsubscribe(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }

    [Fact]
    public void CurrentPollInterval_ReflectsOptionsMonitorUpdates()
    {
        var prefs = new SyncPreferences { SyncFolder = SyncFolder, PollIntervalSeconds = 30 };
        var monitor = new TestOptionsMonitor<SyncPreferences>(prefs);
        var engine = new SyncEngine(
            _client, _repository, _fileMapper, _notifier, _fileSystem, _watcher, _conflictResolver,
            _notifications, _networkMonitor, monitor, NullLogger<SyncEngine>.Instance);

        engine.CurrentPollInterval.Should().Be(TimeSpan.FromSeconds(30));

        monitor.Set(new SyncPreferences { SyncFolder = SyncFolder, PollIntervalSeconds = 120 });

        engine.CurrentPollInterval.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void CurrentPollInterval_ClampsToMinimum()
    {
        var prefs = new SyncPreferences { SyncFolder = SyncFolder, PollIntervalSeconds = 1 };
        var monitor = new TestOptionsMonitor<SyncPreferences>(prefs);
        var engine = new SyncEngine(
            _client, _repository, _fileMapper, _notifier, _fileSystem, _watcher, _conflictResolver,
            _notifications, _networkMonitor, monitor, NullLogger<SyncEngine>.Instance);

        engine.CurrentPollInterval.Should().Be(TimeSpan.FromSeconds(AppConstants.MinimumPollIntervalSeconds));
    }
}
