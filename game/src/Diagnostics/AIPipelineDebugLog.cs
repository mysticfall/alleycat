using System.Diagnostics;
using System.Globalization;
using Godot;

namespace AlleyCat.Diagnostics;

/// <summary>
/// Lightweight diagnostic logging for player speech to NPC reaction latency.
/// </summary>
internal static class AIPipelineDebugLog
{
    private const string Prefix = "[AI Pipeline]";

    public static Stopwatch StartTimer() => Stopwatch.StartNew();

    public static void Stage(string stage, string? detail = null)
        => GD.Print(Format(stage, detail));

    public static void Latency(string stage, Stopwatch stopwatch, string? detail = null)
        => Latency(stage, stopwatch.Elapsed, detail);

    public static void Latency(string stage, TimeSpan elapsed, string? detail = null)
        => GD.Print(Format($"{stage} {FormatMilliseconds(elapsed)} ms", detail));

    private static string Format(string message, string? detail)
        => string.IsNullOrWhiteSpace(detail)
            ? $"{Prefix} {message}"
            : $"{Prefix} {message} ({detail})";

    private static string FormatMilliseconds(TimeSpan elapsed)
        => elapsed.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture);
}
