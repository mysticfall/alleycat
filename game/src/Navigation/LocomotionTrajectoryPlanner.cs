using Godot;

namespace AlleyCat.Navigation;

/// <summary>
/// Explicit tuning and scoring contract for predictive route following.
/// </summary>
public sealed record LocomotionPlannerConfiguration
{
    /// <summary>Gets the shared immutable default configuration.</summary>
    public static LocomotionPlannerConfiguration Default { get; } = new();

    /// <summary>Gets corner turn lead distance in metres.</summary>
    public float CornerAnticipationDistance { get; init; } = 0.8f;
    /// <summary>Gets the distance within which endpoint lateral correction is permitted.</summary>
    public float EndpointCorrectionDistance { get; init; } = 0.65f;
    /// <summary>Gets proportional endpoint correction gain within the endpoint region.</summary>
    public float EndpointCorrectionGain { get; init; } = 2.0f;
    /// <summary>Gets terminal facing blend distance in metres.</summary>
    public float TerminalFacingDistance { get; init; } = 0.55f;
    /// <summary>Gets initial heading error that enters turn-in-place, in radians.</summary>
    public float InitialTurnInPlaceAngle { get; init; } = 0.85f;
    /// <summary>Gets heading error that releases turn-in-place, in radians.</summary>
    public float InitialTurnReleaseAngle { get; init; } = 0.42f;
    /// <summary>Gets nominal path target look-ahead in metres.</summary>
    public float LookAheadDistance { get; init; } = 0.35f;
    /// <summary>Gets continuous lateral feedback gain.</summary>
    public float CrossTrackLookAheadGain { get; init; } = 1.25f;
    /// <summary>Gets the small correction-neutral band in metres.</summary>
    public float CrossTrackCorrectionBand { get; init; } = 0.025f;
    /// <summary>Gets correction sign hysteresis in metres.</summary>
    public float CorrectionHysteresis { get; init; } = 0.012f;
    /// <summary>Gets permitted projection backtrack in metres.</summary>
    public float ProjectionBacktrackDistance { get; init; } = 0.2f;
    /// <summary>Gets forward acceleration and deceleration limit in metres per second squared.</summary>
    public float ForwardAcceleration { get; init; } = 1.8f;
    /// <summary>Gets lateral acceleration limit in metres per second squared.</summary>
    public float LateralAcceleration { get; init; } = 2.2f;
    /// <summary>Gets movement command slew rate per second.</summary>
    public float MovementSlewRate { get; init; } = 2.5f;
    /// <summary>Gets turn command slew rate per second.</summary>
    public float TurnSlewRate { get; init; } = 3.2f;
    /// <summary>Gets bounded route-revision transition duration in seconds.</summary>
    public float RouteRevisionTransitionSeconds { get; init; } = 0.3f;
    /// <summary>Gets added stopping margin in metres.</summary>
    public float StoppingMargin { get; init; } = 0.04f;
    /// <summary>Gets endpoint position tolerance in metres.</summary>
    public float PositionTolerance { get; init; } = 0.035f;
    /// <summary>
    /// Gets the endpoint distance at which translation yields to the terminal-facing pivot.
    /// This matches the navigation facade's default destination tolerance so a small residual
    /// position correction cannot indefinitely suppress the final finite pivot.
    /// </summary>
    public float TerminalFacingPositionTolerance { get; init; } = 0.05f;
    /// <summary>Gets the endpoint distance that enters terminal settling.</summary>
    public float TerminalSettlingEntryDistance { get; init; } = 0.05f;
    /// <summary>Gets the endpoint distance that releases terminal settling for positional re-acquisition.</summary>
    public float TerminalSettlingReleaseDistance { get; init; } = 0.075f;
    /// <summary>Gets endpoint facing tolerance in radians.</summary>
    public float FacingTolerance { get; init; } = 0.035f;
    /// <summary>Gets the minimum signed command that enters an authored stationary pivot.</summary>
    public float MinimumStationaryPivotTurn { get; init; } = 1.0f;
    /// <summary>Gets candidate progress score weight.</summary>
    public float ProgressWeight { get; init; } = 2.4f;
    /// <summary>Gets candidate cross-track score weight.</summary>
    public float CrossTrackWeight { get; init; } = 4.0f;
    /// <summary>Gets candidate route-heading score weight.</summary>
    public float HeadingWeight { get; init; } = 1.8f;
    /// <summary>Gets candidate stopping-overshoot score weight.</summary>
    public float OvershootWeight { get; init; } = 12.0f;
    /// <summary>Gets candidate destination-facing score weight.</summary>
    public float TerminalFacingWeight { get; init; } = 2.5f;
    /// <summary>Gets candidate control-change score weight.</summary>
    public float ControlChangeWeight { get; init; } = 0.22f;
    /// <summary>Gets candidate planned-control tracking score weight.</summary>
    public float PlannedControlWeight { get; init; } = 6.0f;
    /// <summary>Gets candidate control-reversal score weight.</summary>
    public float ReversalWeight { get; init; } = 0.9f;
    /// <summary>Gets forward preference and side/back penalty weight.</summary>
    public float ForwardBiasWeight { get; init; } = 0.3f;
}

