namespace AlleyCat.Mind.Attention;

/// <summary>Supplies deterministic unit-interval samples for attention-driven gaze choices.</summary>
public interface IAttentionGazeRandom
{
    /// <summary>Returns the next sample in the half-open interval <c>[0, 1)</c>.</summary>
    double NextUnitInterval();
}

/// <summary>Configures dwell and secondary-glance behaviour for <see cref="AttentionGazeTargetPolicy{TTarget}"/>.</summary>
public sealed record AttentionGazeTargetSettings
{
    /// <summary>Initialises validated attention-driven gaze dwell settings.</summary>
    public AttentionGazeTargetSettings(
        double primaryDwellSeconds,
        double secondaryDwellSeconds,
        double secondaryGlanceProbability)
    {
        ValidateFinitePositive(primaryDwellSeconds, nameof(primaryDwellSeconds));
        ValidateFinitePositive(secondaryDwellSeconds, nameof(secondaryDwellSeconds));
        if (secondaryDwellSeconds >= primaryDwellSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secondaryDwellSeconds),
                secondaryDwellSeconds,
                "Secondary dwell must be shorter than primary dwell.");
        }

        if (!double.IsFinite(secondaryGlanceProbability)
            || secondaryGlanceProbability < 0d
            || secondaryGlanceProbability > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secondaryGlanceProbability),
                secondaryGlanceProbability,
                "Secondary glance probability must be finite and in the inclusive range 0..1.");
        }

        PrimaryDwellSeconds = primaryDwellSeconds;
        SecondaryDwellSeconds = secondaryDwellSeconds;
        SecondaryGlanceProbability = secondaryGlanceProbability;
    }

    /// <summary>Gets the duration for which a primary target remains stable.</summary>
    public double PrimaryDwellSeconds
    {
        get;
    }

    /// <summary>Gets the duration for which a secondary target remains stable.</summary>
    public double SecondaryDwellSeconds
    {
        get;
    }

    /// <summary>Gets the chance of taking a secondary glance at a primary dwell boundary.</summary>
    public double SecondaryGlanceProbability
    {
        get;
    }

    private static void ValidateFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Dwell duration must be finite and positive.");
        }
    }
}

/// <summary>
/// One adapter-resolved candidate. <see cref="IsValid"/> represents resolution and authored enablement; the policy
/// applies no semantic, visibility, or geometry filters.
/// </summary>
/// <typeparam name="TTarget">Opaque target value that a caller can map to its concrete gaze target.</typeparam>
public sealed record AttentionGazeTargetCandidate<TTarget>(
    string SubjectFullId,
    string CueKey,
    int CueOrder,
    TTarget Target,
    double Attention,
    double Prominence,
    bool IsValid)
    where TTarget : notnull;

/// <summary>Describes the one optional target-assignment action required after a policy evaluation.</summary>
public enum AttentionGazeTargetAction
{
    /// <summary>No target assignment is required.</summary>
    None,

    /// <summary>Assign the accompanying target.</summary>
    SetLookTarget,

    /// <summary>Clear the current target and allow the presentation fallback to take effect.</summary>
    ClearLookTarget,
}

/// <summary>One unambiguous target-assignment decision for an adapter.</summary>
/// <typeparam name="TTarget">Opaque target value supplied by the corresponding candidate.</typeparam>
public readonly record struct AttentionGazeTargetDecision<TTarget>
    where TTarget : notnull
{
    private AttentionGazeTargetDecision(AttentionGazeTargetAction action, TTarget? target)
    {
        Action = action;
        Target = target;
    }

    /// <summary>Gets the required adapter action.</summary>
    public AttentionGazeTargetAction Action
    {
        get;
    }

    /// <summary>Gets the target to assign when <see cref="Action"/> is <see cref="AttentionGazeTargetAction.SetLookTarget"/>.</summary>
    public TTarget? Target
    {
        get;
    }

    internal static AttentionGazeTargetDecision<TTarget> None() => new(AttentionGazeTargetAction.None, default);

    internal static AttentionGazeTargetDecision<TTarget> Set(TTarget target)
        => new(AttentionGazeTargetAction.SetLookTarget, target);

    internal static AttentionGazeTargetDecision<TTarget> Clear()
        => new(AttentionGazeTargetAction.ClearLookTarget, default);
}

