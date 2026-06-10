using Microsoft.Extensions.Logging;

namespace InterlinedSync.Sync;

/// <summary>
/// Default <see cref="ISyncStateNotifier"/> backed by a single field guarded by a
/// <see cref="SemaphoreSlim"/>. Event delivery is best-effort: subscriber failures
/// are logged but never bubble back into the engine.
/// </summary>
public sealed class SyncStateNotifier : ISyncStateNotifier
{
    private readonly ILogger<SyncStateNotifier> _logger;
    private readonly Lock _gate = new();
    private SyncState _current = SyncState.SignedOut;

    public SyncStateNotifier(ILogger<SyncStateNotifier> logger)
    {
        _logger = logger;
    }

    public SyncState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<SyncState>? StateChanged;

    public void SetState(SyncState state)
    {
        SyncState previous;
        lock (_gate)
        {
            if (_current == state)
            {
                return;
            }
            previous = _current;
            _current = state;
        }

        _logger.LogInformation("Sync state transition: {Previous} -> {Current}.", previous, state);

        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<SyncState>>())
        {
            try
            {
                subscriber(this, state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync state subscriber threw.");
            }
        }
    }
}
