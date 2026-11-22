using System.Globalization;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>Who armed the wait's current deadline.</summary>
internal enum DecisionWaitDeadlineSource
{
    /// <summary>The agent's explicit <c>wait(seconds)</c> request seeded the deadline.</summary>
    Explicit,

    /// <summary>A deferred decision verdict armed the deadline.</summary>
    Decision,
}

/// <summary>How one decision verdict changed the wait's schedule.</summary>
internal enum DecisionWaitScheduleStepKind
{
    /// <summary>An immediate verdict: the agent starts now, whatever deadline is armed.</summary>
    ActNow,

    /// <summary>The verdict armed a deadline, or replaced a later one with its sooner deadline.</summary>
    Armed,

    /// <summary>The verdict's deadline was not sooner than the armed one, which stays unchanged.</summary>
    Kept,

    /// <summary>
    /// The evaluation failed: for timing it counts as a no-reason deferral — the jittered no-reason timeout is
    /// armed when nothing is armed or it is sooner, otherwise the armed deadline stays — so the agent always wakes
    /// eventually, never immediately and never later than an armed deadline.
    /// </summary>
    Failure,
}

/// <summary>One schedule change with the timing facts the decision-payload dump records.</summary>
/// <param name="Kind">How the verdict changed the schedule.</param>
/// <param name="Arm">The deferred verdict's arm when the verdict deferred, or <see cref="DecisionWaitArm.NoReason" />
/// for a failure.</param>
/// <param name="RequestedSeconds">The verdict's (or failure's) jittered timeout, when it armed or tried to arm one.</param>
/// <param name="RemainingSeconds">Seconds until the armed deadline after this step, or null when none is armed or
/// the agent starts now.</param>
/// <param name="Source">Who armed the deadline in effect after this step, or null when none is armed.</param>
internal readonly record struct DecisionWaitScheduleStep(
    DecisionWaitScheduleStepKind Kind,
    DecisionWaitArm? Arm,
    double? RequestedSeconds,
    double? RemainingSeconds,
    DecisionWaitDeadlineSource? Source)
{
    /// <summary>
    /// The dump filename verdict token: <c>now</c>, <c>arm-5.2s</c> (deadline armed or shortened to this many
    /// seconds), <c>keep-3.1s</c> (the existing deadline stays, firing in this many seconds; <c>keep</c> when none
    /// is armed), or <c>failed</c>.
    /// </summary>
    public string Token => Kind switch
    {
        DecisionWaitScheduleStepKind.ActNow => "now",
        DecisionWaitScheduleStepKind.Armed => $"arm-{FormatSeconds(RemainingSeconds)}s",
        DecisionWaitScheduleStepKind.Kept when RemainingSeconds is not null => $"keep-{FormatSeconds(RemainingSeconds)}s",
        DecisionWaitScheduleStepKind.Kept => "keep",
        DecisionWaitScheduleStepKind.Failure => "failed",
        _ => "failed",
    };

    private static string FormatSeconds(double? seconds)
        => (seconds ?? 0d).ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// One wait's deadline under the two-question wait gate's timer rules: an immediate verdict starts the agent
/// whatever is armed; a deferred verdict arms its jittered timeout only when no deadline is armed or its deadline
/// is sooner — an armed deadline is never extended or reset; a failed evaluation arms the no-reason timeout under
/// the same rules; an explicit <c>wait(seconds)</c> seeds the initial deadline under the same rules. The schedule lives for one wait only, so every deadline clears when the agent
/// loop starts.
/// </summary>
/// <remarks>Pure and clock-agnostic: callers pass a monotonic "now" in seconds; jitter comes from the injected unit
/// sampler so tests can make it deterministic.</remarks>
internal sealed class DecisionWaitSchedule
{
    private readonly DecisionWaitTimingSettings _timing;
    private readonly Func<double> _jitterSampler;

    public DecisionWaitSchedule(DecisionWaitTimingSettings timing, Func<double> jitterSampler)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(jitterSampler);
        _timing = timing;
        _jitterSampler = jitterSampler;
    }

    /// <summary>The armed deadline on the caller's monotonic clock, or null when none is armed.</summary>
    public double? DeadlineAt
    {
        get; private set;
    }

    /// <summary>Who armed the current deadline, or null when none is armed.</summary>
    public DecisionWaitDeadlineSource? Source
    {
        get; private set;
    }

    /// <summary>Seeds the initial deadline from an explicit <c>wait(seconds)</c> request, without jitter.</summary>
    public void SeedExplicit(double nowSeconds, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "An explicit wait must be finite and positive.");
        }

        if (DeadlineAt is not null)
        {
            throw new InvalidOperationException("An explicit wait seeds only the initial deadline.");
        }

        DeadlineAt = nowSeconds + seconds;
        Source = DecisionWaitDeadlineSource.Explicit;
    }

    /// <summary>Seconds until the armed deadline (never negative), or null when none is armed.</summary>
    public double? RemainingSeconds(double nowSeconds)
        => DeadlineAt is { } deadline ? Math.Max(0d, deadline - nowSeconds) : null;

    /// <summary>Whether an armed deadline has been reached.</summary>
    public bool IsDue(double nowSeconds) => DeadlineAt is { } deadline && nowSeconds >= deadline;

    /// <summary>Applies one resolved decision under the timer rules.</summary>
    public DecisionWaitScheduleStep Apply(DecisionWaitDecision decision, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(decision);
        switch (decision.Kind)
        {
            case DecisionWaitDecisionKind.ActNow:
                return new DecisionWaitScheduleStep(DecisionWaitScheduleStepKind.ActNow, null, null, null, null);
            case DecisionWaitDecisionKind.Failure:
                // A failure times like a no-reason deferral, so a parked wait never stalls on a failing service.
                (DecisionWaitArm failureArm, double failureRequested, _) =
                    ArmShortenOnly(DecisionWaitArm.NoReason, nowSeconds);
                return new DecisionWaitScheduleStep(
                    DecisionWaitScheduleStepKind.Failure,
                    failureArm,
                    failureRequested,
                    RemainingSeconds(nowSeconds),
                    Source);
            case DecisionWaitDecisionKind.Defer:
                DecisionWaitArm arm = decision.Arm
                    ?? throw new InvalidOperationException("A deferred wait decision requires an arm.");
                (_, double requested, bool armed) = ArmShortenOnly(arm, nowSeconds);
                return new DecisionWaitScheduleStep(
                    armed ? DecisionWaitScheduleStepKind.Armed : DecisionWaitScheduleStepKind.Kept,
                    arm,
                    requested,
                    RemainingSeconds(nowSeconds),
                    Source);
            default:
                throw new InvalidOperationException($"Unsupported wait decision '{decision}'.");
        }
    }

    /// <summary>
    /// Samples the arm's jittered timeout and arms it only when nothing is armed or it is sooner than the armed
    /// deadline; never extends or resets.
    /// </summary>
    private (DecisionWaitArm Arm, double Requested, bool Armed) ArmShortenOnly(DecisionWaitArm arm, double nowSeconds)
    {
        double requested = _timing.SampleSeconds(arm, _jitterSampler());
        double candidate = nowSeconds + requested;
        if (DeadlineAt is { } current && current <= candidate)
        {
            return (arm, requested, false);
        }

        DeadlineAt = candidate;
        Source = DecisionWaitDeadlineSource.Decision;
        return (arm, requested, true);
    }
}
