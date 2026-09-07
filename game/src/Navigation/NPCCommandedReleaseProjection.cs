using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>
/// Immutable, engine-free input snapshot for one bounded imminent candidate projection. The projection integrates,
/// per physics tick, exactly the sustained commands the runtime will issue until the next selected release
/// occurrence: blend pin convergence, ordinary cadence, and yaw rotation toward the exit start pose.
/// </summary>
public readonly record struct NPCCommandedReleaseProjectionRequest(
    Vector2 CurrentBlend,
    Vector2 BlendAcceleration,
    Vector2 PinBlend,
    float Phase,
    float PlaybackRate,
    float CyclicLengthSeconds,
    float TargetPhase,
    float ActorYaw,
    float ExitStartYaw,
    float DeltaSeconds,
    bool RequireBlendConvergenceBeforeRelease = true,
    float PlaybackRateAcceleration = 0f,
    float OrdinaryPlaybackRate = NPCFiniteTransitionCadence.SourceSecondsPerWallSecond,
    double CurrentUnwrappedPhase = double.NaN,
    NPCReleaseOccurrence SelectedOccurrence = default,
    float MovingYawTarget = float.NaN,
    float TerminalYawTarget = float.NaN,
    float RequestedYawRate = float.NaN,
    float InitialAuthoredYawRate = 0f,
    float YawSpeedWeight = 1f,
    float YawPhaseWeight = 1f,
    float MaximumYawResidualRate = NPCTerminalReleaseScheduler.MaximumYawRate,
    float YawResidualSmoothingRate = 10f,
    NPCYawCommandState YawCommandState = default,
    bool TerminalYawGuardByDistance = false,
    float InitialAppliedYawRate = 0f);

/// <summary>Projected authored sustained response at one sample.</summary>
public readonly record struct NPCCommandedReleaseAuthoredSample(Vector2 SemanticVelocity, float YawRate);

/// <summary>Authoritative value-only Root response for a complete projected mixer interval.</summary>
public readonly record struct NPCCommandedReleaseAuthoredIntervalSample(
    Vector2 SemanticRoot,
    float AuthoredYawRadians);

/// <summary>Result of one bounded imminent candidate projection. Small and value-only for assertions.</summary>
public readonly record struct NPCCommandedReleaseProjectionResult(
    bool BlendConverged,
    float ConvergenceSeconds,
    bool Resolved,
    float TimeToReleaseSeconds,
    float TargetPhase,
    Vector2 SustainedRootUntilReleaseWorld,
    Vector2 BlendPointAtRelease,
    float PhaseAtRelease,
    float PlaybackRateAtRelease,
    float YawAtRelease,
    int ProjectedTicks,
    NPCReleaseOccurrence SelectedOccurrence = default)
{
    public static NPCCommandedReleaseProjectionResult Unresolved => default;
}

/// <summary>Exact value-only evidence for one projected <c>(previous, current]</c> lead-in interval.</summary>
public readonly record struct NPCCommandedReleaseProjectionInterval(
    int IntervalIndex,
    float DeltaSeconds,
    Vector2 AuthoredRootWorld,
    float AuthoredYawRadians,
    Vector2 BlendPoint);

/// <summary>
/// Pure per-tick projection of what the runtime publisher will actually command between now and the next selected
/// release occurrence. The projection integrates, per physics tick, exactly the commands the runtime issues during
/// the imminent lead-in:
/// <list type="bullet">
/// <item>blend pin convergence via <see cref="NPCTransitionContinuity.StepVelocityCommand" /> at the release
/// approach convergence limits (4 m/s², 20 m/s³), mirroring the release-approach publication;</item>
/// <item>ordinary sustained cadence, resolving on the first consumed forward interval containing the selected
/// release occurrence;</item>
/// <item>yaw rotation toward the exit start yaw at the runtime clamp (±<see cref="NPCTerminalReleaseScheduler.MaximumYawRate" />
/// over the remaining time), with semantic velocity integrated in the progressively rotated frame;</item>
/// <item>sustained semantic velocity sampled at the PROJECTED blend point and phase — never a frozen arm-time
/// blend (the frozen snapshot under-predicted sustained-until-release travel 3.25× on the defect route).</item>
/// </list>
/// The release beat is the first lane beat at or after blend-convergence completion. Invalid occurrences never
/// bypass the release gate. The caller bounds the projection with the candidate's own imminent tick horizon; there
/// is no route-length-derived search horizon.
/// </summary>
public static class NPCCommandedReleaseProjection
{
    /// <summary>Release-approach blend convergence limits, mirroring the release-approach publication.</summary>
    public const float BlendConvergenceAcceleration = 4f;

