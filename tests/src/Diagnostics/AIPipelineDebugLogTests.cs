using AlleyCat.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Diagnostics;

/// <summary>
/// Unit coverage for AI pipeline diagnostics routed through Microsoft.Extensions.Logging.
/// </summary>
public sealed class AIPipelineDebugLogTests : IDisposable
{
    private readonly CapturingLoggerProvider _provider = new();
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Installs a capturing logger factory for each test.
    /// </summary>
    public AIPipelineDebugLogTests()
    {
        _loggerFactory = new CapturingLoggerFactory(_provider);
        AIPipelineDebugLog.SetLoggerFactoryForTesting(_loggerFactory);
    }

    /// <summary>
    /// Stage diagnostics are emitted as debug log entries.
    /// </summary>
    [Fact]
    public void Stage_RoutesStructuredDebugLog()
    {
        AIPipelineDebugLog.Stage("LLM observation received", "42 chars");

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("AlleyCat.AIPipeline", entry.CategoryName);
        Assert.Contains("LLM observation received", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Latency diagnostics are emitted as debug log entries.
    /// </summary>
    [Fact]
    public void Latency_RoutesStructuredDebugLog()
    {
        AIPipelineDebugLog.Latency("TTS backend returned in", TimeSpan.FromMilliseconds(12.34), "model tts-1");

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("TTS backend returned in", entry.Message, StringComparison.Ordinal);
        Assert.Contains("12.34", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Production diagnostics fail clearly instead of suppressing missing logging infrastructure.
    /// </summary>
    [Fact]
    public void IsEnabled_WithoutLoggerOverrideOrGameInfrastructure_ThrowsInvalidOperationException()
    {
        AIPipelineDebugLog.SetLoggerFactoryForTesting(null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => _ = AIPipelineDebugLog.IsEnabled);
        Assert.Contains("Game singleton", exception.Message, StringComparison.Ordinal);

        AIPipelineDebugLog.SetLoggerFactoryForTesting(_loggerFactory);
    }

    /// <summary>
    /// Clears the test logger override.
    /// </summary>
    public void Dispose()
    {
        AIPipelineDebugLog.SetLoggerFactoryForTesting(null);
        _loggerFactory.Dispose();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLoggerFactory(CapturingLoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string categoryName, List<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception)));
    }

    private sealed record CapturedLogEntry(string CategoryName, LogLevel Level, string Message);
}
