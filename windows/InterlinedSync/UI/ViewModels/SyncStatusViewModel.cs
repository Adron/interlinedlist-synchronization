using CommunityToolkit.Mvvm.ComponentModel;
using InterlinedSync.Sync;

namespace InterlinedSync.UI.ViewModels;

/// <summary>
/// ViewModel exposing the current sync state to any UI surface (tray tooltip,
/// settings status row). Bridges between <see cref="ISyncStateNotifier"/> events
/// and observable properties.
/// </summary>
public sealed partial class SyncStatusViewModel : ObservableObject, IDisposable
{
    private readonly ISyncStateNotifier _notifier;

    [ObservableProperty]
    private SyncState _state;

    [ObservableProperty]
    private DateTimeOffset? _lastSyncedAt;

    [ObservableProperty]
    private int _documentCount;

    public SyncStatusViewModel(ISyncStateNotifier notifier)
    {
        _notifier = notifier;
        _state = notifier.Current;
        _notifier.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, SyncState state) => State = state;

    public void Dispose()
    {
        _notifier.StateChanged -= OnStateChanged;
    }
}
