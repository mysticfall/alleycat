using System.Globalization;
using AlleyCat.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Core.Logging;

/// <summary>
/// Unit coverage for pipeline diagnostics routed through Microsoft.Extensions.Logging.
/// </summary>
public sealed class PipelineDebugLogTests : IDisposable
{
    private readonly CapturingLoggerProvider _provider = new();
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Installs a capturing logger factory for each test.
    /// </summary>
    public PipelineDebugLogTests()
    {
        _loggerFactory = new CapturingLoggerFactory(_provider);
        PipelineDebugLog.SetLoggerFactoryForTesting(_loggerFactory);
    }

    /// <summary>
    /// Stage diagnostics are emitted as trace log entries.
    /// </summary>
    [Fact]
    public void Stage_RoutesStructuredTraceLog()
    {
        PipelineDebugLog.Stage("LLM observation received", "42 chars");

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal(LogLevel.Trace, entry.Level);
        Assert.Equal("AlleyCat.Pipeline", entry.CategoryName);
        Assert.Contains("LLM observation received", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Latency diagnostics keep the millisecond log message while carrying the notification entry as state.
    /// </summary>
    [Fact]
    public void Latency_RoutesStructuredTraceLog()
    {
        PipelineDebugLog.Latency("TTS backend returned in", TimeSpan.FromMilliseconds(12.34), "model tts-1");

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal(LogLevel.Trace, entry.Level);
        Assert.Equal("AlleyCat.Pipeline", entry.CategoryName);
        Assert.Contains("TTS backend returned in", entry.Message, StringComparison.Ordinal);
        Assert.Contains("12.34", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Latency diagnostics are emitted with byte-identical messages and notification-eligible entry state.
    /// </summary>
    [Fact]
    public void Latency_CarriesNotificationEntryStateWithUnchangedMessage()
    {
        PipelineDebugLog.Latency("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal("AI pipeline latency TTS audio generated in 1400 ms (44100 bytes)", entry.Message);
        PipelineLatencyEntry latencyEntry = Assert.IsType<PipelineLatencyEntry>(entry.State);
        Assert.Equal("TTS audio generated in", latencyEntry.Stage);
        Assert.Equal(TimeSpan.FromMilliseconds(1400), latencyEntry.Elapsed);
        Assert.Equal("44100 bytes", latencyEntry.Detail);
    }

    /// <summary>
    /// Marker diagnostics are emitted as trace log entries with notification-eligible entry state.
    /// </summary>
    [Fact]
    public void Marker_RoutesStructuredTraceLogWithEntryState()
    {
        PipelineDebugLog.Marker("Speak tool invoked", "142 chars");

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal(LogLevel.Trace, entry.Level);
        Assert.Equal("AlleyCat.Pipeline", entry.CategoryName);
        Assert.Equal("AI pipeline stage Speak tool invoked (142 chars)", entry.Message);
        PipelineMarkerEntry markerEntry = Assert.IsType<PipelineMarkerEntry>(entry.State);
        Assert.Equal("Speak tool invoked", markerEntry.Stage);
        Assert.Equal("142 chars", markerEntry.Detail);
    }

    /// <summary>
    /// Marker diagnostics respect the trace-enabled check of the underlying logger.
    /// </summary>
    [Fact]
    public void Marker_WhenTraceLoggingDisabled_DoesNotLog()
    {
        _provider.TraceEnabled = false;

        PipelineDebugLog.Marker("Speak tool invoked", "142 chars");

        Assert.False(PipelineDebugLog.IsEnabled);
        Assert.Empty(_provider.Entries);
    }

    /// <summary>
    /// Log-only latency diagnostics are emitted without notification-eligible entry state.
    /// </summary>
    [Fact]
    public void LogOnlyLatency_RoutesStructuredTraceLogWithoutNotificationEntryState()
    {
        PipelineDebugLog.LogOnlyLatency("TTS failed after", TimeSpan.FromMilliseconds(12.34));

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.Equal(LogLevel.Trace, entry.Level);
        Assert.Equal("AI pipeline latency TTS failed after 12.34 ms", entry.Message);
        Assert.False(entry.State is IUINotificationEntry);
    }

    /// <summary>
    /// Stage diagnostics never carry notification-eligible entry state.
    /// </summary>
    [Fact]
    public void Stage_DoesNotCarryNotificationEntryState()
    {
        PipelineDebugLog.Stage("STT recording started");

        CapturedLogEntry entry = Assert.Single(_provider.Entries);
        Assert.False(entry.State is IUINotificationEntry);
    }

    /// <summary>
    /// The four stages kept notification-eligible after the refinement — STT backend return, TTS audio generation,
    /// TTS lip-sync preparation, and the speak-tool marker — all carry entry state, and the toast texts stay short
    /// single-line strings.
    /// </summary>
    [Fact]
    public void KeptStages_CarryNotificationEntryStateWithSingleLineTexts()
    {
        PipelineDebugLog.Latency(
            "STT backend returned in",
            TimeSpan.FromMilliseconds(1200),
            "model whisper-1",
            notificationDetail: string.Empty);
        PipelineDebugLog.Latency("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");
        PipelineDebugLog.Latency(
            "TTS lip-sync prepared in",
            TimeSpan.FromMilliseconds(2100),
            "404 frames, 2 mesh(es)",
            notificationDetail: "404 frames");
        PipelineDebugLog.Marker("Speak tool invoked", "142 chars");

        Assert.Equal(4, _provider.Entries.Count);
        Assert.All(_provider.Entries, entry => Assert.IsAssignableFrom<IUINotificationEntry>(entry.State));
        foreach (CapturedLogEntry entry in _provider.Entries)
        {
            string notificationText = ((IUINotificationEntry)entry.State!).ToNotificationText();
            Assert.DoesNotContain('\n', notificationText);
            Assert.DoesNotContain('\r', notificationText);
        }

        // The STT toast drops the model suffix and the lip-sync toast keeps only the frame count, while the
        // speak-tool marker and TTS audio generation keep their full details.
        Assert.Equal(
        [
            "STT backend returned in 1.2 seconds",
            "TTS audio generated in 1.4 seconds (44100 bytes)",
            "TTS lip-sync prepared in 2.1 seconds (404 frames)",
            "Speak tool invoked (142 chars)",
        ],
        _provider.Entries.Select(entry => ((IUINotificationEntry)entry.State!).ToNotificationText()));
    }

    /// <summary>
    /// The stages excluded from notification display keep full trace logging without entry state.
    /// </summary>
    [Fact]
    public void ExcludedStages_StayLogOnlyWithoutNotificationEntryState()
    {
        PipelineDebugLog.LogOnlyLatency("STT recording stopped after", TimeSpan.FromMilliseconds(2100));
        PipelineDebugLog.LogOnlyLatency("STT completed in", TimeSpan.FromMilliseconds(1200), "42 chars");
        PipelineDebugLog.LogOnlyLatency("STT request prepared in", TimeSpan.FromMilliseconds(1.2), "model whisper-1");
        PipelineDebugLog.LogOnlyLatency("TTS backend returned in", TimeSpan.FromMilliseconds(900), "model tts-1");
        PipelineDebugLog.LogOnlyLatency("TTS backend stream completed in", TimeSpan.FromMilliseconds(900), "model tts-1");
        PipelineDebugLog.LogOnlyLatency("TTS audio parsed in", TimeSpan.FromMilliseconds(12), "44100 PCM bytes");
        PipelineDebugLog.LogOnlyLatency("TTS playback started after", TimeSpan.FromMilliseconds(2056));

        Assert.Equal(7, _provider.Entries.Count);
        Assert.All(_provider.Entries, entry => Assert.False(entry.State is IUINotificationEntry));
    }

    /// <summary>
    /// Switching a stage from latency to log-only latency leaves the console message byte-identical.
    /// </summary>
    [Fact]
    public void LogOnlyLatency_RendersByteIdenticalConsoleMessageToLatency()
    {
        PipelineDebugLog.Latency("TTS backend returned in", TimeSpan.FromMilliseconds(12.34), "model tts-1");
        PipelineDebugLog.LogOnlyLatency("TTS backend returned in", TimeSpan.FromMilliseconds(12.34), "model tts-1");

        Assert.Equal(2, _provider.Entries.Count);
        Assert.Equal(_provider.Entries[0].Message, _provider.Entries[1].Message);
    }

    /// <summary>
    /// Pipeline entries inherit the five-second default toast timeout from the notification entry interface.
    /// </summary>
    [Fact]
    public void PipelineEntries_DefaultToFiveSecondNotificationTimeout()
    {
        IUINotificationEntry latencyEntry = new PipelineLatencyEntry("TTS audio generated in", TimeSpan.FromSeconds(1.4));
        IUINotificationEntry markerEntry = new PipelineMarkerEntry("Speak tool invoked");

        Assert.Equal(5.0, latencyEntry.NotificationTimeoutSeconds);
        Assert.Equal(5.0, markerEntry.NotificationTimeoutSeconds);
    }

    /// <summary>
    /// Latency entries render whole-second texts without a detail suffix.
    /// </summary>
    [Fact]
    public void PipelineLatencyEntry_RendersSecondsWithoutDetailSuffix()
    {
        PipelineLatencyEntry entry = new("TTS playback started after", TimeSpan.FromMilliseconds(2056));

        Assert.Equal("TTS playback started after 2.06 seconds", entry.ToNotificationText());
    }

    /// <summary>
    /// Latency entries render the detail suffix after the seconds value.
    /// </summary>
    [Fact]
    public void PipelineLatencyEntry_RendersDetailSuffixAfterSeconds()
    {
        PipelineLatencyEntry entry = new("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");

        Assert.Equal("TTS audio generated in 1.4 seconds (44100 bytes)", entry.ToNotificationText());
    }

    /// <summary>
    /// An empty notification detail omits the toast suffix entirely — no trailing space or parentheses — as the
    /// STT backend stage does for its model detail.
    /// </summary>
    [Fact]
    public void PipelineLatencyEntry_EmptyNotificationDetail_OmitsToastDetailSuffix()
    {
        PipelineLatencyEntry entry = new(
            "STT backend returned in",
            TimeSpan.FromMilliseconds(258.58),
            "model whisper-1",
            NotificationDetail: string.Empty);

        Assert.Equal("STT backend returned in 0.26 seconds", entry.ToNotificationText());
    }

    /// <summary>
    /// A shortened notification detail replaces the console detail in the toast, as the lip-sync stage does when
    /// it keeps only the frame count.
    /// </summary>
    [Fact]
    public void PipelineLatencyEntry_ShortenedNotificationDetail_ReplacesConsoleDetailInToast()
    {
        PipelineLatencyEntry entry = new(
            "TTS lip-sync prepared in",
            TimeSpan.FromMilliseconds(3446.62),
            "404 frames, 2 mesh(es)",
            NotificationDetail: "404 frames");

        Assert.Equal("TTS lip-sync prepared in 3.45 seconds (404 frames)", entry.ToNotificationText());
    }

    /// <summary>
    /// Shortened notification details only affect toasts: the console formatter keeps rendering the full detail
    /// suffix byte-identically for the affected stages.
    /// </summary>
    [Fact]
    public void Latency_WithShortenedNotificationDetail_KeepsFullConsoleDetail()
    {
        PipelineDebugLog.Latency(
            "STT backend returned in",
            TimeSpan.FromMilliseconds(1200),
            "model whisper-1",
            notificationDetail: string.Empty);
        PipelineDebugLog.Latency(
            "TTS lip-sync prepared in",
            TimeSpan.FromMilliseconds(2100),
            "404 frames, 2 mesh(es)",
            notificationDetail: "404 frames");

        Assert.Equal(2, _provider.Entries.Count);
        Assert.Equal(
            "AI pipeline latency STT backend returned in 1200 ms (model whisper-1)",
            _provider.Entries[0].Message);
        Assert.Equal(
            "AI pipeline latency TTS lip-sync prepared in 2100 ms (404 frames, 2 mesh(es))",
            _provider.Entries[1].Message);
    }

    /// <summary>
    /// Latency entries format seconds with the invariant culture so decimal separators stay stable.
    /// </summary>
    [Fact]
    public void PipelineLatencyEntry_FormatsSecondsUsingInvariantCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo commaDecimalCulture;
        try
        {
            commaDecimalCulture = new CultureInfo("de-DE");
        }
        catch (CultureNotFoundException)
        {
            // Invariant-globalisation hosts cannot prove invariant formatting through a culture switch.
            return;
        }

        try
        {
            CultureInfo.CurrentCulture = commaDecimalCulture;
            PipelineLatencyEntry entry = new("TTS audio generated in", TimeSpan.FromMilliseconds(1400), "44100 bytes");
            Assert.Equal("TTS audio generated in 1.4 seconds (44100 bytes)", entry.ToNotificationText());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// Marker entries render the stage with a parenthesised detail when one is present.
    /// </summary>
    [Fact]
    public void PipelineMarkerEntry_RendersStageAndDetail()
    {
        Assert.Equal(
            "Speak tool invoked (142 chars)",
            new PipelineMarkerEntry("Speak tool invoked", "142 chars").ToNotificationText());
        Assert.Equal(
            "Speak tool invoked",
            new PipelineMarkerEntry("Speak tool invoked").ToNotificationText());
    }

    /// <summary>
    /// Production diagnostics fail clearly instead of suppressing missing logging infrastructure.
    /// </summary>
    [Fact]
    public void IsEnabled_WithoutLoggerOverrideOrGameInfrastructure_ThrowsInvalidOperationException()
    {
        PipelineDebugLog.SetLoggerFactoryForTesting(null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => _ = PipelineDebugLog.IsEnabled);
        Assert.Contains("Game singleton", exception.Message, StringComparison.Ordinal);

        PipelineDebugLog.SetLoggerFactoryForTesting(_loggerFactory);
    }

    /// <summary>
    /// Clears the test logger override.
    /// </summary>
    public void Dispose()
    {
        PipelineDebugLog.SetLoggerFactoryForTesting(null);
        _loggerFactory.Dispose();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLogEntry> _entries = [];

        public bool TraceEnabled
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

    private sealed class CapturingLogger(
        string categoryName,
        CapturingLoggerProvider provider,
        List<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel is not LogLevel.None && (logLevel is not LogLevel.Trace || provider.TraceEnabled);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception), state));
    }

    private sealed record CapturedLogEntry(string CategoryName, LogLevel Level, string Message, object? State);
}
