using AlleyCat.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Core.Logging;

/// <summary>
/// Unit coverage for the logger-to-notification bridge.
/// </summary>
public sealed class NotificationLoggerProviderTests
{
    /// <summary>
    /// Error logs reach the UI notification sink regardless of category.
    /// </summary>
    [Fact]
    public void LogError_WithTranscriberCategory_PostsNotification()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Transcription.Transcriber");

        logger.LogError(new InvalidOperationException("backend unavailable"), "Voice transcription failed.");

        string message = Assert.Single(sink.Messages);
        Assert.Contains("AlleyCat.Speech.Transcription.Transcriber", message, StringComparison.Ordinal);
        Assert.Contains("Voice transcription failed.", message, StringComparison.Ordinal);
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    /// <summary>
    /// Non-transcriber error logs still reach the UI notification sink.
    /// </summary>
    [Fact]
    public void LogError_WithOrdinaryCategory_PostsNotification()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Voice.AIVoice");

        logger.LogError("Ordinary runtime failure.");

        string message = Assert.Single(sink.Messages);
        Assert.Contains("AlleyCat.Speech.Voice.AIVoice", message, StringComparison.Ordinal);
        Assert.Contains("Ordinary runtime failure.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Logs below the configured minimum level do not reach the UI notification sink.
    /// </summary>
    [Fact]
    public void LogWarning_WhenMinimumLevelIsError_DoesNotPostNotification()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Transcription.OpenAITranscriber");
        logger.LogWarning("Non-fatal transcription diagnostic.");

        Assert.Empty(sink.Messages);
        Assert.False(logger.IsEnabled(LogLevel.Warning));
    }

    /// <summary>
    /// Notification-eligible entries logged at trace level — the level all pipeline diagnostics emit at — post
    /// exactly their notification text through a factory whose category filter admits trace entries.
    /// </summary>
    [Fact]
    public void Log_WithNotificationEntry_AtTraceWithCategoryAdmitted_PostsEntryNotificationText()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(
            builder => builder.AddProvider(provider).AddFilter<NotificationLoggerProvider>(
                "AlleyCat.Pipeline",
                LogLevel.Trace));
        ILogger logger = loggerFactory.CreateLogger("AlleyCat.Pipeline");

        LogLatencyEntry(logger, LogLevel.Trace);

        Assert.Equal("TTS audio generated in 1.4 seconds (44100 bytes)", Assert.Single(sink.Messages));
    }

    /// <summary>
    /// A category floor above trace filters trace entries before they reach the provider, so the configured level
    /// alone keeps pipeline notifications off without any second switch.
    /// </summary>
    [Fact]
    public void Log_WithNotificationEntry_AtTraceWithCategoryFloorAboveTrace_ReachesNothing()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(
            builder => builder.AddProvider(provider).AddFilter<NotificationLoggerProvider>(
                "AlleyCat.Pipeline",
                LogLevel.Information));
        ILogger logger = loggerFactory.CreateLogger("AlleyCat.Pipeline");

        LogLatencyEntry(logger, LogLevel.Trace);

        Assert.Empty(sink.Messages);
    }

    /// <summary>
    /// Ordinary trace entries stay filtered by the provider's minimum-level short-circuit.
    /// </summary>
    [Fact]
    public void LogTrace_WithPlainState_DoesNotPostNotification()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Voice.AIVoice");

        logger.LogTrace("Ordinary trace diagnostic.");

        Assert.Empty(sink.Messages);
    }

    /// <summary>
    /// Notification-eligible entries posted from inside a sink post are dropped by the re-entrancy guard.
    /// </summary>
    [Fact]
    public void Log_WithNotificationEntry_DropsReentrantEntryPosts()
    {
        ReentrantNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Pipeline");
        sink.Logger = logger;

        LogLatencyEntry(logger, LogLevel.Trace);

        Assert.Equal("TTS audio generated in 1.4 seconds (44100 bytes)", Assert.Single(sink.Messages));
    }

    /// <summary>
    /// Error posts triggered from inside a sink post are dropped by the re-entrancy guard.
    /// </summary>
    [Fact]
    public void LogError_WhilePosting_DropsReentrantErrorPosts()
    {
        ReentrantNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Voice.AIVoice");
        sink.Logger = logger;

        logger.LogError("Ordinary runtime failure.");

        string message = Assert.Single(sink.Messages);
        Assert.Contains("Ordinary runtime failure.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-entrant failure", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Notification-eligible entries pass their own toast timeout to the sink instead of the sink default.
    /// </summary>
    [Fact]
    public void Log_WithNotificationEntry_PassesEntryTimeoutToSink()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(
            builder => builder.AddProvider(provider).AddFilter<NotificationLoggerProvider>(
                "AlleyCat.Pipeline",
                LogLevel.Trace));
        ILogger logger = loggerFactory.CreateLogger("AlleyCat.Pipeline");

        LogLatencyEntry(logger, LogLevel.Trace);

        Assert.Equal("TTS audio generated in 1.4 seconds (44100 bytes)", Assert.Single(sink.Messages));
        Assert.Equal(5.0, Assert.Single(sink.TimeoutSeconds));
    }

    /// <summary>
    /// Error posts keep the notification sink's default timeout because they are not entry-driven.
    /// </summary>
    [Fact]
    public void LogError_PostsWithTheSinkDefaultTimeout()
    {
        CapturingNotificationSink sink = new();
        using NotificationLoggerProvider provider = new(sink);
        ILogger logger = provider.CreateLogger("AlleyCat.Speech.Voice.AIVoice");

        logger.LogError("Ordinary runtime failure.");

        _ = Assert.Single(sink.Messages);
        Assert.Equal(3.0, Assert.Single(sink.TimeoutSeconds));
    }

    private static void LogLatencyEntry(ILogger logger, LogLevel logLevel)
        => logger.Log(
            logLevel,
            default,
            new PipelineLatencyEntry("TTS audio generated in", TimeSpan.FromSeconds(1.4), "44100 bytes"),
            exception: null,
            static (state, _) => $"AI pipeline latency {state.Stage} 1400 ms (44100 bytes)");

    private sealed class CapturingNotificationSink : ILogNotificationSink
    {
        private readonly List<string> _messages = [];
        private readonly List<double> _timeoutSeconds = [];

        public IReadOnlyList<string> Messages => _messages;

        public IReadOnlyList<double> TimeoutSeconds => _timeoutSeconds;

        public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
        {
            _timeoutSeconds.Add(timeoutSeconds);
            if (message is not null)
            {
                _messages.Add(message);
            }

            return true;
        }
    }

    private sealed class ReentrantNotificationSink : ILogNotificationSink
    {
        private readonly List<string> _messages = [];

        public ILogger? Logger
        {
            get;
            set;
        }

        public IReadOnlyList<string> Messages => _messages;

        public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
        {
            if (message is null)
            {
                return true;
            }

            Logger?.LogError("Re-entrant failure while posting a notification.");
            _messages.Add(message);
            return true;
        }
    }
}
