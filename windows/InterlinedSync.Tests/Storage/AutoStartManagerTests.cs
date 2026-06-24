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
    public async Task InMemoryAutoStartManager_RoundTripsSuppressedFlag()
    {
        var manager = new InMemoryAutoStartManager();

        (await manager.IsStartupPromptSuppressedAsync()).Should().BeFalse();

        await manager.SetStartupPromptSuppressedAsync(true);
        (await manager.IsStartupPromptSuppressedAsync()).Should().BeTrue();

        await manager.SetStartupPromptSuppressedAsync(false);
        (await manager.IsStartupPromptSuppressedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryAutoStartManager_IsManagedByInstaller_FalseByDefault()
    {
        var manager = new InMemoryAutoStartManager();
        (await manager.IsManagedByInstallerAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryAutoStartManager_IsManagedByInstaller_TrueOnlyWhenEnabledAndStaged()
    {
        var manager = new InMemoryAutoStartManager();
        manager.SetManagedByInstaller(true);

        // Even when staged, must also be enabled to count as installer-managed.
        (await manager.IsManagedByInstallerAsync()).Should().BeFalse();

        await manager.SetEnabledAsync(true);
        (await manager.IsManagedByInstallerAsync()).Should().BeTrue();
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

    [Fact]
    public async Task RegistryAutoStartManager_SuppressedFlag_RoundTrips()
    {
        var gateway = new FakeRegistryGateway();
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => @"C:\app.exe");

        (await manager.IsStartupPromptSuppressedAsync()).Should().BeFalse();

        await manager.SetStartupPromptSuppressedAsync(true);
        (await manager.IsStartupPromptSuppressedAsync()).Should().BeTrue();
        gateway.PreferenceFlags[RegistryAutoStartManager.StartupPromptSuppressedFlag].Should().Be(1);

        await manager.SetStartupPromptSuppressedAsync(false);
        (await manager.IsStartupPromptSuppressedAsync()).Should().BeFalse();
        gateway.PreferenceFlags[RegistryAutoStartManager.StartupPromptSuppressedFlag].Should().Be(0);
    }

    [Fact]
    public async Task RegistryAutoStartManager_IsManagedByInstaller_FalseWhenValueAbsent()
    {
        var gateway = new FakeRegistryGateway();
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => @"C:\Program Files\InterlinedSync\InterlinedSync.exe");

        (await manager.IsManagedByInstallerAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RegistryAutoStartManager_IsManagedByInstaller_TrueWhenUnderProgramFiles()
    {
        var gateway = new FakeRegistryGateway();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        // Guard the test against hosts where ProgramFiles is empty (rare but
        // possible on some macOS / Linux CI runners). When unavailable we
        // can only assert the negative branch, which is covered above.
        if (string.IsNullOrEmpty(programFiles))
        {
            return;
        }
        var installedPath = Path.Combine(programFiles, "InterlinedList Sync", "InterlinedSync.exe");
        gateway.Values[RegistryAutoStartManager.ValueName] = $"\"{installedPath}\"";

        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => installedPath);

        (await manager.IsManagedByInstallerAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task RegistryAutoStartManager_IsManagedByInstaller_FalseWhenOutsideProgramFiles()
    {
        var gateway = new FakeRegistryGateway();
        gateway.Values[RegistryAutoStartManager.ValueName] = "\"C:\\Users\\bob\\Desktop\\InterlinedSync.exe\"";
        var manager = new RegistryAutoStartManager(gateway, NullLogger<RegistryAutoStartManager>.Instance,
            () => @"C:\Users\bob\Desktop\InterlinedSync.exe");

        (await manager.IsManagedByInstallerAsync()).Should().BeFalse();
    }

    private sealed class FakeRegistryGateway : IAutoStartRegistryGateway
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> PreferenceFlags { get; } = new(StringComparer.Ordinal);
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

        public int? ReadPreferenceFlag(string name)
            => PreferenceFlags.TryGetValue(name, out var v) ? v : null;

        public void WritePreferenceFlag(string name, int value)
            => PreferenceFlags[name] = value;
    }
}
