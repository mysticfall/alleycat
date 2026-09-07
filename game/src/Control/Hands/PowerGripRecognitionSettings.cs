namespace AlleyCat.Control.Hands;

/// <summary>
/// Tunable recognition parameters for the animation-derived power-grip profile (XR-002 TR48; CTRL-002 TR12):
/// thresholds are expressed relative to the candidate animation's articulation — progress 1 is exactly the
/// reference articulation — so no universal fist threshold exists.
/// </summary>
/// <remarks>
/// <para>
/// Defaults: grab at 0.75 of the reference articulation (closing comfortably beyond the midway point recognises
/// the grab while tolerating modest under-curl), release at 0.55 (a substantial aggregate opening, comfortably more open than the grab
/// threshold so over-clench and single-finger variation never release), a 0.10 s stability interval (about six
/// frames at 60 Hz — filters transient flicker without noticeable lag), a ≈5° minimum reference angle before a
/// destination's articulation counts as a directional feature, and a fail-closed floor of half the featured
/// chain weight before the aggregate may drive recognition.
/// </para>
/// <para>
/// Every value is validated in the constructor: a release threshold at or above the grab threshold, a
/// non-finite or out-of-range threshold, a non-positive stability interval, a non-positive minimum reference
/// angle, and a validity fraction outside (0, 1] all fail explicitly rather than silently coercing.
/// </para>
/// </remarks>
public sealed class PowerGripRecognitionSettings
{
    /// <summary>Creates validated settings with the authored defaults or explicit overrides.</summary>
    /// <param name="grabThreshold">Aggregate progress at which a grab is recognised; in (0, 1].</param>
    /// <param name="releaseThreshold">
    /// Aggregate progress at or below which a held grab is released; in [0, 1) and strictly below
    /// <paramref name="grabThreshold" />.
    /// </param>
    /// <param name="stabilitySeconds">Continuous threshold hold required before an edge is emitted; positive.</param>
    /// <param name="minimumReferenceAngleRadians">
    /// Reference articulation a destination must show before it counts as a directional feature; positive.
    /// </param>
    /// <param name="minimumValidChainWeightFraction">
    /// Fraction of the featured chain weight that must be live-valid before the aggregate may drive
    /// recognition; in (0, 1].
    /// </param>
    /// <exception cref="ArgumentException">Thrown when any value violates its contract.</exception>
    public PowerGripRecognitionSettings(
        float grabThreshold = 0.75f,
        float releaseThreshold = 0.55f,
        float stabilitySeconds = 0.10f,
        float minimumReferenceAngleRadians = 0.0873f,
        float minimumValidChainWeightFraction = 0.5f)
    {
        if (!float.IsFinite(grabThreshold) || grabThreshold is <= 0.0f or > 1.0f)
        {
            throw new ArgumentException(
                $"The grab threshold must be finite and in (0, 1]; got {grabThreshold:R}.");
        }

        if (!float.IsFinite(releaseThreshold) || releaseThreshold is < 0.0f or >= 1.0f)
        {
            throw new ArgumentException(
                $"The release threshold must be finite and in [0, 1); got {releaseThreshold:R}.");
        }

        if (releaseThreshold >= grabThreshold)
        {
            throw new ArgumentException(
                $"The release threshold ({releaseThreshold:R}) must be strictly more open than the grab " +
                $"threshold ({grabThreshold:R}); hysteresis is mandatory.");
        }

        if (!float.IsFinite(stabilitySeconds) || stabilitySeconds <= 0.0f)
        {
            throw new ArgumentException(
                $"The stability interval must be finite and positive; got {stabilitySeconds:R}s.");
        }

        if (!float.IsFinite(minimumReferenceAngleRadians) || minimumReferenceAngleRadians <= 0.0f)
        {
            throw new ArgumentException(
                $"The minimum reference angle must be finite and positive; got {minimumReferenceAngleRadians:R} " +
                "radians.");
        }

        if (!float.IsFinite(minimumValidChainWeightFraction)
            || minimumValidChainWeightFraction is <= 0.0f or > 1.0f)
        {
            throw new ArgumentException(
                $"The minimum valid chain-weight fraction must be finite and in (0, 1]; got " +
                $"{minimumValidChainWeightFraction:R}.");
        }

        GrabThreshold = grabThreshold;
        ReleaseThreshold = releaseThreshold;
        StabilitySeconds = stabilitySeconds;
        MinimumReferenceAngleRadians = minimumReferenceAngleRadians;
        MinimumValidChainWeightFraction = minimumValidChainWeightFraction;
    }

    /// <summary>Aggregate progress at which a grab is recognised, relative to the reference articulation.</summary>
    public float GrabThreshold
    {
        get;
    }

    /// <summary>
    /// Aggregate progress at or below which a held grab is released — strictly more open than
    /// <see cref="GrabThreshold" />.
    /// </summary>
    public float ReleaseThreshold
    {
        get;
    }

    /// <summary>Continuous threshold hold required before an edge is emitted.</summary>
    public float StabilitySeconds
    {
        get;
    }

    /// <summary>Reference articulation a destination must show before it counts as a directional feature.</summary>
    public float MinimumReferenceAngleRadians
    {
        get;
    }

    /// <summary>
    /// Fraction of the featured chain weight that must be live-valid before the aggregate may drive recognition;
    /// below it the evaluation reports insufficient validity and no edge may be emitted (fail closed).
    /// </summary>
    public float MinimumValidChainWeightFraction
    {
        get;
    }

    /// <summary>The authored default settings.</summary>
    public static PowerGripRecognitionSettings Default { get; } = new();
}
