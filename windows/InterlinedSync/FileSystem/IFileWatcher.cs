using System.Threading.Channels;

namespace InterlinedSync.FileSystem;

/// <summary>
/// Watches a single folder for changes to <c>*.md</c> files and surfaces them as
/// a debounced stream of <see cref="LocalChange"/>s. The interface is intentionally
/// narrow so tests can substitute a synthetic watcher that pushes events on demand.
/// </summary>
public interface IFileWatcher : IAsyncDisposable
{
    /// <summary>
    /// Reader over the coalesced change stream. Producers complete the channel
    /// when <see cref="StopAsync"/> is called or the watcher is disposed.
    /// </summary>
    ChannelReader<LocalChange> Changes { get; }

    /// <summary>
    /// Starts watching <paramref name="folder"/>. Creates the directory if missing.
    /// Subsequent calls with a different folder restart the watcher.
    /// </summary>
    Task StartAsync(string folder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the watcher. Buffered events that have already been published remain
    /// readable from <see cref="Changes"/>.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