/// <summary>
/// Persistent planner history. It can also be supplied explicitly to reproduce one deterministic evaluation.
/// </summary>
public readonly record struct LocomotionPlannerState(
    float Progress,
    Vector2 Movement,
    float Turn,
    int CorrectionSign,
    bool TurningInPlace,
    long DestinationRequestGeneration,
    long RouteRevision,
    float RevisionTransitionRemaining,
    bool LocalCorrectionActive = false,
    bool TerminalSettling = false)
{
    /// <summary>Gets neutral planner history.</summary>
    public static LocomotionPlannerState Initial => default;
}

/// <summary>
/// First command and retained predictive intent from a planner evaluation.
/// </summary>
public readonly record struct LocomotionPlannerOutput(
    Vector2 Movement,
    float Turn,
    LocomotionPlannerState State,
    float ProjectedProgress,
    float CrossTrackError,
    float RemainingDistance,
    float PredictedProgressAt02Seconds,
    float PredictedProgressAt05Seconds,
    float PredictedProgressAt10Seconds);

/// <summary>
/// Pure persistent receding-horizon planner. It predicts root-motion response but never applies or warps it.
/// </summary>
public sealed class LocomotionTrajectoryPlanner(LocomotionPlannerConfiguration? configuration = null)
{
    private const float MinimumDelta = 0.000001f;
    private readonly LocomotionPlannerConfiguration _configuration = configuration ?? LocomotionPlannerConfiguration.Default;

    /// <summary>Gets retained control and route history.</summary>
    public LocomotionPlannerState State
    {
        get; private set;
    }

    /// <summary>Clears route and command history.</summary>
    public void Reset() => State = LocomotionPlannerState.Initial;

    /// <summary>
    /// Determines whether an observed terminal position has moved far enough to release settled arrival.
    /// </summary>
    /// <remarks>
    /// This intentionally shares the planner's terminal-settling release threshold so navigation can
    /// continue observing a completed request without reintroducing route correction for harmless drift.
    /// </remarks>
    public bool RequiresTerminalPositionRecovery(Transform3D actorTransform, LocomotionRoutePlan routePlan)
    {
        ArgumentNullException.ThrowIfNull(routePlan);
        if (!actorTransform.IsFinite())
        {
            return false;
        }

        float positionTolerance = SafePositive(_configuration.PositionTolerance, 0.035f);
        float terminalFacingPositionTolerance = Math.Max(
            positionTolerance,
            SafePositive(_configuration.TerminalFacingPositionTolerance, 0.05f));
        float releaseDistance = Math.Max(
            terminalFacingPositionTolerance,
            SafePositive(_configuration.TerminalSettlingReleaseDistance, 0.075f));
        Vector3 actorPosition = new(actorTransform.Origin.X, 0.0f, actorTransform.Origin.Z);
        return actorPosition.DistanceTo(routePlan.Endpoint) > releaseDistance;
    }

    /// <summary>Slews retained controls to neutral without discarding route history.</summary>
    public LocomotionPlannerState Stop(double deltaSeconds)
    {
        float delta = double.IsFinite(deltaSeconds) ? Math.Max((float)deltaSeconds, 0.0f) : 0.0f;
        Vector2 movement = MoveTowards(
            State.Movement,
            Vector2.Zero,
            SafePositive(_configuration.MovementSlewRate, 2.5f) * delta);
        float turn = Mathf.MoveToward(
            State.Turn,
            0.0f,
            SafePositive(_configuration.TurnSlewRate, 3.2f) * delta);
        State = Sanitise(State with
        {
            Movement = movement,
            Turn = turn,
            TurningInPlace = false,
            RevisionTransitionRemaining = 0.0f,
        });
        return State;
    }

