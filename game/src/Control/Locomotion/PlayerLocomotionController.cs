using Godot;

#pragma warning disable CS1591, IDE0010, IDE0032, IDE0048

namespace AlleyCat.Control.Locomotion;

/// <summary>Lifecycle state owned exclusively by responsive player locomotion.</summary>
public enum PlayerLocomotionState
{
    Idle,
    EnteringForward,
    EnteringBackward,
    TravellingForward,
    TravellingDirectional,
    ExitingForward,
    ExitingBackward,
    PivotingLeft,
    PivotingRight,
    SideSteppingLeft,
    SideSteppingRight,
}

/// <summary>Retained anatomical phase lane for one player movement bout.</summary>
public enum PlayerLocomotionPhaseLane
{
    Left, Right
}

/// <summary>Explicit animation command emitted only at a locomotion lifecycle boundary.</summary>
public enum PlayerLocomotionCommand
{
    None, EnterForward, EnterBackward, Travel, ExitForward, ExitBackward, PivotLeft, PivotRight,
    SideStepLeft, SideStepRight, Idle
}

/// <summary>Immutable output from one pure player-controller update.</summary>
public readonly record struct PlayerLocomotionOutput(
    PlayerLocomotionState State,
    PlayerLocomotionPhaseLane PhaseLane,
    PlayerLocomotionCommand Command,
    Vector2 Blend,
    float ForwardArcBlend,
    float DomainBlend,
    float PlaybackRate,
    bool MovementAllowed,
    bool RotationAllowed,
    PlayerLocomotionPhaseLane CommandLane);

/// <summary>
/// Small event-driven controller for live player input. It owns no route, destination, or NPC policy.
/// </summary>
public sealed class PlayerLocomotionController
{
    private const float ForwardDomainEnterRatio = 0.42f;
    private const float ForwardDomainExitRatio = 0.58f;

    /// <summary>
    /// Held-turn magnitude required to enter a standing pivot. The upstream radial deadzone already centred the
    /// stick; this threshold only rejects the residual sliver between the deadzone edge and a deliberate turn.
    /// </summary>
    private const float PivotEnterTurnThreshold = 0.1f;

    /// <summary>
    /// Mostly-lateral dominance required to enter a confined side step, mirroring <c>NPCSideStep.Select</c>'s
    /// actor-frame test: the lateral component must strictly dominate the longitudinal one (|Y| &lt; ratio · |X|).
    /// The upstream radial deadzone already centred the stick, so a dominant lateral component is always a
    /// deliberate strafe; at or beyond the diagonal boundary the i2* entry regime keeps owning the bout.
    /// </summary>
    private const float SideStepDominanceRatio = 1f;
    private PlayerLocomotionState _state;
    private PlayerLocomotionPhaseLane _phaseLane;
    private Vector2 _smoothedPlanarResidual;
    private float _smoothedYawResidual;
    private float _segmentPlanarCorrection;
    private float _segmentYawCorrection;
    private PlayerLocomotionPhaseLane _commandLane;
    private Vector2 _retainedSustainedBlend;
    private float _retainedSustainedArc;
    private float _retainedSustainedDomain;
    private float _retainedSustainedRate = 1f;

    public PlayerLocomotionState State => _state;
    public PlayerLocomotionPhaseLane PhaseLane => _phaseLane;

