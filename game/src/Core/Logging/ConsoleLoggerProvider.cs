using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Writes Microsoft.Extensions.Logging entries to the Godot console.
/// </summary>
public sealed class ConsoleLoggerProvider : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new GodotConsoleLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class GodotConsoleLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = LoggerMessageFormatter.Format(categoryName, logLevel, eventId, formatter(state, exception), exception);
            switch (logLevel)
            {
                case LogLevel.Trace:
                case LogLevel.Debug:
                case LogLevel.Information:
                    GD.Print(message);
                    break;
                case LogLevel.Warning:
                    GD.PushWarning(message);
                    break;
                case LogLevel.Error:
                case LogLevel.Critical:
                    GD.PushError(message);
                    break;
                case LogLevel.None:
                    break;
                default:
                    GD.Print(message);
                    break;
            }
        }
    }
}
