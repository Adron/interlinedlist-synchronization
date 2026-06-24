using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace InterlinedSync.Storage;

/// <summary>
/// Per-user auto-start backed by the HKCU Run key. We intentionally avoid
/// HKLM (no UAC) and the Task Scheduler bridge (which is only available
/// inside the MSIX <c>startupTask</c> manifest declaration). The MSIX build
/// uses the manifest extension; the unpackaged (.exe) build uses this class.
/// </summary>
/// <remarks>
/// The actual registry I/O is delegated to <see cref="IAutoStartRegistryGateway"/>
/// so this class is testable without touching HKCU.
/// </remarks>
public sealed class RegistryAutoStartManager : IAutoStartManager
{
    internal const string ValueName = "InterlinedSync";

    private readonly IAutoStartRegistryGateway _gateway;
    private readonly ILogger<RegistryAutoStartManager> _logger;
    private readonly Func<string?> _executablePathProvider;

    public RegistryAutoStartManager(ILogger<RegistryAutoStartManager> logger)
        : this(new HkcuAutoStartRegistryGateway(), logger, DefaultExecutablePath)
    {
    }

    internal RegistryAutoStartManager(
        IAutoStartRegistryGateway gateway,
        ILogger<RegistryAutoStartManager> logger,
        Func<string?> executablePathProvider)
    {
        _gateway = gateway;
        _logger = logger;
        _executablePathProvider = executablePathProvider;
    }

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var value = _gateway.ReadValue(ValueName);
            return Task.FromResult(!string.IsNullOrEmpty(value));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read auto-start registry value.");
            return Task.FromResult(false);
        }
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            if (enabled)
            {
                var path = _executablePathProvider();
                if (string.IsNullOrEmpty(path))
                {
                    _logger.LogWarning("Auto-start requested but executable path could not be resolved.");
                    return Task.CompletedTask;
                }

                _gateway.WriteValue(ValueName, $"\"{path}\"");
                _logger.LogInformation("Auto-start enabled at {Path}.", path);
            }
            else
            {
                _gateway.DeleteValue(ValueName);
                _logger.LogInformation("Auto-start disabled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write auto-start registry value.");
        }
        return Task.CompletedTask;
    }

    private static string? DefaultExecutablePath()
    {
        var process = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(process))
        {
            return process;
        }
        return Assembly.GetEntryAssembly()?.Location;
    }
}