    public PlayerLocomotionOutput Step(
        Vector2 movement,
        float turn,
        LocomotionPermissions permissions,
        bool finiteTransitionCompleted,
        float playbackRateMinimum,
        float playbackRateMaximum,
        PlayerTravelReleasePermitV1? releasePermit = null)
    {
        movement = Finite(movement);
        turn = float.IsFinite(turn) ? Mathf.Clamp(turn, -1f, 1f) : 0f;
        Vector2 permittedMovement = permissions.MovementAllowed ? movement : Vector2.Zero;
        float permittedTurn = permissions.RotationAllowed ? turn : 0f;
        bool wantsMovement = !permittedMovement.IsZeroApprox();

        // Stationary pivots are differentiated from travel arcs by the movement lifecycle rather than the raw body
        // speed: Idle is reached only after the *2i exit completes, so the body has actually stopped, while entry,
        // travel, and exit states keep the arcs authoritative even when intent changes before the body catches up.
        // ANIM-002 confines pl45/pr45 to idle-origin manoeuvres, so they are never selectable from those states.
        bool wantsPivot = !wantsMovement && Mathf.Abs(permittedTurn) > PivotEnterTurnThreshold;
        // Side steps are the lateral counterpart of the pivots: confined precision manoeuvres (ANIM-002 sl/sr) that
        // only start from Idle when the lateral intent strictly dominates. Pivots cannot compete for the same frame
        // because they require !wantsMovement while a side step requires wantsMovement — the two idle-origin
        // manoeuvres are mutually exclusive by construction.
        bool wantsSideStep = wantsMovement
            && Mathf.Abs(permittedMovement.Y) < SideStepDominanceRatio * Mathf.Abs(permittedMovement.X);
        PlayerLocomotionCommand command = PlayerLocomotionCommand.None;
        switch (_state)
        {
            case PlayerLocomotionState.Idle when wantsSideStep:
                // Lateral-dominant intent selects the confined side-step clip instead of an entry clip. The lane
                // follows the lateral sign, matching SelectInitialLane and the NPC sl/sr convention: −X steps left,
                // +X steps right (authored semantic ∓X lateral Root displacement).
                bool stepLeft = permittedMovement.X < 0f;
                _state = stepLeft ? PlayerLocomotionState.SideSteppingLeft : PlayerLocomotionState.SideSteppingRight;
                command = stepLeft ? PlayerLocomotionCommand.SideStepLeft : PlayerLocomotionCommand.SideStepRight;
                ResetCorrectionSegment();
                break;
            case PlayerLocomotionState.Idle when wantsMovement:
                _phaseLane = SelectInitialLane(permittedMovement);
                ResetCorrectionSegment();
                bool backward = SelectBackwardEntry(permittedMovement);
                _state = backward ? PlayerLocomotionState.EnteringBackward : PlayerLocomotionState.EnteringForward;
                command = backward ? PlayerLocomotionCommand.EnterBackward : PlayerLocomotionCommand.EnterForward;
                break;
            case PlayerLocomotionState.Idle when wantsPivot:
                // ANIM-002 response coordinates: positive turn requests actor-right (pr45, negative local-Y yaw);
                // negative turn requests actor-left (pl45, positive local-Y yaw).
                bool pivotLeft = permittedTurn < 0f;
                _state = pivotLeft ? PlayerLocomotionState.PivotingLeft : PlayerLocomotionState.PivotingRight;
                command = pivotLeft ? PlayerLocomotionCommand.PivotLeft : PlayerLocomotionCommand.PivotRight;
                ResetCorrectionSegment();
                break;
            case PlayerLocomotionState.Idle when !wantsMovement:
                ResetCorrectionSegment();
                break;
            case PlayerLocomotionState.EnteringForward when finiteTransitionCompleted:
                _state = wantsMovement
                    ? SelectTravelState(permittedMovement, PlayerLocomotionState.TravellingForward)
                    : PlayerLocomotionState.TravellingForward;
                command = PlayerLocomotionCommand.Travel;
                break;
            case PlayerLocomotionState.EnteringBackward when finiteTransitionCompleted:
                _state = wantsMovement
                    ? SelectTravelState(permittedMovement, PlayerLocomotionState.TravellingDirectional)
                    : PlayerLocomotionState.TravellingDirectional;
                command = PlayerLocomotionCommand.Travel;
                break;
            case PlayerLocomotionState.TravellingForward:
            case PlayerLocomotionState.TravellingDirectional:
                if (!wantsMovement)
                {
                    if (releasePermit is { IsIssued: true, Occurrence: { Consumption: PlayerTravelReleaseConsumption.Consumed } release }
                        && release.AnatomicalSide == ToReleaseSide(_phaseLane))
                    {
                        bool backwardExit = release.EvaluatedCapability.IsBackward();
                        _state = backwardExit
                            ? PlayerLocomotionState.ExitingBackward
                            : PlayerLocomotionState.ExitingForward;
                        command = backwardExit
                            ? PlayerLocomotionCommand.ExitBackward
                            : PlayerLocomotionCommand.ExitForward;
                    }
                }
                else
                {
                    _state = SelectTravelState(permittedMovement, _state);
                }

                break;
            case PlayerLocomotionState.ExitingForward when finiteTransitionCompleted:
            case PlayerLocomotionState.ExitingBackward when finiteTransitionCompleted:
                _state = PlayerLocomotionState.Idle;
                command = PlayerLocomotionCommand.Idle;
                ResetCorrectionSegment();
                break;
            case PlayerLocomotionState.PivotingLeft:
            case PlayerLocomotionState.PivotingRight:
                if (wantsMovement)
                {
                    // Under the Travel discipline the pivot→locomotion hand-off routes via the idle state on
                    // purpose: each hop rides its single pre-authored edge (CTRL-001 TR11/TR26), and the graph
                    // passes through StandingCrouching visually as a ~16 ms crossfade regardless. The next frame's
                    // Idle-state logic then emits the entry command, mirroring the side-step hand-off below.
                    _state = PlayerLocomotionState.Idle;
                    command = PlayerLocomotionCommand.Idle;
                    ResetCorrectionSegment();
                }
                else if (!wantsPivot)
                {
                    // Stick centred: release the held pivot cleanly back to idle without a completion handshake.
                    _state = PlayerLocomotionState.Idle;
                    command = PlayerLocomotionCommand.Idle;
                    ResetCorrectionSegment();
                }
                else
                {
                    bool requestedLeft = permittedTurn < 0f;
                    PlayerLocomotionState requested = requestedLeft ? PlayerLocomotionState.PivotingLeft : PlayerLocomotionState.PivotingRight;
                    if (requested != _state)
                    {
                        // Held sign flip swaps the looped 45-degree unit without an idle interlude.
                        _state = requested;
                        command = requestedLeft ? PlayerLocomotionCommand.PivotLeft : PlayerLocomotionCommand.PivotRight;
                        ResetCorrectionSegment();
                    }
                }

                break;
            case PlayerLocomotionState.SideSteppingLeft:
            case PlayerLocomotionState.SideSteppingRight:
                if (!wantsMovement)
                {
                    // Stick released: end the held loop cleanly back to idle without a completion handshake
                    // (pivot-release precedent — sl/sr release is input-driven).
                    _state = PlayerLocomotionState.Idle;
                    command = PlayerLocomotionCommand.Idle;
                    ResetCorrectionSegment();
                }
                else if (wantsSideStep)
                {
                    bool requestedLeft = permittedMovement.X < 0f;
                    PlayerLocomotionState requested = requestedLeft ? PlayerLocomotionState.SideSteppingLeft : PlayerLocomotionState.SideSteppingRight;
                    if (requested != _state)
                    {
                        // Held sign flip swaps the looped side-step unit without an idle interlude (pivot-flip
                        // precedent); the PlayerSideStepL↔PlayerSideStepR crossfade edge carries the swap.
                        _state = requested;
                        command = requestedLeft ? PlayerLocomotionCommand.SideStepLeft : PlayerLocomotionCommand.SideStepRight;
                        ResetCorrectionSegment();
                    }
                }
                else
                {
                    // Forward/backward dominance ends the confined manoeuvre through Idle: the next frame's
                    // EnterForward/EnterBackward command then rides the idle→entry crossfade as one smooth hop,
                    // matching the NPC side-step→idle→entry flow.
                    _state = PlayerLocomotionState.Idle;
                    command = PlayerLocomotionCommand.Idle;
                    ResetCorrectionSegment();
                }

                break;
            default:
                break;
        }

        float magnitude = Mathf.Clamp(permittedMovement.Length(), 0f, 1f);
        float minimumRate = Mathf.Max(0.01f, playbackRateMinimum);
        float maximumRate = Mathf.Max(minimumRate, playbackRateMaximum);
        float playbackRate = Mathf.Lerp(minimumRate, maximumRate, magnitude);
        float arc = Mathf.Clamp(permittedTurn + permittedMovement.X * 0.5f, -1f, 1f);
        float domain = _state == PlayerLocomotionState.TravellingDirectional ? 1f : 0f;
        // Resolve the commanded lane only now, after the switch: the idle-entry case selects the bout lane
        // inside the switch, so a pre-switch snapshot would command the previous tick's lane.
        _commandLane = _phaseLane;
        // Snapshot the sustained presentation on every travelling tick that carries live movement — including
        // the entry→travel hop tick — so a release that arrives before any sustained tick still holds honest
        // travel values through the gate wait and the exit crossfade.
        if (wantsMovement && _state is PlayerLocomotionState.TravellingForward or PlayerLocomotionState.TravellingDirectional)
        {
            _retainedSustainedBlend = permittedMovement;
            _retainedSustainedArc = arc;
            _retainedSustainedDomain = domain;
            _retainedSustainedRate = playbackRate;
        }
        // While authoritative exit evidence is unavailable, a release keeps the retained travel presentation so
        // zero input neither snaps the blend point nor silently decelerates walking feet under the body.
        bool holdingForExitGate = !wantsMovement
            && _state is PlayerLocomotionState.TravellingForward or PlayerLocomotionState.TravellingDirectional;
        if (holdingForExitGate || _state is PlayerLocomotionState.ExitingForward or PlayerLocomotionState.ExitingBackward)
        {
            _retainedSustainedRate = Mathf.Max(_retainedSustainedRate, minimumRate);
            return new(
                _state,
                _phaseLane,
                command,
                _retainedSustainedBlend,
                _retainedSustainedArc,
                _retainedSustainedDomain,
                _retainedSustainedRate,
                permissions.MovementAllowed,
                permissions.RotationAllowed,
                _commandLane);
        }

        return new(
            _state,
            _phaseLane,
            command,
            permittedMovement,
            arc,
            domain,
            playbackRate,
            permissions.MovementAllowed,
            permissions.RotationAllowed,
            _commandLane);
    }

