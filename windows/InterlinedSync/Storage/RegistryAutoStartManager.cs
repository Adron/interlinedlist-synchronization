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
    internal const string StartupPromptSuppressedFlag = "StartupPromptSuppressed";

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

    public Task<bool> IsManagedByInstallerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var value = _gateway.ReadValue(ValueName);
            if (string.IsNullOrEmpty(value))
            {
                return Task.FromResult(false);
            }

            // The value is stored as a quoted path; strip enclosing quotes
            // before matching against the Program Files install location.
            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            {
                trimmed = trimmed[1..^1];
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            var underInstallRoot =
                (!string.IsNullOrEmpty(programFiles) && trimmed.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(programFilesX86) && trimmed.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(underInstallRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not classify auto-start registry value origin.");
            return Task.FromResult(false);
        }
    }

    public Task<bool> IsStartupPromptSuppressedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var flag = _gateway.ReadPreferenceFlag(StartupPromptSuppressedFlag);
            return Task.FromResult(flag is > 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read startup-prompt-suppressed flag.");
            return Task.FromResult(false);
        }
    }

    public Task SetStartupPromptSuppressedAsync(bool suppressed, CancellationToken cancellationToken = default)
    {
        try
        {
            _gateway.WritePreferenceFlag(StartupPromptSuppressedFlag, suppressed ? 1 : 0);
            _logger.LogInformation("Startup prompt suppressed flag set to {Value}.", suppressed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist startup-prompt-suppressed flag.");
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
