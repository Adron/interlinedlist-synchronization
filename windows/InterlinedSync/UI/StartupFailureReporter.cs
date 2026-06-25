using System.IO;
using System.Text;
using InterlinedSync.Configuration;

namespace InterlinedSync.UI;

/// <summary>
/// Pure formatting helper for surfacing a fatal startup error to the user.
/// Kept out of <c>App.xaml.cs</c> so it can be unit-tested without WPF and so
/// the message format stays consistent across every error-handling entry
/// point (Dispatcher, AppDomain, TaskScheduler, and the OnStartup try/catch).
/// </summary>
/// <remarks>
/// The class deliberately does NOT call <c>MessageBox.Show</c> itself — the
/// WPF interop is hard to test. <see cref="FormatMessage"/> returns the text
/// the caller hands to MessageBox; <see cref="GetDefaultLogDirectory"/>
/// returns the path we point the user at so they can attach logs to a bug
/// report.
/// </remarks>
public static class StartupFailureReporter
{
    /// <summary>
    /// MessageBox title shown for every startup-failure surface.
    /// </summary>
    public const string DialogTitle = "InterlinedList Sync — startup error";

    /// <summary>
    /// Builds the user-facing error text. Always includes the failing stage
    /// (so a user can tell a host-start failure from a DI failure), the
    /// exception type + message, and the log directory so support can pull
    /// the rolling Serilog files. Inner exceptions are walked once to surface
    /// the typical wrapper -> root-cause pair without flooding the dialog.
    /// </summary>
    /// <param name="stage">Short label describing what was happening when the
    /// exception was raised (e.g. <c>"building host"</c>, <c>"Dispatcher"</c>).
    /// Used verbatim in the first sentence of the message.</param>
    /// <param name="exception">The exception to format. <see langword="null"/>
    /// is tolerated and produces a generic "unknown error" message rather
    /// than throwing — this method is on the failure path and must never
    /// itself throw.</param>
    /// <param name="logDirectory">Directory where Serilog writes rolling
    /// files. Shown to the user verbatim so they can open it from File
    /// Explorer. Pass the result of <see cref="GetDefaultLogDirectory"/> if
    /// you don't have one handy.</param>
    public static string FormatMessage(string stage, Exception? exception, string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        var sb = new StringBuilder();
        sb.Append("InterlinedList Sync could not start (")
          .Append(stage)
          .AppendLine(").")
          .AppendLine();

        if (exception is null)
        {
            sb.AppendLine("An unknown error occurred.");
        }
        else
        {
            sb.Append(exception.GetType().FullName)
              .Append(": ")
              .AppendLine(exception.Message);

            if (exception.InnerException is { } inner)
            {
                sb.AppendLine()
                  .Append("Caused by: ")
                  .Append(inner.GetType().FullName)
                  .Append(": ")
                  .AppendLine(inner.Message);
            }
        }

        sb.AppendLine()
          .AppendLine("Log files:")
          .AppendLine(logDirectory);

        return sb.ToString();
    }

    /// <summary>
    /// Resolves <c>%LOCALAPPDATA%\interlinedlist-sync\logs</c> — the same path
    /// the Serilog configuration in <see cref="Program"/> writes to. Centralized
    /// here so the failure dialog and the actual sink can never drift.
    /// </summary>
    public static string GetDefaultLogDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.AppDataFolderName,
            AppConstants.LogsFolderName);
}