    public const float BlendConvergenceJerk = 20f;

    /// <summary>
    /// Playback-rate smoothing limits, mirroring the runtime rate governor's <see cref="StepPlaybackRate" />. The
    /// projection models the same bounded rate response as the publisher so their phase advances remain aligned.
    /// </summary>
    public const float PlaybackRateSmoothingAcceleration = 2f;

    public const float PlaybackRateSmoothingJerk = 10f;

    /// <summary>
    /// Number of ticks inside which the projected publisher aligns directly with ordinary sustained cadence.
    /// </summary>
    public const float PublisherRateAlignmentWindowTicks = 3f;

    public const float BlendConvergenceTolerance = 0.000001f;

    /// <summary>
    /// Upper bound on modelled blend-convergence time. A blend pinned from any authored blend-space coordinate at
    /// 4 m/s² and 20 m/s³ converges well inside this window; beyond it the projection reports unresolved.
    /// </summary>
    public const float MaximumConvergenceSeconds = 1.5f;

    /// <summary>
    /// Fast-path radius of the exit-seam release gate, in blend-space units: a sustained blend already within this
    /// distance of the walk-family pin is treated as at the point without consulting the projection.
    /// </summary>
    public const float ExitSeamAtPinRadius = 0.15f;

    /// <summary>
    /// Runs one bounded projection without retaining per-interval evidence. The caller supplies the candidate's
    /// imminent tick horizon.
    /// </summary>
    public static NPCCommandedReleaseProjectionResult Project(
        NPCCommandedReleaseProjectionRequest request,
        Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample> sustainedIntervalSample,
        int maximumTicks)
        => ProjectCore(request, sustainedIntervalSample, null, maximumTicks);

    /// <summary>
    /// Runs one bounded projection while retaining every exact lead-in interval in caller-owned reusable storage.
    /// This is the allocation-free capture path for the bounded imminent candidate check.
    /// </summary>
    public static NPCCommandedReleaseProjectionResult Project(
        NPCCommandedReleaseProjectionRequest request,
        Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample> sustainedIntervalSample,
        List<NPCCommandedReleaseProjectionInterval> intervalCapture,
        int maximumTicks)
    {
        ArgumentNullException.ThrowIfNull(intervalCapture);
        intervalCapture.Clear();
        return ProjectCore(request, sustainedIntervalSample, intervalCapture, maximumTicks);
    }

    private static bool TryCreateState(
        in NPCCommandedReleaseProjectionRequest request,
        out NPCCommandedReleaseProjectionState state)
    {
        state = default;
        float delta = request.DeltaSeconds;
        if (!float.IsFinite(delta) || delta <= 0f
            || !request.CurrentBlend.IsFinite()
            || !request.PinBlend.IsFinite()
            || !request.BlendAcceleration.IsFinite()
            || !float.IsFinite(request.Phase)
            || !float.IsFinite(request.PlaybackRate))
        {
            return false;
        }

        Vector2 blend = request.CurrentBlend;
        float phase = Mathf.PosMod(request.Phase, 1f);
        NPCYawCommandState yawCommandState = request.YawCommandState;
        if (!float.IsFinite(request.RequestedYawRate) && yawCommandState.RemainingCorrectionRadians <= 0f)
        {
            yawCommandState.RemainingCorrectionRadians = float.MaxValue;
        }

        bool converged = blend.DistanceTo(request.PinBlend) <= BlendConvergenceTolerance;
        state = new NPCCommandedReleaseProjectionState
        {
            Blend = blend,
            BlendAcceleration = request.BlendAcceleration,
            Phase = phase,
            UnwrappedPhase = double.IsFinite(request.CurrentUnwrappedPhase)
                ? request.CurrentUnwrappedPhase
                : phase,
            Rate = Math.Max(request.PlaybackRate, 0.001f),
            RateAcceleration = float.IsFinite(request.PlaybackRateAcceleration)
                ? request.PlaybackRateAcceleration
                : 0f,
            Yaw = request.ActorYaw,
            YawCommandState = yawCommandState,
            PreviousAppliedYawRate = float.IsFinite(request.InitialAppliedYawRate)
                ? request.InitialAppliedYawRate
                : 0f,
            SemanticTotal = Vector2.Zero,
            Converged = converged,
            ConvergenceSeconds = converged ? 0f : float.NaN,
            Tick = 0,
        };
        return true;
    }

