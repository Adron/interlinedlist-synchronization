using System.IO;
using FluentAssertions;
using InterlinedSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.Sync;

public sealed class SyncStateRepositoryTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SyncStateRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sync-state-{Guid.NewGuid():N}.db");
        _repo = new SyncStateRepository(_dbPath, NullLogger<SyncStateRepository>.Instance);
        await _repo.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _repo.DisposeAsync();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var act = async () =>
        {
            await _repo.InitializeAsync();
            await _repo.InitializeAsync();
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Upsert_Then_GetById_RoundTripsRecord()
    {
        var record = NewRecord("doc-1", "/tmp/a.md");

        await _repo.UpsertDocumentAsync(record);
        var loaded = await _repo.GetByIdAsync("doc-1");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("doc-1");
        loaded.LocalPath.Should().Be("/tmp/a.md");
        loaded.Sha256.Should().Be(record.Sha256);
        loaded.ServerUpdatedAt.Should().BeCloseTo(record.ServerUpdatedAt, TimeSpan.FromMilliseconds(1));
        loaded.LocalModifiedAt.Should().BeCloseTo(record.LocalModifiedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Upsert_OverwritesExistingRow()
    {
        var first = NewRecord("doc-1", "/tmp/a.md", sha: "aaaa");
        var second = first with { Sha256 = "bbbb", LocalPath = "/tmp/b.md" };

        await _repo.UpsertDocumentAsync(first);
        await _repo.UpsertDocumentAsync(second);

        var loaded = await _repo.GetByIdAsync("doc-1");
        loaded!.Sha256.Should().Be("bbbb");
        loaded.LocalPath.Should().Be("/tmp/b.md");
    }

    [Fact]
    public async Task GetByPath_IsCaseInsensitive()
    {
        await _repo.UpsertDocumentAsync(NewRecord("doc-1", "/tmp/Hello.md"));

        var loaded = await _repo.GetByPathAsync("/TMP/hello.md");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be("doc-1");
    }

    [Fact]
    public async Task DeleteById_RemovesRow()
    {
        await _repo.UpsertDocumentAsync(NewRecord("doc-1", "/tmp/a.md"));

        await _repo.DeleteByIdAsync("doc-1");

        (await _repo.GetByIdAsync("doc-1")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteById_OnMissingId_IsNoOp()
    {
        var act = () => _repo.DeleteByIdAsync("does-not-exist");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListAll_ReturnsEveryRecord()
    {
        await _repo.UpsertDocumentAsync(NewRecord("doc-1", "/tmp/a.md"));
        await _repo.UpsertDocumentAsync(NewRecord("doc-2", "/tmp/b.md"));
        await _repo.UpsertDocumentAsync(NewRecord("doc-3", "/tmp/c.md"));

        var all = await _repo.ListAllAsync();

        all.Select(r => r.Id).Should().BeEquivalentTo(["doc-1", "doc-2", "doc-3"]);
    }

    [Fact]
    public async Task AppendLog_StoresRowsWithoutThrowing()
    {
        var act = async () =>
        {
            await _repo.AppendLogAsync("pull.complete", "downloaded=2");
            await _repo.AppendLogAsync("pull.error", "boom");
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetById_OnMissingId_ReturnsNull()
    {
        var loaded = await _repo.GetByIdAsync("never-stored");
        loaded.Should().BeNull();
    }

    private static SyncStateRecord NewRecord(string id, string path, string sha = "deadbeef")
    {
        var now = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
        return new SyncStateRecord(id, path, now, now, sha);
    }
}