    /// <summary>
    /// Bounded smoothed synthetic planar correction for one frame. Idle, exits, pivots, and side steps author the
    /// full planar motion themselves, so those states zero the residual; during i2* entries the residual weight
    /// ramps with the clip's authored acceleration (CTRL-001 UR9: no visible foot skating while the weight shift
    /// plants the first foot).
    /// </summary>
    public Vector2 CalculatePlanarResidual(
        Vector2 desiredVelocity,
        Vector2 authoredVelocity,
        float speedWeight,
        float phaseWeight,
        float delta,
        float maximumRate,
        float maximumSegmentDistance,
        float smoothingRate,
        float entryRampReference)
    {
        if (delta <= 0f
            || _state is PlayerLocomotionState.Idle
                or PlayerLocomotionState.ExitingForward
                or PlayerLocomotionState.ExitingBackward
                or PlayerLocomotionState.PivotingLeft
                or PlayerLocomotionState.PivotingRight
                or PlayerLocomotionState.SideSteppingLeft
                or PlayerLocomotionState.SideSteppingRight)
        {
            _smoothedPlanarResidual = Vector2.Zero;
            return Vector2.Zero;
        }

        // CTRL-001 UR9: the entry ramp scales assistance with the clip's own authored planar speed — zero while the
        // weight shift keeps the authored speed near rest, reaching full authority once the gait is up to the ramp
        // reference — so synthetic correction cannot skate the planted feet off their authored marks.
        float weight = Mathf.Clamp(speedWeight, 0f, 1f)
            * Mathf.Clamp(phaseWeight, 0f, 1f)
            * EntryResidualRampScale(Finite(authoredVelocity).Length(), entryRampReference);
        Vector2 error = Finite(desiredVelocity) - Finite(authoredVelocity);
        Vector2 target = error.LimitLength(Mathf.Max(0f, maximumRate) * weight);
        float blend = 1f - Mathf.Exp(-Mathf.Max(0f, smoothingRate) * delta);
        _smoothedPlanarResidual = _smoothedPlanarResidual.Lerp(target, blend);
        float remainingDistance = Mathf.Max(0f, maximumSegmentDistance - _segmentPlanarCorrection);
        Vector2 bounded = _smoothedPlanarResidual.LimitLength(remainingDistance / delta);
        _segmentPlanarCorrection += bounded.Length() * delta;
        return bounded;
    }

