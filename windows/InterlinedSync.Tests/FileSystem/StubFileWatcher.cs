using System.Threading.Channels;
using InterlinedSync.FileSystem;

namespace InterlinedSync.Tests.FileSystem;

/// <summary>
/// Hand-rolled <see cref="IFileWatcher"/> for tests. Lets the test push synthetic
/// events through the channel and inspect Start/Stop calls without spinning up a
/// real <see cref="System.IO.FileSystemWatcher"/>.
/// </summary>
internal sealed class StubFileWatcher : IFileWatcher
{
    private readonly Channel<LocalChange> _channel = Channel.CreateUnbounded<LocalChange>();

    public ChannelReader<LocalChange> Changes => _channel.Reader;

    public string? StartedFolder { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public Task StartAsync(string folder, CancellationToken cancellationToken = default)
    {
        StartedFolder = folder;
        StartCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public ValueTask Publish(LocalChange change) => _channel.Writer.WriteAsync(change);

    public void Complete() => _channel.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
