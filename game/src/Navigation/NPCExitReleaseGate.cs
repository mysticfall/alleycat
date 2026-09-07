using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

public readonly record struct NPCExitReleasePlan(
    string ExitRole,
    NPCAnimationPhaseLane Lane,
    string ReleaseEvent,
    float TargetPhase,
    float TimeToReleaseSeconds);

/// <summary>One engine-evaluated observation of the looping sustained child timeline.</summary>
public readonly record struct NPCSustainedCycleObservation(
    bool IsAvailable,
    bool WasArmedForPreviousEvaluation,
    float PreviousPhase,
    float CurrentPhase,
    float CycleSeconds,
    float PlaybackRate,
    float FrameDeltaSeconds,
    long ClockGeneration = 0,
    double PreviousUnwrappedPhase = double.NaN,
    double CurrentUnwrappedPhase = double.NaN);

/// <summary>
/// Stable identity for one recurrence of an authored release event on the engine-observed sustained timeline.
/// </summary>
public readonly record struct NPCReleaseOccurrence(
    long ClockGeneration,
    long CycleOrdinal,
    float TargetPhase,
    NPCAnimationPhaseLane Lane,
    string ReleaseEvent)
{
    public double UnwrappedPhase => CycleOrdinal + Mathf.PosMod(TargetPhase, 1f);

    public bool IsValid => ClockGeneration > 0
        && CycleOrdinal >= 0
        && float.IsFinite(TargetPhase)
        && !string.IsNullOrWhiteSpace(ReleaseEvent);
}

public enum NPCReleaseOccurrenceCrossingStatus
{
    Invalid,
    Pending,
    Crossed,
    Missed,
}

/// <summary>Monotonic view of the engine-evaluated looping sustained child.</summary>
public readonly record struct NPCSustainedCycleClock(
    bool IsAvailable,
    long Generation,
    double PreviousUnwrappedPhase,
    double CurrentUnwrappedPhase)
{
    public static NPCSustainedCycleClock Reset(in NPCSustainedCycleClock clock)
        => new(false, Math.Max(clock.Generation + 1, 1), 0d, 0d);

    public static NPCSustainedCycleClock Observe(
        in NPCSustainedCycleClock clock,
        float phase,
        float expectedAdvance)
    {
        if (!float.IsFinite(phase) || !float.IsFinite(expectedAdvance) || expectedAdvance < 0f)
        {
            return Reset(clock);
        }

        double normalised = Mathf.PosMod(phase, 1f);
        if (!clock.IsAvailable)
        {
            long generation = Math.Max(clock.Generation, 1);
            return new(true, generation, normalised, normalised);
        }

        double previous = clock.CurrentUnwrappedPhase;
        double previousPhase = previous - Math.Floor(previous);
        double wrappedAdvance = normalised - previousPhase;
        if (wrappedAdvance < 0d)
        {
            wrappedAdvance += 1d;
        }

        // The child phase is authoritative for the fractional advance. The rate-driven expected advance only
        // disambiguates whole loops that a wrapped engine position cannot represent on a large frame.
        long wholeCycles = Math.Max((long)Math.Round(expectedAdvance - wrappedAdvance), 0L);
        double current = previous + wrappedAdvance + wholeCycles;
        return new(true, clock.Generation, previous, current);
    }

    /// <summary>
    /// Advances phase identity only after the AnimationTree has evaluated the sustained child. Publishing a travel
    /// request precedes its first graph evaluation by one producer/consumer interval and is not a phase observation.
    /// </summary>
    public static NPCSustainedCycleClock ObserveEvaluatedTravel(
        in NPCSustainedCycleClock clock,
        bool sustainedStateEvaluated,
        float phase,
        float expectedAdvance)
    {
        if (sustainedStateEvaluated)
        {
            return Observe(clock, phase, expectedAdvance);
        }

        // Repeated pre-live frames retain the pending generation. Leaving a live sustained state creates a fresh
        // identity, so an armed occurrence cannot cross a later, unrelated travel state.
        return clock.IsAvailable ? Reset(clock) : clock;
    }
}

