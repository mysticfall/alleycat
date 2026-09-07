namespace AlleyCat.Control.Locomotion;

#pragma warning disable CS1591

public enum PlayerTravelReleaseSide
{
    Unknown, Left, Right
}

public enum PlayerTravelReleaseEvidenceOrigin
{
    Unknown, LiveEvaluatedClock, PhaseOnly, StaticSchedule, InferredSeam, ContactLifecycle, Cached,
}

public enum PlayerTravelEvaluatedSourceAuthority
{
    Unknown, SingularBlendPoint, AmbiguousBlend, InferredNearest,
}

public enum PlayerTravelClockTransformProvenance
{
    Unknown, CatalogueEventThroughCustomTimeline,
}

public enum PlayerTravelReleaseFreshness
{
    Unknown, CurrentPhysicsTick, Stale, Delayed
}
public enum PlayerTravelReleaseCrossing
{
    Unknown, Crossed, NotCrossed, Missed
}
public enum PlayerTravelReleaseAdmission
{
    Unknown, Admitted, Refused
}
public enum PlayerTravelReleaseConsumption
{
    Unknown, Available, Consumed, Cancelled, Replaced
}

/// <summary>Validated conversion of one authored event into the evaluated custom-timeline phase domain.</summary>
public readonly record struct PlayerTravelReleaseClockTransformV1(
    double AuthoredEventTimeSeconds,
    double AuthoredClipDurationSeconds,
    double EvaluatedCycleSeconds,
    double SeamStartOffsetSeconds,
    double TransformedEventPhase,
    PlayerTravelClockTransformProvenance Provenance)
{
    private const double Tolerance = 0.00001d;

    public static PlayerTravelReleaseClockTransformV1 Create(
        double authoredEventTimeSeconds,
        double authoredClipDurationSeconds,
        double evaluatedCycleSeconds,
        double seamStartOffsetSeconds)
    {
        double transformedPhase = TransformEventPhase(
            authoredEventTimeSeconds,
            authoredClipDurationSeconds,
            evaluatedCycleSeconds,
            seamStartOffsetSeconds);
        return new(
            authoredEventTimeSeconds,
            authoredClipDurationSeconds,
            evaluatedCycleSeconds,
            seamStartOffsetSeconds,
            transformedPhase,
            PlayerTravelClockTransformProvenance.CatalogueEventThroughCustomTimeline);
    }

    public bool IsValid()
    {
        if (Provenance != PlayerTravelClockTransformProvenance.CatalogueEventThroughCustomTimeline
            || !double.IsFinite(AuthoredEventTimeSeconds)
            || !double.IsFinite(AuthoredClipDurationSeconds)
            || !double.IsFinite(EvaluatedCycleSeconds)
            || !double.IsFinite(SeamStartOffsetSeconds)
            || !double.IsFinite(TransformedEventPhase)
            || AuthoredClipDurationSeconds <= 0d
            || EvaluatedCycleSeconds <= 0d
            || AuthoredEventTimeSeconds < 0d
            || AuthoredEventTimeSeconds > AuthoredClipDurationSeconds + Tolerance
            || SeamStartOffsetSeconds < 0d
            || SeamStartOffsetSeconds > EvaluatedCycleSeconds + Tolerance
            || TransformedEventPhase < 0d
            || TransformedEventPhase > 1d)
        {
            return false;
        }

        double expected = TransformEventPhase(
            AuthoredEventTimeSeconds,
            AuthoredClipDurationSeconds,
            EvaluatedCycleSeconds,
            SeamStartOffsetSeconds);
        return Math.Abs(expected - TransformedEventPhase) <= Tolerance;
    }

    private static double TransformEventPhase(
        double eventSeconds,
        double durationSeconds,
        double cycleSeconds,
        double startOffsetSeconds)
    {
        if (!double.IsFinite(eventSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0d
            || !double.IsFinite(cycleSeconds) || cycleSeconds <= 0d || !double.IsFinite(startOffsetSeconds))
        {
            return double.NaN;
        }

        double timelineEventSeconds = eventSeconds * cycleSeconds / durationSeconds;
        double shifted = timelineEventSeconds - startOffsetSeconds;
        if (Math.Abs(shifted - cycleSeconds) <= Tolerance)
        {
            return 1d;
        }

        double wrapped = shifted % cycleSeconds;
        if (wrapped < 0d)
        {
            wrapped += cycleSeconds;
        }
        return Math.Abs(wrapped) <= Tolerance || Math.Abs(wrapped - cycleSeconds) <= Tolerance
            ? 0d
            : wrapped / cycleSeconds;
    }
}

/// <summary>Immutable ANIM-002 schedule bound to one singular live evaluated source. It is not release authority.</summary>
public readonly record struct PlayerTravelReleaseCandidateV1(
    string SourceIdentity,
    string EvaluatedRole,
    string EvaluatedClip,
    string EvaluatedAnimation,
    PlayerTravelEvaluatedSourceAuthority SourceAuthority,
    string ReleaseEvent,
    PlayerTravelReleaseSide AnatomicalSide,
    string ExitRole,
    string ExitClip,
    string CatalogueProvenance,
    PlayerTravelReleaseClockTransformV1 ClockTransform)
{
    public StandingLocomotionCapability EvaluatedCapability
    {
        get; init;
    }
    public StandingLocomotionCapability ExitCapability
    {
        get; init;
    }
}

/// <summary>One live, already-evaluated player travel-clock interval.</summary>
public readonly record struct PlayerTravelClockEvaluationV1(
    string SourceIdentity,
    string EvaluatedRole,
    string EvaluatedClip,
    string EvaluatedAnimation,
    PlayerTravelEvaluatedSourceAuthority SourceAuthority,
    string CatalogueProvenance,
    PlayerTravelReleaseSide AnatomicalSide,
    long PhysicsTick,
    double PreviousPhase,
    double CurrentPhase,
    double EvaluatedCycleSeconds,
    double ExpectedAdvance,
    PlayerTravelReleaseEvidenceOrigin EvidenceOrigin,
    PlayerTravelReleaseFreshness Freshness);

/// <summary>Immutable exact-once authority for CTRL-001's sole travel-to-idle graph hop.</summary>
public readonly record struct PlayerTravelReleaseOccurrenceV1(
    string SourceIdentity,
    string EvaluatedRole,
    string EvaluatedClip,
    string EvaluatedAnimation,
    PlayerTravelEvaluatedSourceAuthority SourceAuthority,
    string ReleaseEvent,
    PlayerTravelReleaseSide AnatomicalSide,
    string ExitRole,
    string ExitClip,
    string CatalogueProvenance,
    PlayerTravelReleaseClockTransformV1 ClockTransform,
    long ClockGeneration,
    long CycleIdentity,
    long OccurrenceIdentity,
    long EvaluationPhysicsTick,
    double PreviousClock,
    double CurrentClock,
    double AuthoredEventClock,
    PlayerTravelReleaseEvidenceOrigin EvidenceOrigin,
    PlayerTravelReleaseFreshness Freshness,
    PlayerTravelReleaseCrossing Crossing,
    PlayerTravelReleaseAdmission Admission,
    PlayerTravelReleaseConsumption Consumption)
{
    public StandingLocomotionCapability EvaluatedCapability
    {
        get; init;
    }
}

public readonly struct PlayerTravelReleasePermitV1
{
    internal PlayerTravelReleasePermitV1(PlayerTravelReleaseOccurrenceV1 occurrence)
    {
        Occurrence = occurrence;
        IsIssued = true;
    }

    public PlayerTravelReleaseOccurrenceV1 Occurrence
    {
        get;
    }
    internal bool IsIssued
    {
        get;
    }
}

/// <summary>Publishes only a fresh interval crossing the validated transformed authored event.</summary>
public sealed class PlayerTravelReleaseGate
{
    private const double Tolerance = 0.00001d;
    private long _nextOccurrenceIdentity = 1;
    private bool _hasEvaluation;
    private double _currentClock;
    private double _nextEventClock;
    private long _lastTick;
    private PlayerTravelReleaseCandidateV1 _candidate;
    private PlayerTravelReleaseOccurrenceV1? _pending;

    public long ClockGeneration { get; private set; } = 1;

    public PlayerTravelReleaseOccurrenceV1? Observe(
        in PlayerTravelReleaseCandidateV1 candidate,
        in PlayerTravelClockEvaluationV1 evaluation)
    {
        if (!IsCandidateValid(candidate) || !IsEvaluationValid(evaluation) || !Matches(candidate, evaluation))
        {
            Reset();
            return null;
        }

        if (_hasEvaluation && candidate != _candidate)
        {
            Reset();
        }

        if (!_hasEvaluation)
        {
            _candidate = candidate;
            _currentClock = Normalise(evaluation.CurrentPhase);
            _nextEventClock = NextEventClock(_currentClock, candidate.ClockTransform.TransformedEventPhase);
            _lastTick = evaluation.PhysicsTick;
            _hasEvaluation = true;
            return null;
        }

        if (evaluation.PhysicsTick != _lastTick + 1
            || CircularDistance(Normalise(evaluation.PreviousPhase), Normalise(_currentClock)) > Tolerance)
        {
            Reset();
            return null;
        }

        double previous = _currentClock;
        double advance = ForwardDistance(evaluation.PreviousPhase, evaluation.CurrentPhase);
        if (advance <= Tolerance || advance > evaluation.ExpectedAdvance + Tolerance || advance >= 1d)
        {
            Reset();
            return null;
        }

        double current = previous + advance;
        _currentClock = current;
        _lastTick = evaluation.PhysicsTick;
        ExpirePending();
        if (_nextEventClock > current + Tolerance)
        {
            return null;
        }
        if (!(_nextEventClock > previous + Tolerance && _nextEventClock <= current + Tolerance))
        {
            Reset();
            return null;
        }

        double eventClock = _nextEventClock;
        long cycleIdentity = Math.Max((long)Math.Floor(eventClock - Tolerance) + 1L, 1L);
        _nextEventClock += 1d;
        ReplacePending();
        PlayerTravelReleaseOccurrenceV1 occurrence = new(
            candidate.SourceIdentity,
            candidate.EvaluatedRole,
            candidate.EvaluatedClip,
            candidate.EvaluatedAnimation,
            candidate.SourceAuthority,
            candidate.ReleaseEvent,
            candidate.AnatomicalSide,
            candidate.ExitRole,
            candidate.ExitClip,
            candidate.CatalogueProvenance,
            candidate.ClockTransform,
            ClockGeneration,
            cycleIdentity,
            _nextOccurrenceIdentity++,
            evaluation.PhysicsTick,
            previous,
            current,
            eventClock,
            evaluation.EvidenceOrigin,
            evaluation.Freshness,
            PlayerTravelReleaseCrossing.Crossed,
            PlayerTravelReleaseAdmission.Admitted,
            PlayerTravelReleaseConsumption.Available)
        {
            EvaluatedCapability = candidate.EvaluatedCapability,
        };
        _pending = occurrence;
        return occurrence;
    }

    public bool TryConsume(
        in PlayerTravelReleaseOccurrenceV1 occurrence,
        long physicsTick,
        PlayerTravelReleaseSide expectedSide,
        out PlayerTravelReleasePermitV1 permit)
    {
        permit = default;
        if (_pending is not { } pending
            || occurrence != pending
            || physicsTick != _lastTick
            || occurrence.EvaluationPhysicsTick != physicsTick
            || occurrence.ClockGeneration != ClockGeneration
            || occurrence.AnatomicalSide != expectedSide
            || occurrence.SourceAuthority != PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint
            || occurrence.EvidenceOrigin != PlayerTravelReleaseEvidenceOrigin.LiveEvaluatedClock
            || occurrence.Freshness != PlayerTravelReleaseFreshness.CurrentPhysicsTick
            || occurrence.Crossing != PlayerTravelReleaseCrossing.Crossed
            || occurrence.Admission != PlayerTravelReleaseAdmission.Admitted
            || occurrence.Consumption != PlayerTravelReleaseConsumption.Available
            || !IsOccurrenceSchemaValid(occurrence)
            || occurrence.PreviousClock >= occurrence.CurrentClock
            || !(occurrence.AuthoredEventClock > occurrence.PreviousClock + Tolerance
                && occurrence.AuthoredEventClock <= occurrence.CurrentClock + Tolerance))
        {
            return false;
        }

        permit = new(occurrence with
        {
            Consumption = PlayerTravelReleaseConsumption.Consumed
        });
        _pending = null;
        return true;
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        _pending = null;
        _hasEvaluation = false;
        _currentClock = 0d;
        _nextEventClock = 0d;
        _lastTick = 0;
        _candidate = default;
        ClockGeneration = Math.Max(ClockGeneration + 1, 1);
    }

    private void ExpirePending()
    {
        if (_pending is not null && _pending.Value.EvaluationPhysicsTick < _lastTick)
        {
            _pending = null;
        }
    }

    private void ReplacePending() => _pending = null;

    private static bool IsCandidateValid(in PlayerTravelReleaseCandidateV1 value)
        => Present(value.SourceIdentity)
            && value.EvaluatedCapability is StandingLocomotionCapability.CyclicForwardPrimary
                or StandingLocomotionCapability.CyclicBackwardPrimary
            && Present(value.EvaluatedClip)
            && Present(value.EvaluatedAnimation)
            && value.SourceAuthority == PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint
            && Present(value.ReleaseEvent)
            && value.AnatomicalSide is PlayerTravelReleaseSide.Left or PlayerTravelReleaseSide.Right
            && Present(value.ExitRole)
            && Present(value.ExitClip)
            && Present(value.CatalogueProvenance)
            && value.ExitCapability == StandingLocomotionCapabilities.Exit(
                value.EvaluatedCapability.IsBackward(),
                value.AnatomicalSide == PlayerTravelReleaseSide.Left
                    ? StandingLocomotionAnatomicalSide.Left
                    : StandingLocomotionAnatomicalSide.Right)
            && value.ClockTransform.IsValid();

    private static bool IsEvaluationValid(in PlayerTravelClockEvaluationV1 value)
        => Present(value.SourceIdentity)
            && Present(value.EvaluatedRole)
            && Present(value.EvaluatedClip)
            && Present(value.EvaluatedAnimation)
            && value.SourceAuthority == PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint
            && Present(value.CatalogueProvenance)
            && value.AnatomicalSide is PlayerTravelReleaseSide.Left or PlayerTravelReleaseSide.Right
            && value.PhysicsTick > 0
            && double.IsFinite(value.PreviousPhase)
            && double.IsFinite(value.CurrentPhase)
            && double.IsFinite(value.EvaluatedCycleSeconds)
            && double.IsFinite(value.ExpectedAdvance)
            && value.PreviousPhase >= 0d && value.PreviousPhase < 1d
            && value.CurrentPhase >= 0d && value.CurrentPhase < 1d
            && value.EvaluatedCycleSeconds > 0d
            && value.ExpectedAdvance > 0d && value.ExpectedAdvance < 1d
            && value.EvidenceOrigin == PlayerTravelReleaseEvidenceOrigin.LiveEvaluatedClock
            && value.Freshness == PlayerTravelReleaseFreshness.CurrentPhysicsTick;

    private static bool Matches(in PlayerTravelReleaseCandidateV1 candidate, in PlayerTravelClockEvaluationV1 evaluation)
        => candidate.SourceIdentity == evaluation.SourceIdentity
            && candidate.EvaluatedRole == evaluation.EvaluatedRole
            && candidate.EvaluatedClip == evaluation.EvaluatedClip
            && candidate.EvaluatedAnimation == evaluation.EvaluatedAnimation
            && candidate.SourceAuthority == evaluation.SourceAuthority
            && candidate.CatalogueProvenance == evaluation.CatalogueProvenance
            && candidate.AnatomicalSide == evaluation.AnatomicalSide
            && Math.Abs(candidate.ClockTransform.EvaluatedCycleSeconds - evaluation.EvaluatedCycleSeconds) <= Tolerance;

    private static bool IsOccurrenceSchemaValid(in PlayerTravelReleaseOccurrenceV1 value)
        => Present(value.SourceIdentity) && Present(value.EvaluatedRole) && Present(value.EvaluatedClip)
            && Present(value.EvaluatedAnimation)
            && value.SourceAuthority == PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint
            && Present(value.ReleaseEvent)
            && Present(value.ExitRole) && Present(value.ExitClip) && Present(value.CatalogueProvenance)
            && value.ClockTransform.IsValid()
            && value.ClockGeneration > 0 && value.CycleIdentity > 0 && value.OccurrenceIdentity > 0
            && value.EvaluationPhysicsTick > 0
            && double.IsFinite(value.PreviousClock) && double.IsFinite(value.CurrentClock)
            && double.IsFinite(value.AuthoredEventClock)
            && EventClockMatchesTransform(value.AuthoredEventClock, value.ClockTransform.TransformedEventPhase);

    private static bool EventClockMatchesTransform(double eventClock, double transformedPhase)
    {
        double phase = Normalise(eventClock);
        double expected = transformedPhase == 1d ? 0d : transformedPhase;
        return CircularDistance(phase, expected) <= Tolerance;
    }

    private static double NextEventClock(double currentClock, double transformedPhase)
    {
        double phase = transformedPhase == 1d ? 1d : transformedPhase;
        double candidate = Math.Floor(currentClock) + phase;
        return candidate <= currentClock + Tolerance ? candidate + 1d : candidate;
    }

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
    private static double Normalise(double phase) => phase - Math.Floor(phase);
    private static double ForwardDistance(double previous, double current)
    {
        double distance = Normalise(current) - Normalise(previous);
        return distance < 0d ? distance + 1d : distance;
    }

    private static double CircularDistance(double left, double right)
    {
        double distance = Math.Abs(left - right);
        return Math.Min(distance, 1d - distance);
    }
}
