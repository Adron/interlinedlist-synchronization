using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Net;
using FluentAssertions;
using InterlinedSync.API;
using InterlinedSync.API.Models;
using InterlinedSync.Configuration;
using InterlinedSync.FileSystem;
using InterlinedSync.Sync;
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
            LoginEndpoint = "/api/auth/login",
        });
        var httpClient = _http.ToHttpClient();
        _client = new InterlinedListClient(httpClient, apiOptions, NullLogger<InterlinedListClient>.Instance);

        var prefs = new SyncPreferences
        {
            SyncFolder = SyncFolder,
            PollIntervalSeconds = 30,
        };
        var monitor = new TestOptionsMonitor<SyncPreferences>(prefs);

        _engine = new SyncEngine(
            _client,
            _repository,
            _fileMapper,
            _notifier,
            _fileSystem,
            monitor,
            NullLogger<SyncEngine>.Instance);
    }

    public async Task DisposeAsync()
    {
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
            _client, _repository, _fileMapper, _notifier, _fileSystem, monitor,
            NullLogger<SyncEngine>.Instance);

        var result = await engine.RunOnceAsync(default);

        result.Skipped.Should().BeTrue();
    }

    private void StubDocuments(params Document[] docs)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            docs,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        _http.When(HttpMethod.Get, "*/api/documents")
             .Respond("application/json", json);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T current) => CurrentValue = current;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
