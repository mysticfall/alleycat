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
    ExitingNeutral,
    TurningInPlace,
}

/// <summary>Retained anatomical phase lane for one player movement bout.</summary>
public enum PlayerLocomotionPhaseLane
{
    Left, Right
}

/// <summary>Explicit animation command emitted only at a locomotion lifecycle boundary.</summary>
public enum PlayerLocomotionCommand
{
    None, EnterForward, EnterBackward, Travel, ExitForward, ExitBackward, ExitNeutral, Idle, TurnLeft, TurnRight
}

/// <summary>Immutable output from one pure player-controller update.</summary>
public readonly record struct PlayerLocomotionOutput(
    PlayerLocomotionState State,
    PlayerLocomotionPhaseLane PhaseLane,
    PlayerLocomotionCommand Command,
    Vector2 Blend,
    float ForwardArcBlend,
    float DomainBlend,
    bool MovementAllowed,
    bool RotationAllowed,
    PlayerLocomotionPhaseLane CommandLane);

/// <summary>
/// Small event-driven presentation scheduler for live player input (CTRL-001 TR4). It owns no route, destination, or
/// NPC policy, and never gates physical response: selection and completion of authored starts, stops, and travel are
/// presentation choices that follow current intent, and every state accepts interruption and retargeting. The
/// evaluated authored root is the physical movement owner. Scheduling follows motion: intent drives the prompt
/// state response, while the previous evaluated authored-root measurement selects the
/// presented travel direction — while the body still travels against fresh intent, the presented gait agrees with
/// realised motion instead of committing a new-direction walk that contradicts actual travel.
/// </summary>
public sealed class PlayerLocomotionController
{
    private const float ForwardDomainEnterRatio = 0.42f;
    private const float ForwardDomainExitRatio = 0.58f;

    private PlayerLocomotionState _state;
    private PlayerLocomotionPhaseLane _phaseLane;
    private PlayerLocomotionPhaseLane _commandLane;
    private Vector2 _retainedSustainedBlend;
    private float _retainedSustainedArc;
    private float _retainedSustainedDomain;

    public PlayerLocomotionState State => _state;
    public PlayerLocomotionPhaseLane PhaseLane => _phaseLane;

    /// <summary>Completes an externally executed shared stop or its finite failure disposition.</summary>
    public void FinishStop()
    {
        if (_state is PlayerLocomotionState.ExitingForward
            or PlayerLocomotionState.ExitingBackward
            or PlayerLocomotionState.ExitingNeutral)
        {
            _state = PlayerLocomotionState.Idle;
        }
    }

