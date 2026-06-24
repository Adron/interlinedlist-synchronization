namespace InterlinedSync.Storage;

/// <summary>
/// Thin abstraction over the Windows registry surface used by
/// <see cref="RegistryAutoStartManager"/>. Exists purely so the manager can be
/// unit-tested without writing to HKCU.
/// </summary>
public interface IAutoStartRegistryGateway
{
    /// <summary>
    /// Reads a value from the per-user
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key.
    /// </summary>
    string? ReadValue(string name);

    /// <summary>
    /// Writes a value to the per-user Run key.
    /// </summary>
    void WriteValue(string name, string value);

    /// <summary>
    /// Deletes a value from the per-user Run key.
    /// </summary>
    void DeleteValue(string name);

    /// <summary>
    /// Reads an integer flag from <c>HKCU\Software\InterlinedSync</c>.
    /// Returns <c>null</c> when the value is absent. Used by the startup
    /// prompt's "don't ask again" affordance — kept separate from the Run
    /// key so we never trip over Microsoft's autostart contract.
    /// </summary>
    int? ReadPreferenceFlag(string name);

    /// <summary>
    /// Writes an integer flag to <c>HKCU\Software\InterlinedSync</c>.
    /// </summary>
    void WritePreferenceFlag(string name, int value);
}