    /// <summary>Evaluates and retains one command from the current observed actor transform.</summary>
    public LocomotionPlannerOutput Tick(
        Transform3D actorTransform,
        double deltaSeconds,
        LocomotionResponseProfile responseProfile,
        LocomotionRoutePlan routePlan)
    {
        LocomotionPlannerOutput output = Evaluate(
            actorTransform,
            deltaSeconds,
            State,
            responseProfile,
            routePlan,
            _configuration);
        State = output.State;
        return output;
    }

    /// <summary>
    /// Evaluates one deterministic tick without mutating a planner instance.
    /// </summary>
    public static LocomotionPlannerOutput Evaluate(
        Transform3D actorTransform,
        double deltaSeconds,
        LocomotionPlannerState priorState,
        LocomotionResponseProfile responseProfile,
        LocomotionRoutePlan routePlan,
        LocomotionPlannerConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(responseProfile);
        ArgumentNullException.ThrowIfNull(routePlan);
        configuration ??= LocomotionPlannerConfiguration.Default;

        float delta = double.IsFinite(deltaSeconds) ? (float)deltaSeconds : 0.0f;
        if (delta <= MinimumDelta || !actorTransform.IsFinite())
        {
            LocomotionPlannerState safe = Sanitise(priorState);
            return Neutral(safe, routePlan);
        }

        LocomotionPlannerState prior = Sanitise(priorState);
        bool hasHistory = prior.DestinationRequestGeneration != 0 || prior.RouteRevision != 0;
        bool revisionChanged = hasHistory
            && (prior.DestinationRequestGeneration != routePlan.DestinationRequestGeneration
                || prior.RouteRevision != routePlan.RouteRevision);
        float transitionRemaining = revisionChanged
            ? SafeNonNegative(configuration.RouteRevisionTransitionSeconds)
            : Math.Max(prior.RevisionTransitionRemaining - delta, 0.0f);

        Vector3 actorPosition = new(actorTransform.Origin.X, 0.0f, actorTransform.Origin.Z);
        float projectionSeed = revisionChanged ? 0.0f : prior.Progress;
        RouteProjection projection = routePlan.Project(
            actorPosition,
            projectionSeed,
            SafeNonNegative(configuration.ProjectionBacktrackDistance));
        float progress = Mathf.Clamp(projection.Distance, 0.0f, routePlan.TotalLength);
        float remaining = Math.Max(routePlan.TotalLength - progress, 0.0f);
        float endpointDistance = actorPosition.DistanceTo(routePlan.Endpoint);
        float endpointCorrectionDistance = SafePositive(configuration.EndpointCorrectionDistance, 0.65f);
        float positionTolerance = SafePositive(configuration.PositionTolerance, 0.035f);
        float terminalFacingPositionTolerance = Math.Max(
            positionTolerance,
            SafePositive(configuration.TerminalFacingPositionTolerance, 0.05f));
        float terminalSettlingEntryDistance = Math.Max(
            terminalFacingPositionTolerance,
            SafePositive(configuration.TerminalSettlingEntryDistance, 0.05f));
        float terminalSettlingReleaseDistance = Math.Max(
            terminalSettlingEntryDistance,
            SafePositive(configuration.TerminalSettlingReleaseDistance, 0.075f));
        bool terminalSettling = prior.TerminalSettling
            ? endpointDistance <= terminalSettlingReleaseDistance
            : endpointDistance <= terminalSettlingEntryDistance;
        bool reacquiringEndpoint = remaining <= SafePositive(configuration.PositionTolerance, 0.035f)
            && endpointDistance > endpointCorrectionDistance;
        float actorYaw = LocomotionRoutePlan.YawFromDirection(actorTransform.Basis.Orthonormalized() * Vector3.Forward);
        float terminalYawError = LocomotionRoutePlan.WrapAngle(routePlan.TerminalYaw - actorYaw);

        bool turningInPlace = prior.TurningInPlace;
        float routeYaw = reacquiringEndpoint
            ? LocomotionRoutePlan.YawFromDirection(routePlan.Endpoint - actorPosition)
            : routePlan.DesiredRouteYaw(progress);
        float routeYawError = LocomotionRoutePlan.WrapAngle(routeYaw - actorYaw);
        if (!turningInPlace
            && ((!routePlan.UsesShortEndpointCorrection
               && progress <= SafePositive(configuration.PositionTolerance, 0.035f))
              || reacquiringEndpoint)
            && Mathf.Abs(routeYawError) >= SafePositive(configuration.InitialTurnInPlaceAngle, 0.85f))
        {
            turningInPlace = true;
        }
        else if (turningInPlace
            && Mathf.Abs(routeYawError) <= SafePositive(configuration.InitialTurnReleaseAngle, 0.42f))
        {
            turningInPlace = false;
        }

        Basis actorRotation = actorTransform.Basis.Orthonormalized();
        Vector3 targetPoint = routePlan.Sample(
            Math.Min(progress + SafePositive(configuration.LookAheadDistance, 0.35f), routePlan.TotalLength),
            out _);
        Vector3 targetOffset = targetPoint - actorPosition;
        Vector2 localTarget = ToLocal(actorRotation, targetOffset);
        int correctionSign = UpdateCorrectionSign(
            prior.CorrectionSign,
            localTarget.X,
            configuration.CrossTrackCorrectionBand,
            configuration.CorrectionHysteresis);

        float forwardSpeed = SafePositive(responseProfile.Forwards.PlanarVelocity.Y, 1.0f);
        float targetSpeed = routePlan.TargetSpeed(remaining, forwardSpeed, SafePositive(configuration.ForwardAcceleration, 1.8f));
        float targetForward = Mathf.Clamp(targetSpeed / forwardSpeed, 0.0f, 1.0f);
        float targetLateral = Mathf.Clamp(
            localTarget.X * SafePositive(configuration.CrossTrackLookAheadGain, 1.25f),
            -1.0f,
            1.0f);
        if (reacquiringEndpoint)
        {
            targetForward = Mathf.Clamp(endpointDistance / endpointCorrectionDistance, 0.0f, 1.0f);
            targetLateral = 0.0f;
        }
        if (prior.CorrectionSign != 0
            && correctionSign == prior.CorrectionSign
            && Math.Sign(targetLateral) != prior.CorrectionSign)
        {
            targetLateral = 0.0f;
        }

        bool correctingEndpoint = !terminalSettling
            && remaining <= endpointCorrectionDistance
            && endpointDistance <= endpointCorrectionDistance
            && endpointDistance > terminalFacingPositionTolerance;
        if (correctingEndpoint)
        {
            Vector2 localEndpoint = ToLocal(actorRotation, routePlan.Endpoint - actorPosition);
            float scale = SafePositive(configuration.EndpointCorrectionGain, 2.0f) / endpointCorrectionDistance;
            targetLateral = Mathf.Clamp(localEndpoint.X * scale, -1.0f, 1.0f);
            targetForward = Mathf.Clamp(localEndpoint.Y * scale, -1.0f, 1.0f);
        }

        // ANIM-003 exposes backwards motion exclusively for bounded rear/lateral correction. The
        // contract thresholds are deliberately not tuning values: enter at <= 1 m and release at >= 1.25 m.
        bool rearIntent = localTarget.Y < -MinimumDelta;
        bool localCorrection = prior.LocalCorrectionActive
            ? !reacquiringEndpoint && endpointDistance < 1.25f
            : !reacquiringEndpoint && endpointDistance <= 1.0f && rearIntent;
        if (localCorrection && rearIntent)
        {
            // A bounded rear correction is a supported moving response, not an initial stationary
            // facing correction. The initial-heading branch above runs before actor-local intent is
            // available, so explicitly release it once the signed local policy is resolved.
            turningInPlace = false;
            float correctionForward = correctingEndpoint
                ? targetForward
                : Mathf.Clamp(localTarget.Y * SafePositive(configuration.EndpointCorrectionGain, 2.0f), -1.0f, 0.0f);
            targetForward = correctionForward;
        }

        if (terminalSettling)
        {
            targetLateral = 0.0f;
            targetForward = 0.0f;
            localCorrection = false;
        }

        float terminalFacingBlend = reacquiringEndpoint
            ? 0.0f
            : 1.0f - Mathf.Clamp(
                remaining / SafePositive(configuration.TerminalFacingDistance, 0.55f),
                0.0f,
                1.0f);
        float desiredYawError = terminalSettling
            ? terminalYawError
            : BlendAngles(routeYawError, terminalYawError, terminalFacingBlend);
        float angularRate = AngularRate(responseProfile, desiredYawError, targetForward, targetLateral);
        float desiredTurn = Mathf.Clamp(desiredYawError / Math.Max(angularRate * 0.5f, 0.01f), -1.0f, 1.0f);

        // The bounded backwards/lateral response is itself the directional correction. Suppress
        // the competing pivot/arc request so candidate scoring cannot prefer stationary turning.
        if (localCorrection)
        {
            desiredTurn = 0.0f;
        }

        // The outer Walking blend uses turn and movement magnitude to select forward moving-turn arcs. Combining that
        // branch with lateral endpoint correction would replace the requested inner movement direction with
        // forwards root motion. Settle position through the directional inner blend first, then turn in place.
        if (correctingEndpoint)
        {
            desiredTurn = 0.0f;
        }

        if (turningInPlace)
        {
            targetForward = 0.0f;
            targetLateral = 0.0f;
        }

        bool terminalFacingReached = Mathf.Abs(terminalYawError) <= SafePositive(configuration.FacingTolerance, 0.035f);
        bool terminalTargetReached = terminalSettling
            && endpointDistance <= terminalSettlingEntryDistance
            && terminalFacingReached;
        bool terminalPivotRequested = false;
        if (terminalTargetReached)
        {
            desiredTurn = 0.0f;
            turningInPlace = false;
        }
        else if (terminalSettling && !terminalFacingReached)
        {
            // The outer locomotion graph selects an authored held looped pivot only when this explicit
            // stationary intent is retained. Without it, either side of the ±π seam can publish a
            // small turn value while remaining in the movement branch after positional arrival.
            turningInPlace = true;
            desiredTurn = Mathf.Sign(desiredTurn) * Math.Max(
                Mathf.Abs(desiredTurn),
                SafePositive(configuration.MinimumStationaryPivotTurn, 1.0f));
            terminalPivotRequested = true;
        }

        Vector2 desiredMovement = ClampUnit(new Vector2(targetLateral, targetForward));
        Candidate best = correctingEndpoint || terminalSettling
            ? new Candidate(desiredMovement, desiredTurn, 0.0f)
            : SelectCandidate(
                actorPosition,
                actorYaw,
                progress,
                prior,
                desiredMovement,
                desiredTurn,
                responseProfile,
                routePlan,
                configuration);

        float movementStep = SafePositive(configuration.MovementSlewRate, 2.5f) * delta;
        float turnStep = SafePositive(configuration.TurnSlewRate, 3.2f) * delta;
        Vector2 movement = MoveTowards(prior.Movement, best.Movement, movementStep);
        float lateralSpeed = Math.Max(
            Mathf.Abs(responseProfile.SideStepLeft.PlanarVelocity.X),
            Mathf.Abs(responseProfile.SideStepRight.PlanarVelocity.X));
        float longitudinalSpeed = Math.Max(
            Mathf.Abs(responseProfile.Forwards.PlanarVelocity.Y),
            0.01f);
        movement.X = Mathf.MoveToward(
            prior.Movement.X,
            movement.X,
            SafePositive(configuration.LateralAcceleration, 2.2f) * delta / Math.Max(lateralSpeed, 0.01f));
        movement.Y = Mathf.MoveToward(
            prior.Movement.Y,
            movement.Y,
            SafePositive(configuration.ForwardAcceleration, 1.8f) * delta / Math.Max(longitudinalSpeed, 0.01f));
        float turn = Mathf.MoveToward(prior.Turn, best.Turn, turnStep);

        // A held looped pivot must release as soon as its observed Root yaw reaches the requested terminal facing.
        // Do not let command slew retain a residual stationary turn that would start another cycle at rest.
        if (terminalTargetReached)
        {
            movement = Vector2.Zero;
            turn = 0.0f;
        }

        if (transitionRemaining > 0.0f && revisionChanged)
        {
            float transition = SafePositive(configuration.RouteRevisionTransitionSeconds, 0.3f);
            float blend = Mathf.Clamp(delta / transition, 0.0f, 1.0f);
            movement = prior.Movement.Lerp(movement, blend);
            turn = Mathf.Lerp(prior.Turn, turn, blend);
        }

        if (terminalPivotRequested)
        {
            // Route replans near a navigation-agent endpoint are expected. Do not let a repeated
            // route-revision blend drop a held looped pivot below the animation graph's entry threshold.
            movement = Vector2.Zero;
            turn = desiredTurn;
        }

        // Terminal settling is entered and released with positional hysteresis. A retained terminal
        // position keeps consuming only stationary root motion: any observed facing deviation therefore
        // deterministically re-requests the stationary pivot above. Only a meaningful position miss
        // releases settling and returns to endpoint correction.
        if (terminalSettling)
        {
            movement = Vector2.Zero;
            turn = terminalPivotRequested ? desiredTurn : 0.0f;
        }

        movement = movement.IsFinite() ? ClampUnit(movement) : Vector2.Zero;
        // Reverse travel is legal only while the retained bounded local-correction mode is active.
        if (!localCorrection)
        {
            movement.Y = Math.Max(movement.Y, 0.0f);
        }
        turn = float.IsFinite(turn) ? Mathf.Clamp(turn, -1.0f, 1.0f) : 0.0f;
        LocomotionPlannerState state = new(
            progress,
            movement,
            turn,
            correctionSign,
            turningInPlace,
            routePlan.DestinationRequestGeneration,
            routePlan.RouteRevision,
            transitionRemaining,
            localCorrection,
            terminalSettling);

        PredictedPose at02 = Predict(actorPosition, actorYaw, movement, turn, 0.2f, responseProfile);
        PredictedPose at05 = Predict(actorPosition, actorYaw, movement, turn, 0.5f, responseProfile);
        PredictedPose at10 = Predict(actorPosition, actorYaw, movement, turn, 1.0f, responseProfile);
        return new LocomotionPlannerOutput(
            movement,
            turn,
            state,
            progress,
            SignedCrossTrack(actorPosition, projection),
            remaining,
            routePlan.Project(at02.Position, progress, 0.0f).Distance,
            routePlan.Project(at05.Position, progress, 0.0f).Distance,
            routePlan.Project(at10.Position, progress, 0.0f).Distance);
    }

