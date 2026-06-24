using FluentAssertions;
using InterlinedSync.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InterlinedSync.Tests.Storage;

public class AutoStartManagerTests
{
    [Fact]
    public async Task InMemoryAutoStartManager_RoundTripsState()
    {
        var manager = new InMemoryAutoStartManager();

        (await manager.IsEnabledAsync()).Should().BeFalse();

        await manager.SetEnabledAsync(true);
        (await manager.IsEnabledAsync()).Should().BeTrue();

        await manager.SetEnabledAsync(false);
        (await manager.IsEnabledAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RegistryAutoStartManager_SetEnabledTrue_WritesQuotedPath()
    {
        var gateway = new FakeRegistryGateway();
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => @"C:\Program Files\InterlinedSync\InterlinedSync.exe");

        await manager.SetEnabledAsync(true);

        gateway.Values.Should().ContainKey(RegistryAutoStartManager.ValueName);
        gateway.Values[RegistryAutoStartManager.ValueName]
            .Should().Be("\"C:\\Program Files\\InterlinedSync\\InterlinedSync.exe\"");
    }

    [Fact]
    public async Task RegistryAutoStartManager_SetEnabledFalse_DeletesValue()
    {
        var gateway = new FakeRegistryGateway();
        gateway.Values[RegistryAutoStartManager.ValueName] = "\"C:\\app.exe\"";
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => @"C:\app.exe");

        await manager.SetEnabledAsync(false);

        gateway.Values.Should().NotContainKey(RegistryAutoStartManager.ValueName);
    }

    [Fact]
    public async Task RegistryAutoStartManager_IsEnabled_ReflectsRegistryState()
    {
        var gateway = new FakeRegistryGateway();
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => @"C:\app.exe");

        (await manager.IsEnabledAsync()).Should().BeFalse();

        gateway.Values[RegistryAutoStartManager.ValueName] = "\"C:\\app.exe\"";
        (await manager.IsEnabledAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task RegistryAutoStartManager_SetEnabledTrue_SkipsWriteWhenPathMissing()
    {
        var gateway = new FakeRegistryGateway();
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => null);

        await manager.SetEnabledAsync(true);

        gateway.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task RegistryAutoStartManager_SetEnabledTrue_SwallowsGatewayException()
    {
        var gateway = new FakeRegistryGateway { ThrowOnWrite = true };
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => @"C:\app.exe");

        Func<Task> act = () => manager.SetEnabledAsync(true);

        await act.Should().NotThrowAsync();
    }

    private sealed class FakeRegistryGateway : IAutoStartRegistryGateway
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public bool ThrowOnWrite { get; set; }

        public string? ReadValue(string name)
            => Values.TryGetValue(name, out var v) ? v : null;

        public void WriteValue(string name, string value)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("denied");
            }
            Values[name] = value;
        }

        public void DeleteValue(string name) => Values.Remove(name);
    }
}
