using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Posts high-severity log entries to the in-game notification UI when it is available.
/// </summary>
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

            if (!IsEnabled(logLevel) || _isPosting)
            {
                return;
            }

            _isPosting = true;
            try
            {
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
