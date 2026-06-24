using InterlinedSync.Storage;

namespace InterlinedSync.UI;

/// <summary>
/// Pure decision logic for whether the one-time "run at startup?" prompt
/// should be shown on this launch. Kept out of <see cref="ViewModels.StartupPromptViewModel"/>
/// and <see cref="Views.StartupPromptDialog"/> so it can be unit-tested without
/// spinning up WPF — and so the prompt rules stay in one place (SRP).
/// </summary>
/// <remarks>
/// The prompt is shown when ALL of the following hold:
/// <list type="number">
///   <item><description>The HKCU Run entry for the app does NOT already exist
///     (otherwise auto-start is already wired up).</description></item>
///   <item><description>The "don't ask again" suppressed flag is NOT set.</description></item>
///   <item><description>The current launch was triggered by the user from the
///     Start Menu (or any other interactive surface) rather than by the
///     installer's "Launch InterlinedList Sync after installation" checkbox.
///     The installer launch is identified by the
///     <c>--from-installer</c> command-line argument.</description></item>
/// </list>
/// A separate guard skips the prompt when <see cref="IAutoStartManager.IsManagedByInstallerAsync"/>
/// reports the Run entry was set by the installer (covered transitively
/// by condition #1 — the Run entry exists in that case).
/// </remarks>
public sealed class StartupPromptDecisionService
{
    /// <summary>
    /// CLI flag the installer passes when running the app via its
    /// "Launch InterlinedList Sync after installation" finish-page checkbox.
    /// Suppresses the prompt on that very first launch.
    /// </summary>
    public const string LaunchedByInstallerArg = "--from-installer";

    private readonly IAutoStartManager _autoStartManager;

    public StartupPromptDecisionService(IAutoStartManager autoStartManager)
    {
        _autoStartManager = autoStartManager;
    }

    /// <summary>
    /// Returns <c>true</c> when the prompt should be shown for this launch.
    /// </summary>
    public async Task<bool> ShouldPromptAsync(
        IReadOnlyList<string> launchArgs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchArgs);

        if (WasLaunchedByInstaller(launchArgs))
        {
            return false;
        }

        if (await _autoStartManager.IsStartupPromptSuppressedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (await _autoStartManager.IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return true;
    }

    private static bool WasLaunchedByInstaller(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], LaunchedByInstallerArg, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
