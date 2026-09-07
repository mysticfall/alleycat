using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>
/// Front-loaded tapered cumulative profile for a side-step planar remainder. The profile reaches one at phase one
/// and decelerates to a zero end-slope so synthetic side-step velocity ends with the authored moving clip.
/// </summary>
public static class NPCSideStepPlanarCorrection
{
    /// <summary>Per-frame synthetic correction ceiling in metres per second.</summary>
    public const float MaximumRate = 0.5f;

    private const float NumericalTolerance = NPCAnimationProgramme.NumericalConvergenceThreshold;

    /// <summary>
    /// Cumulative corrected fraction at normalised clip phase: f(t) = 1 − (1 − t)². Front-loaded (f′(0) = 2),
    /// monotone, f(1) = 1, and f′(1) = 0 so the exit-clip velocity tapers to exactly zero synthetic motion.
    /// </summary>
    public static float CumulativeFraction(float phase)
    {
        float clamped = Mathf.Clamp(float.IsFinite(phase) ? phase : 0f, 0f, 1f);
        float remaining = 1f - clamped;
        return 1f - (remaining * remaining);
    }

    /// <summary>
    /// Resolves the per-frame correction velocity from the live remaining total and the published accumulator:
    /// the cumulative profile target minus what has already been applied, bounded per-frame by
    /// <see cref="MaximumRate" /> and cumulatively by the committed snapshot magnitude. Once the un-applied
    /// envelope is within numerical tolerance the velocity is exactly zero — settlement owns sub-tolerance polish.
    /// </summary>
    public static Vector2 ResolveVelocity(
        Vector2 remainingTotalWorld,
        Vector2 publishedWorldCorrection,
        float phase,
        float snapshotMagnitude,
        float delta)
    {
        if (delta <= 0f
            || !remainingTotalWorld.IsFinite()
            || !publishedWorldCorrection.IsFinite()
            || !float.IsFinite(snapshotMagnitude)
            || snapshotMagnitude <= NumericalTolerance)
        {
            return Vector2.Zero;
        }

        Vector2 unapplied = remainingTotalWorld - publishedWorldCorrection;
        if (unapplied.LengthSquared() <= NumericalTolerance * NumericalTolerance)
        {
            return Vector2.Zero;
        }

        Vector2 cumulative = LimitCumulative(remainingTotalWorld * CumulativeFraction(phase), snapshotMagnitude);
        Vector2 velocity = (cumulative - publishedWorldCorrection) / delta;
        return velocity.IsFinite() ? velocity.LimitLength(MaximumRate) : Vector2.Zero;
    }

    /// <summary>
    /// Bounds one cumulative target by the committed snapshot magnitude, preserving its live direction so
    /// forecast drift can steer the tail while the applied correction can never exceed the committed envelope.
    /// </summary>
    public static Vector2 LimitCumulative(Vector2 cumulative, float snapshotMagnitude)
    {
        if (!cumulative.IsFinite() || !float.IsFinite(snapshotMagnitude) || snapshotMagnitude <= 0f)
        {
            return Vector2.Zero;
        }

        float magnitude = cumulative.Length();
        return magnitude > snapshotMagnitude ? cumulative * (snapshotMagnitude / magnitude) : cumulative;
    }
}
