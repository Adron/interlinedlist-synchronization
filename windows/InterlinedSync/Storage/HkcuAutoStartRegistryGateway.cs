#if WINDOWS_BUILD
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace InterlinedSync.Storage;

/// <summary>
/// Production HKCU\Software\Microsoft\Windows\CurrentVersion\Run gateway.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HkcuAutoStartRegistryGateway : IAutoStartRegistryGateway
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? ReadValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void WriteValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open HKCU Run key.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
#else
namespace InterlinedSync.Storage;

/// <summary>
/// Non-Windows stub so the type resolves at compile time on macOS/Linux for the
/// shared test build. Calls throw; the DI graph wires the in-memory manager
/// instead on those hosts so this is never hit at runtime.
/// </summary>
internal sealed class HkcuAutoStartRegistryGateway : IAutoStartRegistryGateway
{
    public string? ReadValue(string name) => null;
    public void WriteValue(string name, string value) { }
    public void DeleteValue(string name) { }
}
#endif
