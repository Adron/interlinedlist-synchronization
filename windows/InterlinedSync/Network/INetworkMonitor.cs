namespace InterlinedSync.Network;

/// <summary>
/// Reports the current network connectivity state and raises events on changes.
/// Implementations wrap platform-specific APIs (Windows.Networking.Connectivity
/// on Windows; a stub on dev hosts and in tests).
/// </summary>
public interface INetworkMonitor : IDisposable
{
    bool IsOnline { get; }

    event EventHandler<bool>? ConnectivityChanged;

    void Start();

    void Stop();
}
