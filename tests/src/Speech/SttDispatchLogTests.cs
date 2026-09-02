using AlleyCat.Core.Logging;
using AlleyCat.Speech.Transcription;
using AlleyCat.Tests.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the speech-owned STT dispatch marker and its dedicated pipeline child category (SPCH-003).
/// </summary>
[Collection(PipelineDiagnosticsCollection.Name)]
public sealed class SttDispatchLogTests : IDisposable
{
    private const string SttCategoryName = "AlleyCat.Pipeline.STT";

    private readonly CapturingLogProvider _provider = new();
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Installs a capturing logger factory for each test.
    /// </summary>
    public SttDispatchLogTests()
    {
        _loggerFactory = new CapturingLoggerFactory(_provider);
        PipelineDebugLog.SetLoggerFactoryForTesting(_loggerFactory);
    }

    /// <summary>
    /// The STT dispatch marker emits one debug-level entry under the dedicated STT child category with
    /// notification-eligible entry state, leaving the trace-level pipeline markers on the parent category.
    /// </summary>
    [Fact]
    public void Dispatch_RoutesSingleDebugEntryUnderSttChildCategory()
    {
        SttDispatchLog.Dispatch("manual", TimeSpan.FromSeconds(2.35), 75200);

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal(SttCategoryName, entry.CategoryName);
        Assert.Equal("Dispatching manual audio to STT (2.35 seconds, 75200 PCM bytes)", entry.Message);
        SttDispatchEntry dispatchEntry = Assert.IsType<SttDispatchEntry>(entry.State);
        Assert.Equal("manual", dispatchEntry.SourceMode);
        Assert.Equal(TimeSpan.FromSeconds(2.35), dispatchEntry.Duration);
        Assert.Equal(75200, dispatchEntry.PcmByteCount);
    }

    /// <summary>
    /// The STT dispatch marker respects the debug-enabled check of the STT child-category logger.
    /// </summary>
    [Fact]
    public void Dispatch_WhenDebugLoggingDisabled_DoesNotLog()
    {
        _provider.DebugEnabled = false;

        SttDispatchLog.Dispatch("automatic", TimeSpan.FromSeconds(1), 16000);

        Assert.Empty(_provider.Entries);
    }

    /// <summary>
    /// The dispatch entry inherits the five-second default toast timeout from the notification entry interface.
    /// </summary>
    [Fact]
    public void SttDispatchEntry_DefaultsToFiveSecondNotificationTimeout()
    {
        IUINotificationEntry dispatchEntry = new SttDispatchEntry("manual", TimeSpan.FromSeconds(2.35), 75200);

        Assert.Equal(5.0, dispatchEntry.NotificationTimeoutSeconds);
    }

    /// <summary>
    /// Configuration-rule prefix matching on the real category names lets a parent pipeline level admit the STT
    /// child's debug entry by inheritance, while an explicit STT child entry — the shipped YAML toggle — overrides
    /// the parent in both directions.
    /// </summary>
    [Fact]
    public void Log_WithSttDispatchEntry_ParentRulesAdmitByPrefixWhileChildEntryOverrides()
    {
        CapturingNotificationSink parentTraceSink = new();
        using NotificationLoggerProvider parentTraceProvider = new(parentTraceSink);
        using ILoggerFactory parentTraceFactory = LoggerFactory.Create(
            builder => builder.AddProvider(parentTraceProvider).AddFilter("AlleyCat.Pipeline", LogLevel.Trace));
        LogSttDispatchEntry(parentTraceFactory.CreateLogger(SttCategoryName), LogLevel.Debug);
        Assert.Equal(
            "Dispatching manual audio to STT (2.35 seconds, 75200 PCM bytes)",
            Assert.Single(parentTraceSink.Messages));

        CapturingNotificationSink childNoneSink = new();
        using NotificationLoggerProvider childNoneProvider = new(childNoneSink);
        using ILoggerFactory childNoneFactory = LoggerFactory.Create(
            builder => builder.AddProvider(childNoneProvider)
                .AddFilter("AlleyCat.Pipeline", LogLevel.Trace)
                .AddFilter(SttCategoryName, LogLevel.None));
        LogSttDispatchEntry(childNoneFactory.CreateLogger(SttCategoryName), LogLevel.Debug);
        Assert.Empty(childNoneSink.Messages);
    }

    /// <summary>
    /// Clears the test logger override.
    /// </summary>
    public void Dispose()
    {
        PipelineDebugLog.SetLoggerFactoryForTesting(null);
        _loggerFactory.Dispose();
    }

    private static void LogSttDispatchEntry(ILogger logger, LogLevel logLevel)
        => logger.Log(
            logLevel,
            default,
            new SttDispatchEntry("manual", TimeSpan.FromSeconds(2.35), 75200),
            exception: null,
            static (state, _) => state.ToNotificationText());

    private sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly List<CapturedLogEntry> _entries = [];

        public bool TraceEnabled
        {
            get;
            set;
        } = true;

        public bool DebugEnabled
        {
            get;
            set;
        } = true;

        public IReadOnlyList<CapturedLogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLoggerFactory(CapturingLogProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(
        string categoryName,
        CapturingLogProvider provider,
        List<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel is not LogLevel.None
                && (logLevel is not LogLevel.Trace || provider.TraceEnabled)
                && (logLevel is not LogLevel.Debug || provider.DebugEnabled);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception), state));
    }

    private sealed record CapturedLogEntry(string CategoryName, LogLevel Level, string Message, object? State);

    private sealed class CapturingNotificationSink : ILogNotificationSink
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
        {
            if (message is not null)
            {
                _messages.Add(message);
            }

            return true;
        }
    }
}