    private static NPCCommandedReleaseProjectionStep AdvanceProjectionInterval(
        in NPCCommandedReleaseProjectionRequest request,
        Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample> sustainedIntervalSample,
        List<NPCCommandedReleaseProjectionInterval>? intervalCapture,
        ref NPCCommandedReleaseProjectionState state)
    {
        int tick = ++state.Tick;
        float delta = request.DeltaSeconds;
        float cyclicLength = float.IsFinite(request.CyclicLengthSeconds) && request.CyclicLengthSeconds > 0f
            ? request.CyclicLengthSeconds
            : float.Epsilon;
        Vector2 previousBlend = state.Blend;
        float previousPhase = state.Phase;
        state.Blend = NPCTransitionContinuity.StepVelocityCommand(
            state.Blend,
            request.PinBlend,
            ref state.BlendAcceleration,
            delta,
            BlendConvergenceAcceleration,
            BlendConvergenceJerk);
        if (!state.Converged && state.Blend.DistanceTo(request.PinBlend) <= BlendConvergenceTolerance)
        {
            state.Converged = true;
            state.ConvergenceSeconds = tick * delta;
        }

        float publishedRate = Math.Max(request.OrdinaryPlaybackRate, 0.001f);
        float timeToSelectedReleaseSeconds = NPCExitReleaseGate.TimeToNextRelease(
            state.Phase,
            request.TargetPhase,
            cyclicLength,
            Math.Max(state.Rate, 0.001f));
        bool alignRate = publishedRate + 0.00001f < state.Rate
            && timeToSelectedReleaseSeconds <= PublisherRateAlignmentWindowTicks * delta;
        float appliedRate = alignRate
            ? publishedRate
            : StepPlaybackRate(state.Rate, publishedRate, ref state.RateAcceleration, delta);
        double projectedAdvance = state.Rate * delta / cyclicLength;
        double selectedPosition = request.SelectedOccurrence.UnwrappedPhase;
        bool occurrenceAlreadyMissed = selectedPosition < state.UnwrappedPhase - NPCExitReleaseGate.DefaultPhaseTolerance;
        bool releaseIntervalConsumed = !occurrenceAlreadyMissed
            && (state.Converged || !request.RequireBlendConvergenceBeforeRelease)
            && selectedPosition >= state.UnwrappedPhase - NPCExitReleaseGate.DefaultPhaseTolerance
            && selectedPosition <= state.UnwrappedPhase + projectedAdvance + NPCExitReleaseGate.DefaultPhaseTolerance;
        float commitPhase = state.Phase;
        state.Phase = Mathf.PosMod(state.Phase + (state.Rate * delta / cyclicLength), 1f);
        state.UnwrappedPhase += projectedAdvance;

        NPCCommandedReleaseAuthoredIntervalSample intervalSample =
            sustainedIntervalSample(previousBlend, previousPhase, state.Blend, state.Phase, state.Rate);
        float previousYaw = state.Yaw;
        float yawTarget = float.IsFinite(request.MovingYawTarget)
            ? request.MovingYawTarget
            : request.ExitStartYaw;
        float movingYawError = float.IsFinite(yawTarget)
            ? NPCNavigationRoutePlan.WrapAngle(yawTarget - state.Yaw)
            : 0f;
        float terminalYawError = float.IsFinite(request.TerminalYawTarget)
            ? NPCNavigationRoutePlan.WrapAngle(request.TerminalYawTarget - state.Yaw)
            : float.NaN;
        float sampledAuthoredYawRate = intervalSample.AuthoredYawRadians / delta;
        float authoredYawRate = tick == 1 && float.IsFinite(request.InitialAuthoredYawRate)
            ? request.InitialAuthoredYawRate
            : sampledAuthoredYawRate;
        float requestedYawRate = float.IsFinite(request.RequestedYawRate)
            ? request.RequestedYawRate
            : Mathf.Clamp(
                movingYawError / Math.Max(delta, float.Epsilon),
                -NPCTerminalReleaseScheduler.MaximumYawRate,
                NPCTerminalReleaseScheduler.MaximumYawRate);
        float yawRate = NPCYawCommandStep.Step(
            requestedYawRate,
            authoredYawRate,
            movingYawError,
            terminalYawError,
            request.YawSpeedWeight,
            request.YawPhaseWeight,
            delta,
            request.MaximumYawResidualRate,
            request.YawResidualSmoothingRate,
            ref state.YawCommandState);
        float yawDelta = yawRate * delta;
        if (float.IsFinite(request.TerminalYawTarget))
        {
            if (request.TerminalYawGuardByDistance || Mathf.Abs(terminalYawError) <= 0.2f)
            {
                yawDelta = NPCYawConvergence.StepTerminalRotation(
                    state.Yaw,
                    request.TerminalYawTarget,
                    ref state.PreviousAppliedYawRate,
                    delta);
            }
            yawDelta = NPCYawConvergence.ClampRotationDelta(state.Yaw, request.TerminalYawTarget, yawDelta);
        }
        else
        {
            state.PreviousAppliedYawRate = yawDelta / delta;
        }

        state.Yaw = NPCNavigationRoutePlan.WrapAngle(state.Yaw + yawDelta);

        Vector2 semanticRoot = intervalSample.SemanticRoot;
        Vector3 worldRoot = new Basis(Vector3.Up, state.Yaw) * new Vector3(semanticRoot.X, 0f, -semanticRoot.Y);
        Vector2 semanticStep = new(worldRoot.X, worldRoot.Z);
        state.SemanticTotal += semanticStep;
        intervalCapture?.Add(new(
            tick - 1,
            delta,
            semanticStep,
            NPCNavigationRoutePlan.WrapAngle(state.Yaw - previousYaw),
            state.Blend));
        state.Rate = appliedRate;

        return new(releaseIntervalConsumed, commitPhase, projectedAdvance);
    }