    private static Candidate SelectCandidate(
        Vector3 position,
        float yaw,
        float progress,
        LocomotionPlannerState prior,
        Vector2 desiredMovement,
        float desiredTurn,
        LocomotionResponseProfile profile,
        LocomotionRoutePlan route,
        LocomotionPlannerConfiguration configuration)
    {
        Candidate best = new(desiredMovement, desiredTurn, float.PositiveInfinity);
        for (int movementChoice = 0; movementChoice < 3; movementChoice++)
        {
            Vector2 movement = movementChoice switch
            {
                0 => desiredMovement,
                1 => desiredMovement * 0.5f,
                _ => new Vector2(0.0f, Math.Max(desiredMovement.Y, 0.0f)),
            };
            for (int turnChoice = 0; turnChoice < 3; turnChoice++)
            {
                float turn = turnChoice switch
                {
                    0 => desiredTurn,
                    1 => desiredTurn * 0.5f,
                    _ => 0.0f,
                };
                float score = ScoreCandidate(
                    position,
                    yaw,
                    progress,
                    prior,
                    movement,
                    turn,
                    desiredMovement,
                    desiredTurn,
                    profile,
                    route,
                    configuration);
                if (score < best.Score)
                {
                    best = new Candidate(movement, turn, score);
                }
            }
        }

        return best;
    }

