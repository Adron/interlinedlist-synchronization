using Microsoft.Extensions.Logging;

namespace InterlinedSync.Notifications;

/// <summary>
/// Fallback dispatcher used when the Windows App SDK runtime is unavailable
/// (e.g. when building or running tests on macOS / Linux). Writes the toast
/// payload to the logger so nothing is silently dropped.
/// </summary>
public sealed class LoggingNotificationDispatcher : INotificationDispatcher
{
    private readonly ILogger<LoggingNotificationDispatcher> _logger;

    public LoggingNotificationDispatcher(ILogger<LoggingNotificationDispatcher> logger)
    {
        _logger = logger;
    }

    public Task ShowAsync(NotificationPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        _logger.LogInformation(
            "Toast (logged only): {Title} | {Body} | actions={Actions}",
            payload.Title,
            payload.Body,
            string.Join(", ", payload.Actions.Select(a => a.Label)));

        return Task.CompletedTask;
    }
}
