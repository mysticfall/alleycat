using AlleyCat.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Installs explicit AI pipeline logging support for isolated integration tests that do not run under the Game scene.
/// </summary>
internal sealed class AIPipelineDebugLogFixture : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = new TestLoggerFactory();
    private bool _disposed;

    /// <summary>
    /// Installs the test logger override.
    /// </summary>
    public AIPipelineDebugLogFixture()
    {
        AIPipelineDebugLog.SetLoggerFactoryForTesting(_loggerFactory);
    }

    /// <summary>
    /// Clears the test logger override.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        AIPipelineDebugLog.SetLoggerFactoryForTesting(null);
        _loggerFactory.Dispose();
        _disposed = true;
    }

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        /// <inheritdoc />
        public void AddProvider(ILoggerProvider provider)
        {
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => TestLogger.Instance;

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    private sealed class TestLogger : ILogger
    {
        public static readonly TestLogger Instance = new();

        private TestLogger()
        {
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => false;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