    private static float ScoreCandidate(
        Vector3 position,
        float yaw,
        float progress,
        LocomotionPlannerState prior,
        Vector2 movement,
        float turn,
        Vector2 desiredMovement,
        float desiredTurn,
        LocomotionResponseProfile profile,
        LocomotionRoutePlan route,
        LocomotionPlannerConfiguration configuration)
    {
        float score = 0.0f;
        ScoreHorizon(0.2f, 0.25f);
        ScoreHorizon(0.5f, 0.35f);
        ScoreHorizon(1.0f, 0.4f);

        score += SafeWeight(configuration.ControlChangeWeight) * (movement.DistanceSquaredTo(prior.Movement) + Mathf.Pow(turn - prior.Turn, 2.0f));
        score += SafeWeight(configuration.PlannedControlWeight)
            * (movement.DistanceSquaredTo(desiredMovement) + Mathf.Pow(turn - desiredTurn, 2.0f));
        if (movement.Dot(prior.Movement) < -0.001f || turn * prior.Turn < -0.001f)
        {
            score += SafeWeight(configuration.ReversalWeight);
        }

        score -= SafeWeight(configuration.ForwardBiasWeight) * Math.Max(movement.Y, 0.0f);
        score += SafeWeight(configuration.ForwardBiasWeight) * (Mathf.Abs(movement.X) + Math.Max(-movement.Y, 0.0f));
        return float.IsFinite(score) ? score : float.MaxValue;

        void ScoreHorizon(float horizon, float horizonWeight)
        {
            PredictedPose predicted = Predict(position, yaw, movement, turn, horizon, profile);
            RouteProjection projected = route.Project(predicted.Position, progress, 0.0f);
            float predictedProgress = projected.Distance;
            float overshoot = Math.Max(predicted.Position.DistanceTo(route.Endpoint) - Math.Max(route.TotalLength - predictedProgress, 0.0f), 0.0f);
            float terminalBlend = 1.0f - Mathf.Clamp((route.TotalLength - predictedProgress) / SafePositive(configuration.TerminalFacingDistance, 0.55f), 0.0f, 1.0f);
            float routeYaw = route.DesiredRouteYaw(predictedProgress);
            float desiredYaw = route.UsesShortEndpointCorrection
                ? route.TerminalYaw
                : BlendAngles(routeYaw, route.TerminalYaw, terminalBlend);
            float heading = Mathf.Abs(LocomotionRoutePlan.WrapAngle(desiredYaw - predicted.Yaw));
            float facing = Mathf.Abs(LocomotionRoutePlan.WrapAngle(route.TerminalYaw - predicted.Yaw));
            score += horizonWeight * (
                (-SafeWeight(configuration.ProgressWeight) * (predictedProgress - progress))
                + (SafeWeight(configuration.CrossTrackWeight) * projected.CrossTrackDistance * projected.CrossTrackDistance)
                + (SafeWeight(configuration.HeadingWeight) * heading * heading)
                + (SafeWeight(configuration.OvershootWeight) * overshoot * overshoot)
                + (SafeWeight(configuration.TerminalFacingWeight) * terminalBlend * facing * facing));
        }
    }

