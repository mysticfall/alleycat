using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Lightweight structured diagnostic logging for pipeline stages and latency measurements.
/// </summary>
/// <remarks>
/// Every entry emits at trace level, so the configured level of the <c>AlleyCat.Pipeline</c> category is the single
/// universal switch for pipeline diagnostics: opting the category into trace logging enables both console output and
/// notification routing, while the shipped information default keeps both off.
/// </remarks>
internal static class PipelineDebugLog
{
    private const string CategoryName = "AlleyCat.Pipeline";

    private static ILogger? _logger;
    private static ILoggerFactory? _loggerFactoryOverride;

    public static Stopwatch StartTimer() => Stopwatch.StartNew();

    public static bool IsEnabled => GetLogger().IsEnabled(LogLevel.Trace);

    public static void Stage(string stage, string? detail = null)
    {
        ILogger logger = GetLogger();
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("AI pipeline stage {Stage}{DetailSuffix}", stage, FormatDetailSuffix(detail));
        }
    }

    /// <summary>
    /// Logs a point-in-time marker whose entry state is eligible for notification display.
    /// </summary>
    public static void Marker(string stage, string? detail = null)
    {
        ILogger logger = GetLogger();
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.Log(
                LogLevel.Trace,
                default,
                new PipelineMarkerEntry(stage, detail),
                exception: null,
                static (state, _) => $"AI pipeline stage {state.Stage}{FormatDetailSuffix(state.Detail)}");
        }
    }

    public static void Latency(
        string stage,
        Stopwatch stopwatch,
        string? detail = null,
        string? notificationDetail = null)
        => Latency(stage, stopwatch.Elapsed, detail, notificationDetail);

    /// <summary>
    /// Logs a latency measurement whose entry state is eligible for notification display. The console message always
    /// renders <paramref name="detail" /> in full, while the toast renders <paramref name="notificationDetail" />
    /// when supplied — null falls back to <paramref name="detail" /> and an empty value omits the toast suffix.
    /// </summary>
    public static void Latency(string stage, TimeSpan elapsed, string? detail = null, string? notificationDetail = null)
    {
        ILogger logger = GetLogger();
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.Log(
                LogLevel.Trace,
                default,
                new PipelineLatencyEntry(stage, elapsed, detail, notificationDetail),
                exception: null,
                static (state, _) =>
                    $"AI pipeline latency {state.Stage} {FormatMilliseconds(state.Elapsed)} ms{FormatDetailSuffix(state.Detail)}");
        }
    }

    public static void LogOnlyLatency(string stage, Stopwatch stopwatch, string? detail = null)
        => LogOnlyLatency(stage, stopwatch.Elapsed, detail);

    /// <summary>
    /// Logs a latency measurement that never becomes a notification, for failure paths, high-frequency
    /// micro-stages, and pipeline stages excluded from notification display.
    /// </summary>
    public static void LogOnlyLatency(string stage, TimeSpan elapsed, string? detail = null)
    {
        ILogger logger = GetLogger();
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace(
                "AI pipeline latency {Stage} {ElapsedMilliseconds} ms{DetailSuffix}",
                stage,
                FormatMilliseconds(elapsed),
                FormatDetailSuffix(detail));
        }
    }

    internal static void SetLoggerFactoryForTesting(ILoggerFactory? loggerFactory)
    {
        _loggerFactoryOverride = loggerFactory;
        _logger = null;
    }

    private static ILogger GetLogger() => _logger ??= CreateLogger();

    private static ILogger CreateLogger()
        => _loggerFactoryOverride is not null
            ? _loggerFactoryOverride.CreateLogger(CategoryName)
            : Game.Instance.GetRequiredService<ILoggerFactory>().CreateLogger(CategoryName);

    private static string FormatDetailSuffix(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";

    private static string FormatMilliseconds(TimeSpan elapsed)
        => elapsed.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture);

}
