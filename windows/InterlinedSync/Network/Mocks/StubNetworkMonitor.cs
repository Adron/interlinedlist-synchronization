namespace InterlinedSync.Network.Mocks;

/// <summary>
/// Deterministic <see cref="INetworkMonitor"/> for tests. Lets the test author
/// flip connectivity state synchronously and verify subscribers reacted.
/// </summary>
public sealed class StubNetworkMonitor : INetworkMonitor
{
    private bool _isOnline = true;

    public bool IsOnline => _isOnline;

    public bool Started { get; private set; }

    public event EventHandler<bool>? ConnectivityChanged;

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public void Dispose() => Stop();

    public void SetOnline(bool isOnline)
    {
        if (_isOnline == isOnline)
        {
            return;
        }
        _isOnline = isOnline;
        ConnectivityChanged?.Invoke(this, isOnline);
    }
}