    private static PredictedPose Predict(
        Vector3 position,
        float yaw,
        Vector2 movement,
        float turn,
        float seconds,
        LocomotionResponseProfile profile)
    {
        Vector2 velocity = TranslationVelocity(profile, movement, turn);
        float angularVelocity = SignedAngularVelocity(profile, turn, movement.Length());
        float midYaw = yaw + (angularVelocity * seconds * 0.5f);
        var rotation = new Basis(Vector3.Up, midYaw);
        Vector3 localVelocity = new(velocity.X, 0.0f, -velocity.Y);
        Vector3 predictedPosition = position + (rotation * localVelocity * seconds);
        return new PredictedPose(predictedPosition, LocomotionRoutePlan.WrapAngle(yaw + (angularVelocity * seconds)));
    }

    private static Vector2 TranslationVelocity(LocomotionResponseProfile profile, Vector2 movement, float turn)
    {
        float xRate = movement.X < 0.0f
            ? -profile.SideStepLeft.PlanarVelocity.X
            : profile.SideStepRight.PlanarVelocity.X;
        float yRate = movement.Y < 0.0f
            ? profile.Backwards.PlanarVelocity.Y
            : profile.Forwards.PlanarVelocity.Y;
        Vector2 velocity = new(movement.X * xRate, movement.Y * yRate);
        if (movement.Y > 0.0f && Mathf.Abs(turn) > 0.0f)
        {
            LocomotionCycleResponse turning = turn > 0.0f ? profile.WalkArcLeft : profile.WalkArcRight;
            float blend = Mathf.Abs(turn) * movement.Y;
            velocity = velocity.Lerp(turning.PlanarVelocity * movement.Y, blend);
        }

        return velocity.IsFinite() ? velocity : Vector2.Zero;
    }

