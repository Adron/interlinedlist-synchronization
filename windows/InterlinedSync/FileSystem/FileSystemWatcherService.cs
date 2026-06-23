using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.FileSystem;

/// <summary>
/// Default <see cref="IFileWatcher"/> backed by <see cref="FileSystemWatcher"/>.
/// Coalesces rapid bursts of events (text editors often write a temp file,
/// rename, then touch the original) into a single <see cref="LocalChange"/>
/// per path using a configurable debounce window.
/// </summary>
public sealed class FileSystemWatcherService : IFileWatcher
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);
    private const string MarkdownFilter = "*.md";
    private const string MarkdownExtension = ".md";

    private readonly ILogger<FileSystemWatcherService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly Channel<LocalChange> _channel;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LocalChange> _pending = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private ITimer? _flushTimer;
    private string? _folder;
    private bool _disposed;

    public FileSystemWatcherService(ILogger<FileSystemWatcherService> logger)
        : this(logger, TimeProvider.System, DefaultDebounce)
    {
    }

    public FileSystemWatcherService(ILogger<FileSystemWatcherService> logger, TimeProvider timeProvider, TimeSpan debounce)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _debounce = debounce > TimeSpan.Zero ? debounce : DefaultDebounce;
        _channel = Channel.CreateUnbounded<LocalChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<LocalChange> Changes => _channel.Reader;

    public Task StartAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(folder);

        lock (_gate)
        {
            if (_watcher is not null && string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            DisposeWatcherLocked();

            var watcher = new FileSystemWatcher(folder, MarkdownFilter)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 65536,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _folder = folder;
            _flushTimer = _timeProvider.CreateTimer(_ => Flush(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            _logger.LogInformation("FileSystemWatcher started for {Folder}.", folder);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            DisposeWatcherLocked();
            _pending.Clear();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Queue(new LocalChange(LocalChangeKind.Created, e.FullPath));
    private void OnChanged(object sender, FileSystemEventArgs e) => Queue(new LocalChange(LocalChangeKind.Modified, e.FullPath));
    private void OnDeleted(object sender, FileSystemEventArgs e) => Queue(new LocalChange(LocalChangeKind.Deleted, e.FullPath));

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var fromMd = IsMarkdown(e.OldFullPath);
        var toMd = IsMarkdown(e.FullPath);

        if (fromMd && toMd)
        {
            Queue(new LocalChange(LocalChangeKind.Renamed, e.FullPath, e.OldFullPath));
        }
        else if (fromMd)
        {
            Queue(new LocalChange(LocalChangeKind.Deleted, e.OldFullPath));
        }
        else if (toMd)
        {
            Queue(new LocalChange(LocalChangeKind.Created, e.FullPath));
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher reported an internal error.");
    }

    private static bool IsMarkdown(string path) =>
        path.EndsWith(MarkdownExtension, StringComparison.OrdinalIgnoreCase);

    private void Queue(LocalChange change)
    {
        if (!IsMarkdown(change.Path))
        {
            return;
        }

        lock (_gate)
        {
            if (_watcher is null)
            {
                return;
            }

            if (_pending.TryGetValue(change.Path, out var existing))
            {
                _pending[change.Path] = Merge(existing, change);
            }
            else
            {
                _pending[change.Path] = change;
            }

            _flushTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private static LocalChange Merge(LocalChange first, LocalChange next)
    {
        // Created then Modified collapses to Created (still a new doc).
        // Created then Deleted collapses to Deleted (Flush drops the pair).
        // Anything-then-Renamed wins (carries old path info).
        if (next.Kind == LocalChangeKind.Renamed) return next;
        if (first.Kind == LocalChangeKind.Created && next.Kind == LocalChangeKind.Modified) return first;
        return next with { OldPath = first.OldPath ?? next.OldPath };
    }

    private void Flush()
    {
        List<LocalChange> toEmit;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            toEmit = new List<LocalChange>(_pending.Values);
            _pending.Clear();
        }

        foreach (var change in toEmit)
        {
            if (!_channel.Writer.TryWrite(change))
            {
                _logger.LogWarning("Could not enqueue local change for {Path}.", change.Path);
            }
        }
    }

    private void DisposeWatcherLocked()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreated;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_flushTimer is not null)
        {
            _flushTimer.Dispose();
            _flushTimer = null;
        }
        _folder = null;
    }
}