    public PlayerLocomotionOutput Step(
        Vector2 movement,
        Vector2 _,
        float turn,
        LocomotionPermissions permissions,
        bool finiteTransitionCompleted)
    {
        movement = Finite(movement);
        turn = float.IsFinite(turn) ? Mathf.Clamp(turn, -1f, 1f) : 0f;
        Vector2 permittedMovement = permissions.MovementAllowed ? movement : Vector2.Zero;
        bool wantsMovement = !permittedMovement.IsZeroApprox();
        // The turn demand uses the same non-zero convention as movement; a movement demand always wins
        // (the travelling channel already presents turn through the arc blend).
        bool wantsTurn = !Mathf.IsZeroApprox(turn);
        PlayerLocomotionCommand command = PlayerLocomotionCommand.None;
        if (!permissions.MovementAllowed || !permissions.RotationAllowed)
        {
            _state = PlayerLocomotionState.Idle;
            return new(
                _state,
                _phaseLane,
                PlayerLocomotionCommand.Idle,
                Vector2.Zero,
                0f,
                0f,
                permissions.MovementAllowed,
                permissions.RotationAllowed,
                _commandLane);
        }
        switch (_state)
        {
            case PlayerLocomotionState.Idle when wantsMovement:
                _phaseLane = SelectInitialLane(permittedMovement);
                bool backward = SelectBackwardEntry(permittedMovement);
                _state = backward ? PlayerLocomotionState.EnteringBackward : PlayerLocomotionState.EnteringForward;
                command = backward ? PlayerLocomotionCommand.EnterBackward : PlayerLocomotionCommand.EnterForward;
                break;
            case PlayerLocomotionState.Idle when wantsTurn:
                // Stationary turning (CTRL-001): a held turn with no movement presents the authored
                // turn-in-place clips; the yaw reaches the actor through the same authored root-rotation
                // application the travelling channel uses. The looping presentation sustains while held.
                command = turn > 0f ? PlayerLocomotionCommand.TurnRight : PlayerLocomotionCommand.TurnLeft;
                _state = PlayerLocomotionState.TurningInPlace;
                break;
            case PlayerLocomotionState.TurningInPlace:
                if (wantsMovement)
                {
                    // Fresh movement replaces the turn without an idle interlude, mirroring the
                    // exit-state interruption into travel.
                    _phaseLane = SelectInitialLane(permittedMovement);
                    bool turnToBackward = SelectBackwardEntry(permittedMovement);
                    _state = turnToBackward ? PlayerLocomotionState.EnteringBackward : PlayerLocomotionState.EnteringForward;
                    command = turnToBackward ? PlayerLocomotionCommand.EnterBackward : PlayerLocomotionCommand.EnterForward;
                }
                else if (!wantsTurn)
                {
                    _state = PlayerLocomotionState.Idle;
                    command = PlayerLocomotionCommand.Idle;
                }

                break;
            case PlayerLocomotionState.EnteringForward:
            case PlayerLocomotionState.EnteringBackward:
                if (!wantsMovement)
                {
                    // The entry-window release classifies on the retained bout intent (the live intent the
                    // entry was commanding, snapshotted on every holding tick): a lateral-dominant intent
                    // entered through the forward entry clip (and a lateral-heavy backward blend through the
                    // backward entry) must still take the neutral stop policy — UR6/UR11 bind at any release
                    // point, including during an entry or crossfade.
                    if (PlayerStopPolicy.IsLateralDominantRelease(_retainedSustainedBlend.X, _retainedSustainedBlend.Y))
                    {
                        _state = PlayerLocomotionState.ExitingNeutral;
                        command = PlayerLocomotionCommand.ExitNeutral;
                    }
                    else
                    {
                        bool backwardExit = _state == PlayerLocomotionState.EnteringBackward;
                        _state = backwardExit ? PlayerLocomotionState.ExitingBackward : PlayerLocomotionState.ExitingForward;
                        command = backwardExit ? PlayerLocomotionCommand.ExitBackward : PlayerLocomotionCommand.ExitForward;
                    }
                }
                else if (finiteTransitionCompleted || SelectBackwardEntry(permittedMovement) != (_state == PlayerLocomotionState.EnteringBackward))
                {
                    // A completed start, or fresh intent the entry no longer matches, retargets straight into travel:
                    // the sustained blend presents the new direction on the same tick with no idle interlude.
                    _state = SelectTravelState(permittedMovement, PlayerLocomotionState.TravellingForward);
                    command = PlayerLocomotionCommand.Travel;
                }

                break;
            case PlayerLocomotionState.TravellingForward:
            case PlayerLocomotionState.TravellingDirectional:
                if (!wantsMovement)
                {
                    if (PlayerStopPolicy.IsLateralDominantRelease(_retainedSustainedBlend.X, _retainedSustainedBlend.Y))
                    {
                        // Lateral-dominant releases (pure lateral and lateral-heavy diagonals) never route
                        // through the forward stop clip: its authored forward root would rotate the body's
                        // remaining motion into a forward glide. They take the neutral stop policy — the
                        // travel presentation freezes and crossfades straight into idle with no velocity
                        // rotation (responsiveness over stop-continuation naturalness).
                        _state = PlayerLocomotionState.ExitingNeutral;
                        command = PlayerLocomotionCommand.ExitNeutral;
                    }
                    else
                    {
                        bool backwardExit = _retainedSustainedBlend.Y < -Mathf.Abs(_retainedSustainedBlend.X) * 0.35f;
                        _state = backwardExit ? PlayerLocomotionState.ExitingBackward : PlayerLocomotionState.ExitingForward;
                        command = backwardExit ? PlayerLocomotionCommand.ExitBackward : PlayerLocomotionCommand.ExitForward;
                    }
                }
                else if (IsTravelReversal(permittedMovement))
                {
                    // A forward/backward domain reversal (CTRL-001 reversal cadence) routes through the
                    // AUTHORED exit for the direction being travelled: the stop clips are the authored
                    // braking presentation, so the still-forward realised motion is decelerated by content
                    // instead of being contradicted by an instant full-rate opposite gait. The opposite
                    // direction is entered afterwards through the normal idle→entry path.
                    bool backwardExit = _retainedSustainedBlend.Y < -Mathf.Abs(_retainedSustainedBlend.X) * 0.35f;
                    _state = backwardExit ? PlayerLocomotionState.ExitingBackward : PlayerLocomotionState.ExitingForward;
                    command = backwardExit ? PlayerLocomotionCommand.ExitBackward : PlayerLocomotionCommand.ExitForward;
                }
                else
                {
                    _state = SelectTravelState(permittedMovement, _state);
                }

                break;
            case PlayerLocomotionState.ExitingForward:
            case PlayerLocomotionState.ExitingBackward:
            case PlayerLocomotionState.ExitingNeutral:
                if (wantsMovement && !IsTravelReversal(permittedMovement))
                {
                    // Fresh intent replaces the pending stop without a stop–idle–start commitment — unless
                    // the intent OPPOSES the travelled direction (a reversal): then the authored braking
                    // stop plays out first and the opposite direction enters through the idle→entry path.
                    _state = SelectTravelState(permittedMovement, PlayerLocomotionState.TravellingForward);
                    command = PlayerLocomotionCommand.Travel;
                }
                else if (finiteTransitionCompleted)
                {
                    _state = PlayerLocomotionState.Idle;
                    command = PlayerLocomotionCommand.Idle;
                }

                break;
            default:
                break;
        }

        // Authored root is the physical owner, so live permitted intent schedules the source that will move the actor.
        // Feeding the previous authored interval back into selection would create an independent response path and
        // make reversal/lateral changes lag behind the first eligible tick.
        Vector2 presentedDirection = permittedMovement;
        float arc = Mathf.Clamp(turn + permittedMovement.X * 0.5f, -1f, 1f);
        float domain = _state == PlayerLocomotionState.TravellingDirectional ? 1f : 0f;
        // Resolve the commanded lane only now, after the switch: the idle-entry case selects the bout lane inside
        // the switch, so a pre-switch snapshot would command the previous tick's lane.
        _commandLane = _phaseLane;
        // Snapshot the sustained presentation on every tick that carries live movement — the entry→travel
        // hop tick and the entry window itself included — so the exit crossfade can fade the last honest
        // gait out coherently, and so an entry-window release classifies on the bout intent it was
        // commanding (UR6/UR11: the neutral stop policy binds at any release point) instead of on a stale
        // blend from a previous bout.
        if (wantsMovement && _state is PlayerLocomotionState.EnteringForward
            or PlayerLocomotionState.EnteringBackward
            or PlayerLocomotionState.TravellingForward
            or PlayerLocomotionState.TravellingDirectional)
        {
            _retainedSustainedBlend = presentedDirection;
            _retainedSustainedArc = arc;
            _retainedSustainedDomain = domain;
        }

        // While an authored stop fades the travel tree out, the outgoing blend keeps the last live gait so the
        // crossfade does not collapse toward a mid-space mush pose. This is presentation continuity only: the
        // response model brakes independently of it.
        bool exiting = _state is PlayerLocomotionState.ExitingForward
            or PlayerLocomotionState.ExitingBackward
            or PlayerLocomotionState.ExitingNeutral;
        return new(
            _state,
            _phaseLane,
            command,
            exiting ? _retainedSustainedBlend : presentedDirection,
            exiting ? _retainedSustainedArc : arc,
            exiting ? _retainedSustainedDomain : domain,
            permissions.MovementAllowed,
            permissions.RotationAllowed,
            _commandLane);
    }

