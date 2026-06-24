using FluentAssertions;
using InterlinedSync.Network.Mocks;
using Xunit;

namespace InterlinedSync.Tests.Network;

public sealed class NetworkMonitorTests
{
    [Fact]
    public void StubNetworkMonitor_DefaultsOnline()
    {
        using var monitor = new StubNetworkMonitor();

        monitor.IsOnline.Should().BeTrue();
    }

    [Fact]
    public void Start_TogglesStartedFlag()
    {
        using var monitor = new StubNetworkMonitor();

        monitor.Start();
        monitor.Started.Should().BeTrue();

        monitor.Stop();
        monitor.Started.Should().BeFalse();
    }

    [Fact]
    public void SetOnline_FiresConnectivityChanged_OnTransition()
    {
        using var monitor = new StubNetworkMonitor();
        var events = new List<bool>();
        monitor.ConnectivityChanged += (_, isOnline) => events.Add(isOnline);

        monitor.SetOnline(false);
        monitor.SetOnline(true);

        events.Should().Equal(false, true);
    }

    [Fact]
    public void SetOnline_DoesNotFire_WhenStateUnchanged()
    {
        using var monitor = new StubNetworkMonitor();
        var fired = 0;
        monitor.ConnectivityChanged += (_, _) => fired++;

        monitor.SetOnline(true); // already online
        monitor.SetOnline(true);

        fired.Should().Be(0);
    }

    [Fact]
    public void Dispose_StopsMonitor()
    {
        var monitor = new StubNetworkMonitor();
        monitor.Start();

        monitor.Dispose();

        monitor.Started.Should().BeFalse();
    }
}
