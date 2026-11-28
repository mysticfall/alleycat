namespace AlleyCat.Animations;

/// <summary>
/// ANIM-003 TR18 settle easing for MotionBricks bridges into and out of catalogue idles. Maps bridge playback time to
/// generated source time: an optional quadratic ease-in from zero rate, a linear middle at the source rate, and an
/// optional quadratic ease-out to zero rate at the seam. Each ease plays its source span at half the average rate, so
/// it lasts twice as long as the span it covers, and the rate is continuous where it meets the linear middle.
/// </summary>
/// <remarks>
/// This warp is scoped to catalogue-idle bridges. It must never be applied to locomotion source sampling or to the
/// CTRL-001 player settle (ANIM TR2–TR3).
/// </remarks>
public readonly record struct BridgeTimeWarp
{
    private BridgeTimeWarp(double sourceDuration, double easeInSource, double easeOutSource)
    {
        SourceDuration = sourceDuration;
        EaseInSource = easeInSource;
        EaseOutSource = easeOutSource;
    }

    /// <summary>Generated bridge duration in seconds.</summary>
    public double SourceDuration
    {
        get;
    }

    /// <summary>Source seconds covered by the ease-in.</summary>
    public double EaseInSource
    {
        get;
    }

    /// <summary>Source seconds covered by the ease-out.</summary>
    public double EaseOutSource
    {
        get;
    }

    /// <summary>Playback seconds spent in the ease-in.</summary>
    public double EaseInPlayback => EaseInSource * 2;

    /// <summary>Playback seconds spent in the ease-out.</summary>
    public double EaseOutPlayback => EaseOutSource * 2;

    /// <summary>Total playback seconds; the seam is reached at this playback time.</summary>
    public double PlaybackDuration => SourceDuration + EaseInSource + EaseOutSource;

    /// <summary>An unwarped mapping (playback time equals source time).</summary>
    public static BridgeTimeWarp Identity(double sourceDuration) => Create(sourceDuration, 0, 0);

    /// <summary>
    /// Creates a warp. Ease durations are playback seconds. When the two eases would cover more than the whole bridge,
    /// both shrink proportionally so the linear middle has zero length.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for a negative or non-finite duration.</exception>
    public static BridgeTimeWarp Create(double sourceDuration, double easeInPlayback, double easeOutPlayback)
    {
        if (!double.IsFinite(sourceDuration) || sourceDuration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        }

        if (!double.IsFinite(easeInPlayback) || easeInPlayback < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(easeInPlayback));
        }

        if (!double.IsFinite(easeOutPlayback) || easeOutPlayback < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(easeOutPlayback));
        }

        double easeIn = easeInPlayback / 2;
        double easeOut = easeOutPlayback / 2;
        double covered = easeIn + easeOut;
        if (covered > sourceDuration && covered > 0)
        {
            double scale = sourceDuration / covered;
            easeIn *= scale;
            easeOut *= scale;
        }

        return new BridgeTimeWarp(sourceDuration, easeIn, easeOut);
    }

    /// <summary>Source time for a playback time, clamped to the bridge.</summary>
    public double SourceTime(double playback)
    {
        if (playback <= 0)
        {
            return 0;
        }

        if (playback >= PlaybackDuration)
        {
            return SourceDuration;
        }

        if (playback < EaseInPlayback)
        {
            double u = playback / EaseInPlayback;
            return EaseInSource * u * u;
        }

        double middleEnd = EaseInPlayback + (SourceDuration - EaseInSource - EaseOutSource);
        if (playback <= middleEnd)
        {
            return EaseInSource + (playback - EaseInPlayback);
        }

        double v = (playback - middleEnd) / EaseOutPlayback;
        return SourceDuration - EaseOutSource + (EaseOutSource * (1 - ((1 - v) * (1 - v))));
    }

    /// <summary>Source seconds advanced per playback second at a playback time.</summary>
    public double Rate(double playback)
    {
        if (playback <= 0)
        {
            return EaseInPlayback > 0 ? 0 : 1;
        }

        if (playback >= PlaybackDuration)
        {
            return EaseOutPlayback > 0 ? 0 : 1;
        }

        if (playback < EaseInPlayback)
        {
            return playback / EaseInPlayback;
        }

        double middleEnd = EaseInPlayback + (SourceDuration - EaseInSource - EaseOutSource);
        return playback <= middleEnd ? 1 : 1 - ((playback - middleEnd) / EaseOutPlayback);
    }
}