/// <summary>
/// Stateful, Mind-independent ranking and dwell policy for attention-derived gaze candidates. Callers may invoke
/// <see cref="Evaluate"/> on a cadence or from a future evaluation request; both use identical dwell state semantics.
/// </summary>
/// <typeparam name="TTarget">Opaque target value that a later adapter maps to a concrete visual cue.</typeparam>
public sealed class AttentionGazeTargetPolicy<TTarget>(
    AttentionGazeTargetSettings settings,
    IAttentionGazeRandom random)
    where TTarget : notnull
{
    private readonly AttentionGazeTargetSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IAttentionGazeRandom _random = random ?? throw new ArgumentNullException(nameof(random));
    private GazeState _state;
    private double _elapsedDwellSeconds;
    private RetainedTarget? _primary;
    private RetainedTarget? _secondary;
    private bool _hasEmittedDecision;
    private bool _hasAssignedTarget;
    private TTarget? _assignedTarget;

    /// <summary>
    /// Advances dwell by <paramref name="deltaSeconds"/> and evaluates current adapter-resolved candidates. A call may
    /// make at most one dwell transition; cadence scheduling and catch-up remain adapter concerns.
    /// </summary>
    public AttentionGazeTargetDecision<TTarget> Evaluate(
        double deltaSeconds,
        IReadOnlyList<AttentionGazeTargetCandidate<TTarget>> candidates)
    {
        ValidateDelta(deltaSeconds);
        ArgumentNullException.ThrowIfNull(candidates);

        List<RankedCandidate> eligible = GetEligibleCandidates(candidates);
        if (eligible.Count == 0)
        {
            ResetDwell();
            return EmitClear();
        }

        _elapsedDwellSeconds += deltaSeconds;
        return _state switch
        {
            GazeState.Primary => EvaluatePrimary(eligible),
            GazeState.Secondary => EvaluateSecondary(eligible),
            GazeState.None => StartPrimary(eligible),
            _ => throw new InvalidOperationException($"Unknown attention gaze state '{_state}'."),
        };
    }

    private AttentionGazeTargetDecision<TTarget> EvaluatePrimary(IReadOnlyList<RankedCandidate> eligible)
    {
        RankedCandidate? primary = FindRetainedCandidate(eligible, _primary);
        if (primary is null)
        {
            return StartPrimary(eligible);
        }

        _primary = RetainedTarget.From(primary.Value);
        if (_elapsedDwellSeconds < _settings.PrimaryDwellSeconds)
        {
            return EmitSet(primary.Value.Target);
        }

        _elapsedDwellSeconds = 0d;
        IReadOnlyList<RankedCandidate> secondaryCandidates = ExcludePrimary(eligible, primary.Value.Identity);
        if (secondaryCandidates.Count > 0 && ShouldTakeSecondaryGlance())
        {
            RankedCandidate secondary = SelectSecondary(secondaryCandidates);
            _secondary = RetainedTarget.From(secondary);
            _state = GazeState.Secondary;
            return EmitSet(secondary.Target);
        }

        return StartPrimary(eligible);
    }

    private AttentionGazeTargetDecision<TTarget> EvaluateSecondary(IReadOnlyList<RankedCandidate> eligible)
    {
        RankedCandidate? secondary = FindRetainedCandidate(eligible, _secondary);
        if (secondary is null)
        {
            return ReturnToPrimaryOrReselect(eligible);
        }

        _secondary = RetainedTarget.From(secondary.Value);
        return _elapsedDwellSeconds < _settings.SecondaryDwellSeconds
            ? EmitSet(secondary.Value.Target)
            : ReturnToPrimaryOrReselect(eligible);
    }

    private AttentionGazeTargetDecision<TTarget> ReturnToPrimaryOrReselect(IReadOnlyList<RankedCandidate> eligible)
    {
        RankedCandidate? primary = FindRetainedCandidate(eligible, _primary);
        if (primary is null)
        {
            return StartPrimary(eligible);
        }

        _primary = RetainedTarget.From(primary.Value);
        _secondary = null;
        _state = GazeState.Primary;
        _elapsedDwellSeconds = 0d;
        return EmitSet(primary.Value.Target);
    }

    private AttentionGazeTargetDecision<TTarget> StartPrimary(IReadOnlyList<RankedCandidate> eligible)
    {
        RankedCandidate primary = eligible[0];
        _primary = RetainedTarget.From(primary);
        _secondary = null;
        _state = GazeState.Primary;
        _elapsedDwellSeconds = 0d;
        return EmitSet(primary.Target);
    }

    private bool ShouldTakeSecondaryGlance()
        => _settings.SecondaryGlanceProbability > 0d
            && (_settings.SecondaryGlanceProbability >= 1d
                || NextUnitInterval() < _settings.SecondaryGlanceProbability);

    private RankedCandidate SelectSecondary(IReadOnlyList<RankedCandidate> candidates)
    {
        double greatestScore = candidates[0].Score;
        if (greatestScore <= 0d)
        {
            // All eligible secondary weights are zero. Primary ranking is the deterministic fallback.
            return candidates[0];
        }

        double totalWeight = 0d;
        foreach (RankedCandidate candidate in candidates)
        {
            totalWeight += candidate.Score / greatestScore;
        }

        double threshold = NextUnitInterval() * totalWeight;
        double cumulativeWeight = 0d;
        RankedCandidate fallback = candidates[0];
        foreach (RankedCandidate candidate in candidates)
        {
            double weight = candidate.Score / greatestScore;
            if (weight <= 0d)
            {
                continue;
            }

            fallback = candidate;
            cumulativeWeight += weight;
            if (threshold < cumulativeWeight)
            {
                return candidate;
            }
        }

        return fallback;
    }

    private double NextUnitInterval()
    {
        double value = _random.NextUnitInterval();
        return !double.IsFinite(value) || value < 0d || value >= 1d
            ? throw new InvalidOperationException(
                $"Attention gaze random source returned '{value}', but samples must be finite and in the half-open interval [0, 1).")
            : value;
    }

    private static List<RankedCandidate> GetEligibleCandidates(
        IReadOnlyList<AttentionGazeTargetCandidate<TTarget>> candidates)
    {
        var eligible = new List<RankedCandidate>(candidates.Count);
        for (int index = 0; index < candidates.Count; index++)
        {
            AttentionGazeTargetCandidate<TTarget> candidate = candidates[index]
                ?? throw new ArgumentException($"Candidate at index {index} cannot be null.", nameof(candidates));
            if (!candidate.IsValid || candidate.Target is null || !double.IsFinite(candidate.Attention)
                || candidate.Attention < 0d || !double.IsFinite(candidate.Prominence) || candidate.Prominence <= 0d)
            {
                continue;
            }

            double score = candidate.Attention * candidate.Prominence;
            if (!double.IsFinite(score))
            {
                continue;
            }

            eligible.Add(new RankedCandidate(
                new CandidateIdentity(candidate.SubjectFullId, candidate.CueKey),
                candidate.CueOrder,
                candidate.Target,
                score,
                index));
        }

        eligible.Sort(RankedCandidateComparer.Instance);
        return eligible;
    }

    private static RankedCandidate? FindRetainedCandidate(
        IReadOnlyList<RankedCandidate> candidates,
        RetainedTarget? retained)
    {
        if (retained is null)
        {
            return null;
        }

        foreach (RankedCandidate candidate in candidates)
        {
            if (candidate.Identity == retained.Identity)
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<RankedCandidate> ExcludePrimary(
        IReadOnlyList<RankedCandidate> candidates,
        CandidateIdentity primaryIdentity)
    {
        var secondaryCandidates = new List<RankedCandidate>(candidates.Count - 1);
        foreach (RankedCandidate candidate in candidates)
        {
            if (candidate.Identity != primaryIdentity)
            {
                secondaryCandidates.Add(candidate);
            }
        }

        return secondaryCandidates;
    }

    private AttentionGazeTargetDecision<TTarget> EmitSet(TTarget target)
    {
        if (_hasAssignedTarget && EqualityComparer<TTarget>.Default.Equals(_assignedTarget, target))
        {
            return AttentionGazeTargetDecision<TTarget>.None();
        }

        _hasEmittedDecision = true;
        _hasAssignedTarget = true;
        _assignedTarget = target;
        return AttentionGazeTargetDecision<TTarget>.Set(target);
    }

    private AttentionGazeTargetDecision<TTarget> EmitClear()
    {
        if (_hasEmittedDecision && !_hasAssignedTarget)
        {
            return AttentionGazeTargetDecision<TTarget>.None();
        }

        _hasEmittedDecision = true;
        _hasAssignedTarget = false;
        _assignedTarget = default;
        return AttentionGazeTargetDecision<TTarget>.Clear();
    }

    private void ResetDwell()
    {
        _state = GazeState.None;
        _elapsedDwellSeconds = 0d;
        _primary = null;
        _secondary = null;
    }

    private static void ValidateDelta(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                deltaSeconds,
                "Evaluation delta must be finite and non-negative.");
        }
    }

    private enum GazeState
    {
        None,
        Primary,
        Secondary,
    }

    private readonly record struct CandidateIdentity(string SubjectFullId, string CueKey);

    private readonly record struct RankedCandidate(
        CandidateIdentity Identity,
        int CueOrder,
        TTarget Target,
        double Score,
        int InputOrder);

    private sealed record RetainedTarget(CandidateIdentity Identity)
    {
        public static RetainedTarget From(RankedCandidate candidate) => new(candidate.Identity);
    }

    private sealed class RankedCandidateComparer : IComparer<RankedCandidate>
    {
        public static RankedCandidateComparer Instance
        {
            get;
        } = new();

        public int Compare(RankedCandidate left, RankedCandidate right)
        {
            int scoreComparison = right.Score.CompareTo(left.Score);
            if (scoreComparison != 0)
            {
                return scoreComparison;
            }

            int subjectComparison = StringComparer.Ordinal.Compare(left.Identity.SubjectFullId, right.Identity.SubjectFullId);
            if (subjectComparison != 0)
            {
                return subjectComparison;
            }

            int cueOrderComparison = left.CueOrder.CompareTo(right.CueOrder);
            return cueOrderComparison != 0 ? cueOrderComparison : left.InputOrder.CompareTo(right.InputOrder);
        }
    }
}
