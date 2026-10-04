namespace AlleyCat.Control.Locomotion;

/// <summary>
/// One clip or slice that contributes to the pose on a tick: its planted intervals, its playback position in
/// source-clip seconds, and its blend weight. Weights need not be normalised; <see cref="PlantedCombinationRule" />
/// normalises them over all contributions of the tick.
/// </summary>
public readonly record struct PlantedClipContribution(PlantedClipIntervals Clip, double SourceTimeSeconds, double Weight);

/// <summary>Per-foot planted state of one tick.</summary>
public readonly record struct PlantedFeetState(bool Left, bool Right)
{
    /// <summary>Whether the given anatomical foot is planted.</summary>
    public bool IsPlanted(StandingLocomotionAnatomicalSide foot) =>
        foot == StandingLocomotionAnatomicalSide.Left ? Left : Right;
}

/// <summary>
/// The ANIM-002 Planted Combination Rule: when several weighted clips or slices contribute to the pose on a tick, a
/// foot is planted only if every contributing clip whose normalised blend weight exceeds
/// <see cref="WeightThreshold" /> marks it planted at that clip's playback position, and at least one contribution
/// decides; otherwise it is not planted. A single contributing clip's own intervals therefore apply directly. Callers supply the contributions and their weights from their own weight source
/// (for example AnimationTree blend parameters and transition progress); the rule itself is pure and allocation-free.
/// </summary>
public static class PlantedCombinationRule
{
    /// <summary>
    /// Fixed ANIM-002 spec constant: a contribution whose normalised weight exceeds this value decides planted state.
    /// </summary>
    public const double WeightThreshold = 0.1d;

    /// <summary>Evaluates both feet for one tick's contributions.</summary>
    /// <exception cref="ArgumentException">
    /// No contributions are given, a weight is negative or non-finite, or the weights sum to zero.
    /// </exception>
    public static PlantedFeetState Evaluate(ReadOnlySpan<PlantedClipContribution> contributions)
    {
        double total = TotalWeight(contributions);
        return new(
            IsPlanted(contributions, total, StandingLocomotionAnatomicalSide.Left),
            IsPlanted(contributions, total, StandingLocomotionAnatomicalSide.Right));
    }

    /// <summary>Evaluates one foot for one tick's contributions.</summary>
    /// <exception cref="ArgumentException">
    /// No contributions are given, a weight is negative or non-finite, or the weights sum to zero.
    /// </exception>
    public static bool IsPlanted(
        ReadOnlySpan<PlantedClipContribution> contributions,
        StandingLocomotionAnatomicalSide foot) =>
        IsPlanted(contributions, TotalWeight(contributions), foot);

    /// <summary>Whether a contribution's normalised weight exceeds <see cref="WeightThreshold" />.</summary>
    public static bool Decides(double normalisedWeight) => normalisedWeight > WeightThreshold;

    private static bool IsPlanted(
        ReadOnlySpan<PlantedClipContribution> contributions,
        double total,
        StandingLocomotionAnatomicalSide foot)
    {
        // A planted claim needs at least one deciding contribution. With ten or more contributions every normalised
        // weight can sit at or below the threshold (for example ten equal weights of exactly 0.1); no clip then has
        // authority to plant the foot, so it is not planted (the rule's "otherwise" case) rather than vacuously planted.
        bool decided = false;
        foreach (PlantedClipContribution contribution in contributions)
        {
            if (!Decides(contribution.Weight / total))
            {
                continue;
            }

            if (!contribution.Clip.IsPlanted(foot, contribution.SourceTimeSeconds))
            {
                return false;
            }

            decided = true;
        }

        return decided;
    }

    private static double TotalWeight(ReadOnlySpan<PlantedClipContribution> contributions)
    {
        if (contributions.IsEmpty)
        {
            throw new ArgumentException("At least one contributing clip is required.", nameof(contributions));
        }

        double total = 0d;
        foreach (PlantedClipContribution contribution in contributions)
        {
            ArgumentNullException.ThrowIfNull(contribution.Clip, nameof(contributions));
            if (!double.IsFinite(contribution.Weight) || contribution.Weight < 0d)
            {
                throw new ArgumentException(
                    $"Contribution weight of '{contribution.Clip.Identity}' must be finite and non-negative.",
                    nameof(contributions));
            }

            total += contribution.Weight;
        }

        return total > 0d
            ? total
            : throw new ArgumentException("Contribution weights must not all be zero.", nameof(contributions));
    }
}
