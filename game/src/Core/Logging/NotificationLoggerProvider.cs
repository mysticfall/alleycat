using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Posts high-severity log entries — and notification-eligible entry states — to the in-game notification UI when it
/// is available.
/// </summary>
/// <remarks>
/// There is no separate notification switch: pipeline diagnostics emit at trace level, so the configured level of
/// their log category acts as the single universal switch. The framework's category-level filter runs before entries
/// reach this provider, which means an entry-carrying diagnostic arriving here has already been opted in by
/// configuration, while the shipped information default keeps such entries filtered out entirely.
/// </remarks>
public sealed class NotificationLoggerProvider(
    ILogNotificationSink notificationSink,
    LogLevel minimumLevel = LogLevel.Error) : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => new GodotNotificationLogger(categoryName, notificationSink, minimumLevel);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class GodotNotificationLogger(
        string categoryName,
        ILogNotificationSink notificationSink,
        LogLevel minimumLevel) : ILogger
    {
        private bool _isPosting;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel is not LogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            // The minimum-level short-circuit relaxes only for notification-eligible entries, which arrive below the
            // error floor because pipeline diagnostics emit at trace level; ordinary entries below the minimum level
            // stay filtered.
            var notificationEntry = state as IUINotificationEntry;
            bool postNotificationEntry = logLevel is not LogLevel.None
                && notificationEntry is not null;

            if ((!postNotificationEntry && !IsEnabled(logLevel)) || _isPosting)
            {
                return;
            }

            _isPosting = true;
            try
            {
                if (postNotificationEntry)
                {
                    _ = notificationSink.TryPostNotification(
                        notificationEntry!.ToNotificationText(),
                        notificationEntry.NotificationTimeoutSeconds);
                    return;
                }

                string message = LoggerMessageFormatter.Format(
                    categoryName,
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    exception,
                    includeException: false);
                _ = notificationSink.TryPostNotification(message);
            }
            finally
            {
                _isPosting = false;
            }
        }
    }
}