    private static float SignedAngularVelocity(LocomotionResponseProfile profile, float turn, float movementMagnitude)
    {
        if (turn == 0.0f)
        {
            return 0.0f;
        }

        LocomotionCycleResponse response = movementMagnitude <= 0.05f
            ? (turn > 0.0f ? profile.TurnInPlaceLeft90 : profile.TurnInPlaceRight90)
            : (turn > 0.0f ? profile.WalkArcLeft : profile.WalkArcRight);
        return Mathf.Abs(turn) * response.AngularVelocity;
    }

    private static float AngularRate(LocomotionResponseProfile profile, float yawError, float forward, float lateral)
    {
        LocomotionCycleResponse response = Mathf.Abs(forward) + Mathf.Abs(lateral) <= 0.05f
            ? (yawError >= 0.0f ? profile.TurnInPlaceLeft90 : profile.TurnInPlaceRight90)
            : (yawError >= 0.0f ? profile.WalkArcLeft : profile.WalkArcRight);
        return Math.Max(Mathf.Abs(response.AngularVelocity), 0.01f);
    }

    private static Vector2 ToLocal(Basis actorRotation, Vector3 worldOffset)
    {
        Vector3 local = actorRotation.Transposed() * worldOffset;
        Vector2 result = new(local.X, -local.Z);
        return result.IsFinite() ? result : Vector2.Zero;
    }

