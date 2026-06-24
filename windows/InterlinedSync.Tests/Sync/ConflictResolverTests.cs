using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using FluentAssertions;
using InterlinedSync.API.Models;
using InterlinedSync.FileSystem;
using InterlinedSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.Sync;

public sealed class ConflictResolverTests : IAsyncLifetime
{
    private const string SyncFolder = @"C:\sync";
    private const string DocId = "doc-1";
    private const string OriginalPath = @"C:\sync\Doc.md";

    private string _dbPath = string.Empty;
    private SyncStateRepository _repository = null!;
    private FileMapper _fileMapper = null!;
    private MockFileSystem _fileSystem = null!;
    private ConflictResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conflict-{Guid.NewGuid():N}.db");
        _repository = new SyncStateRepository(_dbPath, NullLogger<SyncStateRepository>.Instance);
        await _repository.InitializeAsync();

        _fileMapper = new FileMapper(_repository);
        _fileSystem = new MockFileSystem();
        _fileSystem.AddDirectory(SyncFolder);

        _resolver = new ConflictResolver(_fileSystem, _fileMapper, _repository, NullLogger<ConflictResolver>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _repository.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch (IOException) { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch (IOException) { }
    }

    [Fact]
    public void Decide_NoOp_WhenLocalAndRemoteUnchanged()
    {
        var serverTime = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var record = new SyncStateRecord(DocId, OriginalPath, serverTime, serverTime, "sha-stable");
        var remote = new Document(DocId, "Doc", null, "ignored", serverTime);

        var decision = _resolver.Decide(record, "sha-stable", serverTime, remote);

        decision.Action.Should().Be(ConflictAction.NoOp);
    }

    [Fact]
    public void Decide_Push_WhenLocalChangedAndRemoteUnchanged()
    {
        var serverTime = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var record = new SyncStateRecord(DocId, OriginalPath, serverTime, serverTime, "sha-old");
        var remote = new Document(DocId, "Doc", null, "server body", serverTime);

        var decision = _resolver.Decide(record, "sha-new", serverTime, remote);

        decision.Action.Should().Be(ConflictAction.Push);
    }

    [Fact]
    public void Decide_Pull_WhenLocalUnchangedAndRemoteChanged()
    {
        var serverTime = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var newerServer = serverTime.AddMinutes(10);
        var record = new SyncStateRecord(DocId, OriginalPath, serverTime, serverTime, "sha-stable");
        var remote = new Document(DocId, "Doc", null, "newer body", newerServer);

        var decision = _resolver.Decide(record, "sha-stable", serverTime, remote);

        decision.Action.Should().Be(ConflictAction.Pull);
    }

    [Fact]
    public void Decide_ConflictCopyAndPull_WhenLocalAndRemoteChanged()
    {
        var serverTime = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var newerServer = serverTime.AddMinutes(10);
        var record = new SyncStateRecord(DocId, OriginalPath, serverTime, serverTime, "sha-old");
        var remote = new Document(DocId, "Doc", null, "server body", newerServer);

        var decision = _resolver.Decide(record, "sha-new", serverTime, remote);

        decision.Action.Should().Be(ConflictAction.ConflictCopyAndPull);
    }

    [Fact]
    public void Decide_ConflictCopyAndPull_WhenLocalDeletedAndRemoteChanged()
    {
        var serverTime = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var newerServer = serverTime.AddMinutes(10);
        var record = new SyncStateRecord(DocId, OriginalPath, serverTime, serverTime, "sha-old");
        var remote = new Document(DocId, "Doc", null, "server body", newerServer);

        var decision = _resolver.Decide(record, null, null, remote);

        decision.Action.Should().Be(ConflictAction.ConflictCopyAndPull);
    }

    [Fact]
    public void Decide_Push_WhenNoRecordAndLocalPresent()
    {
        var remote = new Document(DocId, "Doc", null, "body",
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));

        var decision = _resolver.Decide(null, "sha-local", DateTimeOffset.UtcNow, remote);

        decision.Action.Should().Be(ConflictAction.Push);
    }

    [Fact]
    public void Decide_Pull_WhenNoRecordAndNoLocal()
    {
        var remote = new Document(DocId, "Doc", null, "body",
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));

        var decision = _resolver.Decide(null, null, null, remote);

        decision.Action.Should().Be(ConflictAction.Pull);
    }

    [Fact]
    public async Task ResolveConflictAsync_WritesConflictCopy_FromLocalBytes()
    {
        var record = new SyncStateRecord(DocId, OriginalPath,
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            "sha-old");
        var localBytes = Encoding.UTF8.GetBytes("my local edits");
        var remote = new Document(DocId, "Doc", null, "server body",
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));

        var decision = await _resolver.ResolveConflictAsync(record, localBytes, remote, default);

        decision.Action.Should().Be(ConflictAction.ConflictCopyAndPull);
        decision.ConflictCopyPath.Should().NotBeNullOrEmpty();
        _fileSystem.File.Exists(decision.ConflictCopyPath!).Should().BeTrue();
        _fileSystem.File.ReadAllText(decision.ConflictCopyPath!).Should().Be("my local edits");
        decision.ConflictCopyPath.Should().Contain(".conflict-");
    }

    [Fact]
    public async Task ResolveConflictAsync_SkipsConflictCopy_WhenLocalBytesAbsent()
    {
        var record = new SyncStateRecord(DocId, OriginalPath,
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            "sha-old");
        var remote = new Document(DocId, "Doc", null, "server body",
            new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));

        var decision = await _resolver.ResolveConflictAsync(record, null, remote, default);

        decision.Action.Should().Be(ConflictAction.ConflictCopyAndPull);
        decision.ConflictCopyPath.Should().NotBeNullOrEmpty();
        _fileSystem.File.Exists(decision.ConflictCopyPath!).Should().BeFalse();
    }
}