    private static NPCCommandedReleaseProjectionResult ProjectCore(
        NPCCommandedReleaseProjectionRequest request,
        Func<Vector2, float, Vector2, float, float, NPCCommandedReleaseAuthoredIntervalSample> sustainedIntervalSample,
        List<NPCCommandedReleaseProjectionInterval>? intervalCapture,
        int maximumTicks)
    {
        ArgumentNullException.ThrowIfNull(sustainedIntervalSample);
        if (maximumTicks <= 0)
        {
            return NPCCommandedReleaseProjectionResult.Unresolved;
        }

        if (!TryCreateState(request, out NPCCommandedReleaseProjectionState state))
        {
            return NPCCommandedReleaseProjectionResult.Unresolved;
        }

        NPCReleaseOccurrence selectedOccurrence = request.SelectedOccurrence.IsValid
            ? request.SelectedOccurrence
            : NPCExitReleaseGate.SelectOccurrence(
                1,
                state.UnwrappedPhase,
                request.TargetPhase,
                NPCAnimationPhaseLane.Left,
                "projection_release");
        request = request with
        {
            SelectedOccurrence = selectedOccurrence,
        };

        while (state.Tick < maximumTicks)
        {
            NPCCommandedReleaseProjectionStep step = AdvanceProjectionInterval(
                request,
                sustainedIntervalSample,
                intervalCapture,
                ref state);
            if (step.ReleaseIntervalConsumed)
            {
                return CreateStateResult(
                    request,
                    selectedOccurrence,
                    in state,
                    resolved: true,
                    state.Tick * request.DeltaSeconds,
                    step.CommitPhase);
            }
        }

        return CreateStateResult(request, selectedOccurrence, in state, resolved: false, 0f);
    }

    private static NPCCommandedReleaseProjectionResult CreateStateResult(
        in NPCCommandedReleaseProjectionRequest request,
        in NPCReleaseOccurrence selectedOccurrence,
        in NPCCommandedReleaseProjectionState state,
        bool resolved,
        float timeToReleaseSeconds,
        float? phase = null)
        => new(
            state.Converged,
            state.Converged ? state.ConvergenceSeconds : 0f,
            resolved,
            timeToReleaseSeconds,
            request.TargetPhase,
            state.SemanticTotal,
            state.Blend,
            phase ?? state.Phase,
            state.Rate,
            state.Yaw,
            state.Tick,
            selectedOccurrence);

