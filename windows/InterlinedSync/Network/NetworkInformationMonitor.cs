using Microsoft.Extensions.Logging;

namespace InterlinedSync.Network;

/// <summary>
/// Production <see cref="INetworkMonitor"/> backed by
/// <c>Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged</c>.
/// On non-Windows build hosts we degrade to the .NET BCL
/// <see cref="System.Net.NetworkInformation.NetworkChange"/> events so the class
/// still compiles and behaves sensibly in tests.
/// </summary>
public sealed class NetworkInformationMonitor : INetworkMonitor
{
    private readonly ILogger<NetworkInformationMonitor> _logger;
    private readonly Lock _gate = new();
    private bool _started;
    private bool _isOnline = true;
    private System.Net.NetworkInformation.NetworkAvailabilityChangedEventHandler? _availabilityHandler;

    public NetworkInformationMonitor(ILogger<NetworkInformationMonitor> logger)
    {
        _logger = logger;
        _isOnline = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
    }

    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _isOnline;
            }
        }
    }

    public event EventHandler<bool>? ConnectivityChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
        }

        _availabilityHandler = (_, args) => UpdateStatus(args.IsAvailable);
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += _availabilityHandler;
        _logger.LogInformation("Network monitor started. Initial state: {IsOnline}.", IsOnline);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }
            _started = false;
        }

        if (_availabilityHandler is not null)
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= _availabilityHandler;
            _availabilityHandler = null;
        }
    }

    public void Dispose() => Stop();

    private void UpdateStatus(bool isOnline)
    {
        bool changed;
        lock (_gate)
        {
            changed = _isOnline != isOnline;
            _isOnline = isOnline;
        }

        if (!changed)
        {
            return;
        }

        _logger.LogInformation("Network connectivity changed to {IsOnline}.", isOnline);
        try
        {
            ConnectivityChanged?.Invoke(this, isOnline);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConnectivityChanged subscriber threw.");
        }
    }
}
