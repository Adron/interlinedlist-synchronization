using System.IO;
using FluentAssertions;
using InterlinedSync.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.FileSystem;

public sealed class FileSystemWatcherServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    // The OS may take a moment to dispatch FileSystemWatcher events; allow well past debounce.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private string _folder = string.Empty;
    private FileSystemWatcherService _watcher = null!;

    public Task InitializeAsync()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"watcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
        _watcher = new FileSystemWatcherService(
            NullLogger<FileSystemWatcherService>.Instance,
            TimeProvider.System,
            Debounce);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _watcher.DisposeAsync();
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task Emits_CreatedEvent_ForNewMarkdownFile()
    {
        await _watcher.StartAsync(_folder);
        var path = Path.Combine(_folder, "note.md");

        await File.WriteAllTextAsync(path, "hello");

        var change = await ReadOneAsync();
        change.Should().NotBeNull();
        change!.Path.Should().Be(path);
        change.Kind.Should().BeOneOf(LocalChangeKind.Created, LocalChangeKind.Modified);
    }

    [Fact]
    public async Task IgnoresNonMarkdownFiles()
    {
        await _watcher.StartAsync(_folder);

        await File.WriteAllTextAsync(Path.Combine(_folder, "ignore.txt"), "txt");
        await File.WriteAllTextAsync(Path.Combine(_folder, "note.log"), "log");

        // Wait past the debounce window — nothing should arrive.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var act = async () => await _watcher.Changes.ReadAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CoalescesRapidWrites()
    {
        await _watcher.StartAsync(_folder);
        var path = Path.Combine(_folder, "burst.md");

        for (var i = 0; i < 10; i++)
        {
            await File.WriteAllTextAsync(path, $"iteration {i}");
        }

        var first = await ReadOneAsync();
        first.Should().NotBeNull();
        first!.Path.Should().Be(path);

        // After the first event drains, no follow-up burst should be pending.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var act = async () => await _watcher.Changes.ReadAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EmitsDeletedEvent()
    {
        var path = Path.Combine(_folder, "doomed.md");
        await File.WriteAllTextAsync(path, "x");

        await _watcher.StartAsync(_folder);

        File.Delete(path);

        var change = await ReadKindAsync(LocalChangeKind.Deleted);
        change.Should().NotBeNull();
        change!.Path.Should().Be(path);
    }

    [Fact]
    public async Task EmitsRenamedEvent_WhenBothPathsAreMarkdown()
    {
        var oldPath = Path.Combine(_folder, "old.md");
        var newPath = Path.Combine(_folder, "new.md");
        await File.WriteAllTextAsync(oldPath, "x");

        await _watcher.StartAsync(_folder);
        File.Move(oldPath, newPath);

        var change = await ReadKindAsync(LocalChangeKind.Renamed);
        change.Should().NotBeNull();
        change!.Path.Should().Be(newPath);
        change.OldPath.Should().Be(oldPath);
    }

    [Fact]
    public async Task StopAsync_HaltsEventDelivery()
    {
        await _watcher.StartAsync(_folder);
        await _watcher.StopAsync();

        await File.WriteAllTextAsync(Path.Combine(_folder, "after-stop.md"), "x");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var act = async () => await _watcher.Changes.ReadAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<LocalChange?> ReadOneAsync()
    {
        using var cts = new CancellationTokenSource(ReadTimeout);
        try
        {
            return await _watcher.Changes.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<LocalChange?> ReadKindAsync(LocalChangeKind kind)
    {
        using var cts = new CancellationTokenSource(ReadTimeout);
        try
        {
            while (await _watcher.Changes.WaitToReadAsync(cts.Token))
            {
                while (_watcher.Changes.TryRead(out var change))
                {
                    if (change.Kind == kind)
                    {
                        return change;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return null;
    }
}
