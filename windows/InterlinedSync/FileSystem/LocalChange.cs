namespace InterlinedSync.FileSystem;

/// <summary>
/// Classification of a local file event coalesced by the watcher debounce window.
/// </summary>
public enum LocalChangeKind
{
    Created,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>
/// A debounced local file event surfaced by <see cref="IFileWatcher"/>.
/// For <see cref="LocalChangeKind.Renamed"/>, <see cref="OldPath"/> holds the
/// pre-rename path and <see cref="Path"/> holds the new path. All other kinds
/// have <see cref="OldPath"/> set to <c>null</c>.
/// </summary>
public sealed record LocalChange(LocalChangeKind Kind, string Path, string? OldPath = null);