    /// <summary>
    /// Bounded smoothed synthetic yaw correction for one frame. Pivots and side steps author their full yaw
    /// cadence, so those states zero the residual; during i2* entries the same authored-acceleration ramp as the
    /// planar residual gates yaw assistance (CTRL-001 UR9: no visible foot skating).
    /// </summary>
    public float CalculateYawResidual(
        float desiredYawRate,
        float authoredYawRate,
        float authoredPlanarSpeed,
        float speedWeight,
        float phaseWeight,
        float delta,
        float maximumRate,
        float maximumSegmentRadians,
        float smoothingRate,
        float entryRampReference)
    {
        if (delta <= 0f
            || _state is PlayerLocomotionState.PivotingLeft
                or PlayerLocomotionState.PivotingRight
                or PlayerLocomotionState.SideSteppingLeft
                or PlayerLocomotionState.SideSteppingRight)
        {
            // While pivoting or side-stepping, the authored confined-manoeuvre cadence is the sole yaw authority;
            // synthetic assistance would fight the packaged response and skate the planted feet.
            _smoothedYawResidual = 0f;
            return 0f;
        }

        float finiteDesired = float.IsFinite(desiredYawRate) ? desiredYawRate : 0f;
        float finiteAuthored = float.IsFinite(authoredYawRate) ? authoredYawRate : 0f;
        // CTRL-001 UR9: yaw assistance during an entry ramps with the same authored planar acceleration measure as
        // the planar residual, so turning intent cannot yaw the body while the weight shift still has the feet
        // planted (authored planar speed near zero).
        float weight = Mathf.Clamp(speedWeight, 0f, 1f)
            * Mathf.Clamp(phaseWeight, 0f, 1f)
            * EntryResidualRampScale(authoredPlanarSpeed, entryRampReference);
        float target = Mathf.Clamp(finiteDesired - finiteAuthored, -Mathf.Max(0f, maximumRate) * weight, Mathf.Max(0f, maximumRate) * weight);
        float blend = 1f - Mathf.Exp(-Mathf.Max(0f, smoothingRate) * delta);
        _smoothedYawResidual = Mathf.Lerp(_smoothedYawResidual, target, blend);
        float remaining = Mathf.Max(0f, maximumSegmentRadians - _segmentYawCorrection);
        float bounded = Mathf.Clamp(_smoothedYawResidual, -remaining / delta, remaining / delta);
        _segmentYawCorrection += Mathf.Abs(bounded) * delta;
        return bounded;
    }