    /// <summary>
    /// Decides whether a scheduler-selected imminent candidate may commit its release. A resolved candidate is gated
    /// on its PROJECTED pin-convergence completing by the projected release beat — the blend will be pinned at,
    /// definitively before, release, which is the pin discipline the gate exists to enforce (the packaged *2i exits
    /// continue the walk-family release pose; committing from a genuinely mid-convergence blend crossfades a mixed
    /// stance into that pose, the measured 3.9-5.1 m/s foot sweeps). With convergence-aware occurrence selection, a
    /// valid gated candidate releases at/after convergence completion BY CONSTRUCTION, so this criterion passes
    /// laterally-correcting approaches whose blend is still walking to the pin but converges inside the lead-in.
    /// No settle margin is added to the ordering check: beat selection already guarantees convergence before
    /// release for valid candidates. Invalid or missed occurrences never bypass this gate; ordinary locomotion
    /// continues until a later compatible occurrence exists.
    /// </summary>
    public static bool PassesExitSeamReleaseGate(
        NPCCommandedReleaseProjectionResult projection,
        float blendDistanceToPinMetres,
        float atPinRadiusMetres = ExitSeamAtPinRadius)
    {
        return (float.IsFinite(blendDistanceToPinMetres) && blendDistanceToPinMetres <= atPinRadiusMetres)
            || (projection.Resolved
                && projection.BlendConverged
                && projection.ConvergenceSeconds <= projection.TimeToReleaseSeconds);
    }

    /// <summary>
    /// Resolves the terminal yaw residual for an imminent candidate. A resolved commanded trajectory supplies the
    /// selected occurrence's release pose; unresolved projections remain fail-closed at the current actor pose.
    /// </summary>
    public static float ResolveExitYawResidual(
        float terminalYaw,
        float exitEndpointYaw,
        float actorYaw,
        in NPCCommandedReleaseProjectionResult projection)
    {
        float yawAtRelease = projection.Resolved && float.IsFinite(projection.YawAtRelease)
            ? projection.YawAtRelease
            : actorYaw;
        return float.IsFinite(terminalYaw)
            && float.IsFinite(exitEndpointYaw)
            && float.IsFinite(yawAtRelease)
            ? Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(terminalYaw - yawAtRelease - exitEndpointYaw))
            : float.PositiveInfinity;
    }

    /// <summary>
    /// Pure playback-rate governor step shared by the runtime publisher and this projection: moves the current
    /// rate toward the target under the smoothing acceleration/jerk limits, snapping exactly onto the target when
    /// the step reaches it (and clearing the acceleration so the next transition restarts cleanly).
    /// </summary>
    public static float StepPlaybackRate(
        float current,
        float target,
        ref float acceleration,
        float delta)
    {
        if (!float.IsFinite(current) || !float.IsFinite(target) || !float.IsFinite(acceleration) || delta <= 0f)
        {
            acceleration = 0f;
            return float.IsFinite(current) ? current : 1f;
        }

        float offset = target - current;
        float desiredAcceleration = Mathf.Clamp(
            offset / delta,
            -PlaybackRateSmoothingAcceleration,
            PlaybackRateSmoothingAcceleration);
        acceleration = Mathf.MoveToward(acceleration, desiredAcceleration, PlaybackRateSmoothingJerk * delta);
        float step = acceleration * delta;
        if (Mathf.Sign(step) == Mathf.Sign(offset) && Mathf.Abs(step) >= Mathf.Abs(offset))
        {
            acceleration = 0f;
            return target;
        }

        return current + step;
    }
}

internal struct NPCCommandedReleaseProjectionState
{
    public Vector2 Blend;
    public Vector2 BlendAcceleration;
    public float Phase;
    public double UnwrappedPhase;
    public float Rate;
    public float RateAcceleration;
    public float Yaw;
    public NPCYawCommandState YawCommandState;
    public float PreviousAppliedYawRate;
    public Vector2 SemanticTotal;
    public bool Converged;
    public float ConvergenceSeconds;
    public int Tick;
}

internal readonly record struct NPCCommandedReleaseProjectionStep(
    bool ReleaseIntervalConsumed,
    float CommitPhase,
    double ProjectedAdvance);
