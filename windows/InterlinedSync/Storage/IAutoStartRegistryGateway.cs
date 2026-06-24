namespace InterlinedSync.Storage;

/// <summary>
/// Thin abstraction over the Windows registry surface used by
/// <see cref="RegistryAutoStartManager"/>. Exists purely so the manager can be
/// unit-tested without writing to HKCU.
/// </summary>
public interface IAutoStartRegistryGateway
{
    string? ReadValue(string name);
    void WriteValue(string name, string value);
    void DeleteValue(string name);
}