    private static int UpdateCorrectionSign(int previous, float lateralError, float band, float hysteresis)
    {
        float enter = SafeNonNegative(band) + SafeNonNegative(hysteresis);
        float leave = Math.Max(SafeNonNegative(band) - SafeNonNegative(hysteresis), 0.0f);
        return previous == 0
            ? Mathf.Abs(lateralError) > enter ? Math.Sign(lateralError) : 0
            : Math.Sign(lateralError) == previous && Mathf.Abs(lateralError) > leave
            ? previous
            : Math.Sign(lateralError) != previous && Mathf.Abs(lateralError) > enter
            ? Math.Sign(lateralError)
            : previous;
    }

    private static float SignedCrossTrack(Vector3 position, RouteProjection projection)
    {
        Vector3 offset = position - projection.Point;
        return (projection.Direction.X * offset.Z) - (projection.Direction.Z * offset.X);
    }

    private static float BlendAngles(float from, float to, float weight)
        => LocomotionRoutePlan.WrapAngle(from + (LocomotionRoutePlan.WrapAngle(to - from) * Mathf.Clamp(weight, 0.0f, 1.0f)));

    private static Vector2 MoveTowards(Vector2 from, Vector2 to, float maximumDelta)
    {
        Vector2 difference = to - from;
        float length = difference.Length();
        return length <= maximumDelta || length <= MinimumDelta ? to : from + (difference * (maximumDelta / length));
    }

    private static Vector2 ClampUnit(Vector2 value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1.0f ? value / Mathf.Sqrt(lengthSquared) : value;
    }

    private static LocomotionPlannerState Sanitise(LocomotionPlannerState state)
        => state.Movement.IsFinite()
            && float.IsFinite(state.Turn)
            && float.IsFinite(state.Progress)
            && float.IsFinite(state.RevisionTransitionRemaining)
            ? state with
            {
                Movement = ClampUnit(state.Movement),
                Turn = Mathf.Clamp(state.Turn, -1.0f, 1.0f),
                Progress = Math.Max(state.Progress, 0.0f),
                RevisionTransitionRemaining = Math.Max(state.RevisionTransitionRemaining, 0.0f),
            }
            : LocomotionPlannerState.Initial;

    private static LocomotionPlannerOutput Neutral(LocomotionPlannerState state, LocomotionRoutePlan route)
        => new(Vector2.Zero, 0.0f, state, state.Progress, 0.0f, Math.Max(route.TotalLength - state.Progress, 0.0f), state.Progress, state.Progress, state.Progress);

    private static float SafePositive(float value, float fallback) => float.IsFinite(value) && value > 0.0f ? value : fallback;

    private static float SafeNonNegative(float value) => float.IsFinite(value) ? Math.Max(value, 0.0f) : 0.0f;

    private static float SafeWeight(float value) => float.IsFinite(value) ? Math.Max(value, 0.0f) : 0.0f;

    private readonly record struct Candidate(Vector2 Movement, float Turn, float Score);

    private readonly record struct PredictedPose(Vector3 Position, float Yaw);
}
