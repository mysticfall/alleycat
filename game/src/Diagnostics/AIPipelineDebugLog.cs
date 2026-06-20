using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Diagnostics;

/// <summary>
/// Lightweight diagnostic logging for player speech to NPC reaction latency.
/// </summary>
internal static class AIPipelineDebugLog
{
    private const string CategoryName = "AlleyCat.AIPipeline";

    private static ILogger? _logger;
    private static ILoggerFactory? _loggerFactoryOverride;

    public static Stopwatch StartTimer() => Stopwatch.StartNew();

    public static bool IsEnabled => GetLogger().IsEnabled(LogLevel.Debug);

    public static void Stage(string stage, string? detail = null)
    {
        ILogger logger = GetLogger();
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("AI pipeline stage {Stage}{DetailSuffix}", stage, FormatDetailSuffix(detail));
        }
    }

    public static void Latency(string stage, Stopwatch stopwatch, string? detail = null)
        => Latency(stage, stopwatch.Elapsed, detail);

    public static void Latency(string stage, TimeSpan elapsed, string? detail = null)
    {
        ILogger logger = GetLogger();
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
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