    /// <summary>
    /// Residual authority multiplier for i2* entries (CTRL-001 UR9): the authored planar speed ramped against
    /// <paramref name="rampReference" /> and clamped to full authority. Non-entry states return 1 — the ramp only
    /// gates the weight-shift portion of an entry clip.
    /// </summary>
    private float EntryResidualRampScale(float authoredPlanarSpeed, float rampReference)
    {
        if (_state is not (PlayerLocomotionState.EnteringForward or PlayerLocomotionState.EnteringBackward))
        {
            return 1f;
        }

        float speed = float.IsFinite(authoredPlanarSpeed) ? Mathf.Max(authoredPlanarSpeed, 0f) : 0f;
        return Mathf.Clamp(speed / Mathf.Max(rampReference, 0.0001f), 0f, 1f);
    }

    private static PlayerLocomotionPhaseLane SelectInitialLane(Vector2 movement)
        => movement.X > 0f ? PlayerLocomotionPhaseLane.Right : PlayerLocomotionPhaseLane.Left;

    private static bool SelectBackwardEntry(Vector2 movement)
        => movement.Y < -Mathf.Abs(movement.X) * 0.35f;

    private static PlayerLocomotionState SelectTravelState(Vector2 movement, PlayerLocomotionState current)
    {
        float forward = Mathf.Max(0f, movement.Y);
        float lateralOrBackward = Mathf.Abs(movement.X) + Mathf.Max(0f, -movement.Y);
        float threshold = current == PlayerLocomotionState.TravellingForward ? ForwardDomainExitRatio : ForwardDomainEnterRatio;
        return forward > 0f && lateralOrBackward <= forward * threshold
            ? PlayerLocomotionState.TravellingForward
            : PlayerLocomotionState.TravellingDirectional;
    }

    private static PlayerTravelReleaseSide ToReleaseSide(PlayerLocomotionPhaseLane lane)
        => lane == PlayerLocomotionPhaseLane.Left ? PlayerTravelReleaseSide.Left : PlayerTravelReleaseSide.Right;

    private void ResetCorrectionSegment()
    {
        _smoothedPlanarResidual = Vector2.Zero;
        _smoothedYawResidual = 0f;
        _segmentPlanarCorrection = 0f;
        _segmentYawCorrection = 0f;
    }

    private static Vector2 Finite(Vector2 value)
        => value.IsFinite() ? value : Vector2.Zero;
}