/// <summary>Pure shared-timeline release planning and execution gate for an armed NPC exit.</summary>
public static class NPCExitReleaseGate
{
    public const float DefaultPhaseTolerance = 0.00001f;

    public static NPCExitReleasePlan Plan(
        string exitRole,
        string phaseEvent,
        NPCAnimationPhaseLane lane,
        NPCAnimationPhaseLane sustainedLane,
        float sharedPhase,
        float cyclicLengthSeconds,
        float playbackRate,
        float oppositeEventPhase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phaseEvent);
        float targetPhase = lane == sustainedLane ? 0f : NormalisePhase(oppositeEventPhase);
        return new(
            exitRole,
            lane,
            phaseEvent,
            targetPhase,
            TimeToNextRelease(sharedPhase, targetPhase, cyclicLengthSeconds, playbackRate));
    }

    public static float TimeToNextRelease(
        float sharedPhase,
        float targetPhase,
        float cyclicLengthSeconds,
        float playbackRate)
    {
        float phase = NormalisePhase(sharedPhase);
        float target = NormalisePhase(targetPhase);
        float length = SafePositive(cyclicLengthSeconds);
        float rate = SafePositive(playbackRate);
        float phaseDistance = Mathf.PosMod(target - phase, 1f);
        return CircularPhaseError(phase, target) <= DefaultPhaseTolerance
            ? 0f
            : phaseDistance * length / rate;
    }

    public static bool IsAtRelease(float sharedPhase, float targetPhase, float tolerance = DefaultPhaseTolerance)
        => CircularPhaseError(sharedPhase, targetPhase) <= Math.Max(tolerance, 0f);

    /// <summary>
    /// Returns whether the already-evaluated looping child crossed the target while armed, or whether the next
    /// graph evaluation at the currently published rate reaches it. The latter is the publication frame on which
    /// the state-machine transition must be requested.
    /// </summary>
    public static bool ReachesReleaseThisFrame(
        in NPCSustainedCycleObservation observation,
        float targetPhase,
        bool alreadyExecuted = false)
    {
        if (alreadyExecuted || !IsValid(observation) || !float.IsFinite(targetPhase))
        {
            return false;
        }

        float previous = NormalisePhase(observation.PreviousPhase);
        float current = NormalisePhase(observation.CurrentPhase);
        float target = NormalisePhase(targetPhase);
        float expectedAdvance = ExpectedFrameAdvance(observation);
        if (expectedAdvance >= 1f || CircularPhaseError(current, target) <= DefaultPhaseTolerance)
        {
            return true;
        }

        float currentToTarget = ForwardDistance(current, target);
        if (currentToTarget <= expectedAdvance + DefaultPhaseTolerance)
        {
            return true;
        }

        if (!observation.WasArmedForPreviousEvaluation)
        {
            return false;
        }

        float observedAdvance = ForwardDistance(previous, current);
        float previousToTarget = ForwardDistance(previous, target);
        return observedAdvance > DefaultPhaseTolerance
            && previousToTarget <= observedAdvance + DefaultPhaseTolerance;
    }

    public static NPCReleaseOccurrence SelectOccurrence(
        long clockGeneration,
        double currentUnwrappedPhase,
        float targetPhase,
        NPCAnimationPhaseLane lane,
        string releaseEvent,
        int additionalCycles = 0)
    {
        if (clockGeneration <= 0
            || !double.IsFinite(currentUnwrappedPhase)
            || currentUnwrappedPhase < 0d
            || !float.IsFinite(targetPhase)
            || string.IsNullOrWhiteSpace(releaseEvent)
            || additionalCycles < 0)
        {
            return default;
        }

        float target = NormalisePhase(targetPhase);
        long ordinal = (long)Math.Floor(currentUnwrappedPhase);
        double candidate = ordinal + target;
        if (candidate < currentUnwrappedPhase - DefaultPhaseTolerance)
        {
            ordinal++;
        }

        return new(clockGeneration, ordinal + additionalCycles, target, lane, releaseEvent);
    }

    /// <summary>
    /// Classifies the selected occurrence against the observed and next-consumed forward intervals. Earlier
    /// same-phase recurrences cannot satisfy this check because the occurrence's cycle ordinal is retained.
    /// </summary>
    public static NPCReleaseOccurrenceCrossingStatus OccurrenceCrossingStatus(
        in NPCSustainedCycleObservation observation,
        in NPCReleaseOccurrence occurrence,
        bool alreadyExecuted = false)
    {
        if (alreadyExecuted
            || !IsValid(observation)
            || !occurrence.IsValid
            || observation.ClockGeneration != occurrence.ClockGeneration
            || !double.IsFinite(observation.PreviousUnwrappedPhase)
            || !double.IsFinite(observation.CurrentUnwrappedPhase))
        {
            return NPCReleaseOccurrenceCrossingStatus.Invalid;
        }

        double target = occurrence.UnwrappedPhase;
        double previous = observation.PreviousUnwrappedPhase;
        double current = observation.CurrentUnwrappedPhase;
        double tolerance = DefaultPhaseTolerance;
        if (target < previous - tolerance)
        {
            return NPCReleaseOccurrenceCrossingStatus.Missed;
        }

        if (observation.WasArmedForPreviousEvaluation
            && current > previous + tolerance
            && target >= previous - tolerance
            && target <= current + tolerance)
        {
            return NPCReleaseOccurrenceCrossingStatus.Crossed;
        }

        double next = current + ExpectedFrameAdvance(observation);
        return target >= current - tolerance && target <= next + tolerance
            ? NPCReleaseOccurrenceCrossingStatus.Crossed
            : target < current - tolerance
                ? NPCReleaseOccurrenceCrossingStatus.Missed
                : NPCReleaseOccurrenceCrossingStatus.Pending;
    }

    public static float ExpectedFrameAdvance(in NPCSustainedCycleObservation observation)
        => IsValid(observation)
            ? observation.PlaybackRate * observation.FrameDeltaSeconds / observation.CycleSeconds
            : 0f;

    public static float CircularPhaseError(float sharedPhase, float targetPhase)
    {
        float difference = Mathf.Abs(NormalisePhase(sharedPhase) - NormalisePhase(targetPhase));
        return Math.Min(difference, 1f - difference);
    }

    /// <summary>
    /// Maps a packaged opposite release event into the shared normalised phase established by the current release.
    /// </summary>
    public static float ResolveOppositeTargetPhase(
        double currentEventTimeSeconds,
        double oppositeEventTimeSeconds,
        double cycleDurationSeconds)
    {
        return !double.IsFinite(currentEventTimeSeconds)
            || !double.IsFinite(oppositeEventTimeSeconds)
            || !double.IsFinite(cycleDurationSeconds)
            || cycleDurationSeconds <= 0d
            ? throw new ArgumentOutOfRangeException(
                nameof(cycleDurationSeconds),
                "Packaged release evidence requires finite event times and a positive cycle duration.")
            : NormalisePhase((float)((oppositeEventTimeSeconds - currentEventTimeSeconds) / cycleDurationSeconds));
    }

    private static float NormalisePhase(float phase)
        => float.IsFinite(phase) ? Mathf.PosMod(phase, 1f) : 0f;

    private static float ForwardDistance(float from, float to)
        => Mathf.PosMod(to - from, 1f);

    private static bool IsValid(in NPCSustainedCycleObservation observation)
        => observation.IsAvailable
            && float.IsFinite(observation.PreviousPhase)
            && float.IsFinite(observation.CurrentPhase)
            && float.IsFinite(observation.CycleSeconds)
            && observation.CycleSeconds > 0f
            && float.IsFinite(observation.PlaybackRate)
            && observation.PlaybackRate > 0f
            && float.IsFinite(observation.FrameDeltaSeconds)
            && observation.FrameDeltaSeconds > 0f;

    private static float SafePositive(float value)
        => float.IsFinite(value) && value > 0f ? value : float.Epsilon;
}
