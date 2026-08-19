using System.Diagnostics;

namespace AlleyCat.Core.Time;

/// <summary>
/// Game clock measuring monotonic seconds elapsed since its origin, captured at construction.
/// </summary>
public sealed class GameClock(Func<double>? timestampSource = null) : IGameClock
{
    private Func<double> _timestampSource = timestampSource ?? GetStopwatchSeconds;

    private double OriginTimestamp { get; set; } = (timestampSource ?? GetStopwatchSeconds)();

    /// <inheritdoc />
    public double NowSeconds
    {
        get
        {
            double now = _timestampSource();
            return double.IsFinite(now)
                ? Math.Max(0d, now - OriginTimestamp)
                : throw new InvalidOperationException($"Game clock returned non-finite timestamp '{now}'.");
        }
    }

    /// <summary>
    /// Replaces the timestamp source and re-baselines the origin to the new source's current value,
    /// so elapsed time restarts from zero. This internal seam is available only to friend test
    /// assemblies and does not affect the production API.
    /// </summary>
    /// <param name="timestampSource">Monotonic timestamp source in seconds.</param>
    internal void SetTimestampSourceForTesting(Func<double> timestampSource)
    {
        ArgumentNullException.ThrowIfNull(timestampSource);
        _timestampSource = timestampSource;
        OriginTimestamp = timestampSource();
    }

    private static double GetStopwatchSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