    private static PlayerLocomotionPhaseLane SelectInitialLane(Vector2 movement)
        => movement.X > 0f ? PlayerLocomotionPhaseLane.Right : PlayerLocomotionPhaseLane.Left;

    private static bool SelectBackwardEntry(Vector2 movement)
        => movement.Y < -Mathf.Abs(movement.X) * 0.35f;

    /// <summary>
    /// Whether live movement intent reverses the travelled forward/backward domain (CTRL-001 reversal
    /// cadence): the intent selects the opposite entry side of the same forward/backward boundary the
    /// retained travelling direction sits on (<see cref="SelectBackwardEntry" />'s ratio). Pure lateral
    /// or lateral-to-forward retargets are NOT reversals — they retarget in place as before.
    /// </summary>
    private bool IsTravelReversal(Vector2 movement)
        => _retainedSustainedBlend.LengthSquared() > 0f
            && SelectBackwardEntry(movement) != SelectBackwardEntry(_retainedSustainedBlend);

    private static PlayerLocomotionState SelectTravelState(Vector2 movement, PlayerLocomotionState current)
    {
        float forward = Mathf.Max(0f, movement.Y);
        float lateralOrBackward = Mathf.Abs(movement.X) + Mathf.Max(0f, -movement.Y);
        float threshold = current == PlayerLocomotionState.TravellingForward ? ForwardDomainExitRatio : ForwardDomainEnterRatio;
        return forward > 0f && lateralOrBackward <= forward * threshold
            ? PlayerLocomotionState.TravellingForward
            : PlayerLocomotionState.TravellingDirectional;
    }

    private static Vector2 Finite(Vector2 value)
        => value.IsFinite() ? value : Vector2.Zero;
}
