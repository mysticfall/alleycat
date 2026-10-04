using AlleyCat.Core.Logging;
using AlleyCat.Interaction.Hands;
using Godot;
using Microsoft.Extensions.Logging;

#pragma warning disable CS1591

namespace AlleyCat.Control.Locomotion;

/// <summary>Responsive player-only implementation of <see cref="ILocomotion"/>.</summary>
[GlobalClass]
public partial class CharacterLocomotion : LocomotionBase, IManualTranslationAcceptance
{
    private const int RootMotionConsumptionPhysicsPriority = 100;
    /// <summary>
    /// Time reserved when gating the stop cut for the authored stop-to-idle crossfade (0.2 s in the player
    /// tree) plus the completion handshake ticks, so the measured release-to-stop time stays inside the
    /// target even when the cut — not the natural completion — ends the stop.
    /// </summary>
    private const float StopIdleCrossfadeReserveSeconds = 0.25f;
    /// <summary>Playback rate that freezes a presentation without a pose discontinuity or further root motion.</summary>
    private const float StopPresentationFreezeRate = 0f;
    private static readonly StringName _playbackParameter = HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter();
    private static readonly StringName _startState = new("Start");
    private static readonly StringName _walkingState = new("PlayerTravel");
    private const string TurnInPlaceLeftStateName = "PlayerTurnInPlaceL";
    private const string TurnInPlaceRightStateName = "PlayerTurnInPlaceR";
    private static readonly StringName _statesNodeName = new("States");
    private static readonly StringName _forwardLeftBlendParameter = new("parameters/States/PlayerTravel/ForwardArcL/blend_position");
    private static readonly StringName _forwardRightBlendParameter = new("parameters/States/PlayerTravel/ForwardArcR/blend_position");
    private static readonly StringName _directionalLeftBlendParameter = new("parameters/States/PlayerTravel/DirectionalL/blend_position");
    private static readonly StringName _directionalRightBlendParameter = new("parameters/States/PlayerTravel/DirectionalR/blend_position");
    private static readonly StringName _phaseForwardBlendParameter = new("parameters/States/PlayerTravel/PhaseForward/blend_amount");
    private static readonly StringName _phaseDirectionalBlendParameter = new("parameters/States/PlayerTravel/PhaseDirectional/blend_amount");
    private static readonly StringName _domainBlendParameter = new("parameters/States/PlayerTravel/Domain/blend_amount");
    private static readonly StringName _playbackRateParameter = new("parameters/States/PlayerTravel/Rate/scale");
    private static readonly (string SpaceName, bool RightLane)[] _travelBlendSpaces =
    [
        ("ForwardArcL", false), ("ForwardArcR", true), ("DirectionalL", false), ("DirectionalR", true),
    ];
    private readonly PlayerLocomotionController _controller = new();
    private CharacterBody3D? _targetBody;
    private AnimationTree? _animationTree;
    private Node3D? _rootMotionReference;
    private Vector2 _movementInput;
    private Vector2 _rotationInput;
    private bool _transitionObserved;
    private bool _travelSeamAlignmentApplied;
    private float _retainedTravelPlaybackRate = 1f;
    private StringName _observedTransitionStateName = new();
    private StandingLocomotionCatalogue? _catalogue;
    private bool _catalogueLoadFailed;
    private bool _stopCutPending;
    private float _stopDisplacementAccumulatedMetres;
    private float _stopElapsedSeconds;
    private float _stopReleaseSpeed;
    private StringName _activeStopRateParameter = new();
    private float _boundSkeletonMotionScale = 1f;
    private bool _stopMotionScaleResolved;

    /// <summary>One-time-per-session guard for the stationary-turn presentation limitation diagnostic.</summary>
    private static bool _stationaryTurnLimitationReported;

    public CharacterLocomotion()
    {
        ProcessPhysicsPriority = RootMotionConsumptionPhysicsPriority;
    }

    [Export]
    public CharacterBody3D? TargetCharacterBodyNode
    {
        get; set
        {
            field = value;
            ResolveRuntimeReferences();
        }
    }
    [Export]
    public AnimationTree? AnimationTree
    {
        get; set
        {
            field = value;
            ResolveRuntimeReferences();
        }
    }
    [Export]
    public Node3D? RootMotionReference
    {
        get; set
        {
            field = value;
            ResolveRuntimeReferences();
        }
    }
    /// <summary>Default animated idle state retained by the player pose graph.</summary>
    [Export] public StringName IdleAnimationStateName { get; set; } = new("StandingCrouching");

    [Export(PropertyHint.Range, "0,20,0.01,or_greater")]
    public float RotationSpeedMultiplier { get; set; } = 1f;

    [Export(PropertyHint.Range, "0,20,0.01,or_greater")]
    public float SmoothTurnSensitivity { get; set; } = 2.5f;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float InputDeadzone { get; set; } = 0.15f;

    /// <summary>Lowest authored playback rate selected by non-zero movement demand.</summary>
    [ExportGroup("Sustained Blend")]
    [Export(PropertyHint.Range, "0.01,2,0.01")]
    public float MinimumPlaybackRate { get; set; } = 0.05f;

    /// <summary>Highest playback rate sustained travel may present at while cadence-tracking the realised body speed.</summary>
    [Export(PropertyHint.Range, "0.1,2,0.01")]
    public float MaximumPlaybackRate { get; set; } = 1f;

    /// <summary>
    /// Maximum release-to-stop seconds for one stop lane (responsiveness target). The authored stop clip's
    /// playback rate is resolved so its natural completion lands inside this budget, and an in-flight stop is
    /// cut into the authored idle return before the budget expires.
    /// </summary>
    [ExportGroup("Stop Responsiveness")]
    [Export(PropertyHint.Range, "0.05,2,0.01,or_greater")]
    public float StopTargetSeconds { get; set; } = 0.4f;

    /// <summary>
    /// Maximum post-release planar displacement metres for one stop lane. The stop-rate policy resolves the
    /// clip rate from this budget where it is tighter than the time budget, and an in-flight stop is cut once
    /// the accumulated displacement approaches it.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,2,0.005,or_greater")]
    public float StopTargetDisplacementMetres { get; set; } = 0.2f;

    /// <summary>Lowest playback rate an authored stop clip may be governed to — stops never play slower than authored.</summary>
    [Export(PropertyHint.Range, "0.01,8,0.01")]
    public float MinimumStopPlaybackRate { get; set; } = 1f;

    /// <summary>Highest playback rate an authored stop clip may be governed to.</summary>
    [Export(PropertyHint.Range, "0.1,8,0.01")]
    public float MaximumStopPlaybackRate { get; set; } = 4f;

    /// <summary>
    /// Displacement headroom reserved when gating the stop cut, covering per-tick overshoot at the governed
    /// rate and the completion handshake tick before the freeze applies.
    /// </summary>
    [Export(PropertyHint.Range, "0,0.2,0.005")]
    public float StopCutSafetyMarginMetres { get; set; } = 0.04f;

    /// <summary>
    /// Speed headroom above the release-tick body speed that an in-flight stop may present before the
    /// no-rise gate cuts it: a rate-scaled stop clip's early authored root (authored to continue walk
    /// cadence) can transiently exceed the release speed, and the stop must never accelerate the body.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float StopNoRiseToleranceMetresPerSecond { get; set; } = 0.05f;

    [ExportGroup("Locomotive Animation Catalogue")]
    [Export(PropertyHint.File, "*.json")]
    public string CataloguePath
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            _catalogue = null;
            _catalogueLoadFailed = false;
            _travelSeamAlignmentApplied = false;
        }
    } = "res://assets/characters/reference/female/animations/runtime/locomotion/sad/locomotion_sad_catalogue.json";

    /// <summary>Current player lifecycle state for focused runtime verification.</summary>
    public PlayerLocomotionState PlayerState => _controller.State;

    /// <summary>Ordinary permitted travel demand submitted on the latest physics tick.</summary>
    public Vector2 LastOrdinaryTravelDemand
    {
        get; private set;
    }

    /// <summary>Retained anatomical lane for the current movement bout.</summary>
    public PlayerLocomotionPhaseLane PhaseLane => _controller.PhaseLane;

    /// <summary>Last evaluated authored-root planar velocity in actor-semantic coordinates.</summary>
    public Vector2 AuthoredPlanarVelocity
    {
        get; private set;
    }

    /// <summary>Last evaluated authored-root local-Y yaw rate.</summary>
    public float AuthoredYawRate
    {
        get; private set;
    }

    /// <summary>Whether the last evaluated authored-root interval produced no planar or yaw motion.</summary>
    public bool AuthoredRootSettled => AuthoredPlanarVelocity.IsZeroApprox() && Mathf.IsZeroApprox(AuthoredYawRate);

    /// <summary>Last requested evaluated authored-root world displacement.</summary>
    public Vector3 LastAuthoredRootWorldDelta
    {
        get; private set;
    }

    /// <summary>Last collision-realised world displacement; this is measured from the body transform.</summary>
    public Vector3 LastRealisedWorldDelta
    {
        get; private set;
    }

    /// <summary>Last complete planar movement requested from the single collision operation.</summary>
    public Vector3 LastRequestedWorldDelta
    {
        get; private set;
    }

    /// <summary>Number of coherent movement operations executed by this locomotion owner.</summary>
    public ulong MovementOperationCount
    {
        get; private set;
    }

    public override void _Ready()
    {
        base._Ready();
        ResolveRuntimeReferences();
        if (_rootMotionReference is null)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} '{Name}' requires exported {nameof(RootMotionReference)} to be assigned; assign the player root-motion reference or install a character module that binds it.");
        }
    }

    public override void Move(Vector2 input)
        => _movementInput = ManualTranslationAcceptance.ApplyRadialDeadzone(input, InputDeadzone);

    public override void Rotate(Vector2 input)
        => _rotationInput = ManualTranslationAcceptance.ApplyRadialDeadzone(input, InputDeadzone);

    /// <inheritdoc />
    public bool IsManualTranslationAccepted(Vector2 input)
        => ManualTranslationAcceptance.IsAccepted(input, InputDeadzone, GetCurrentLocomotionPermissions());

    public override void _PhysicsProcess(double delta)
    {
        if (delta <= 0d || _animationTree is null)
        {
            return;
        }

        float frameDelta = (float)delta;
        ApplyTravelSeamAlignmentOnce();
        LocomotionPermissions permissions = GetCurrentLocomotionPermissions();
        LocomotionStateTarget? poseOverride = GetLocomotionStateTarget();
        if (poseOverride.HasValue)
        {
            ProcessPoseOverride(poseOverride.Value, permissions, frameDelta);
            return;
        }

        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        PlayerLocomotionState previousState = _controller.State;
        // The pending stop cut completes exactly one lifecycle handshake: consume it here so a later
        // finite transition (for example the entry a fresh intent just started) cannot fire on the
        // previous stop's completion.
        bool stopCutPending = _stopCutPending;
        _stopCutPending = false;
        bool transitionCompleted = ObserveFiniteTransitionCompletion(playback)
            || stopCutPending
            || ObserveNeutralExitCompletion(playback);
        float turn = permissions.RotationAllowed
            ? Mathf.Clamp(_rotationInput.X * RotationSpeedMultiplier * SmoothTurnSensitivity, -1f, 1f)
            : 0f;

        Vector2 permittedMovement = permissions.MovementAllowed ? _movementInput : Vector2.Zero;
        LastOrdinaryTravelDemand = permittedMovement;
        PlayerLocomotionOutput output = _controller.Step(
            _movementInput,
            AuthoredPlanarVelocity,
            turn,
            permissions,
            transitionCompleted);
        ApplyLifecycleCommand(playback, output.Command, output.CommandLane, previousState);
        PublishSustainedBlend(output, ResolveTravelPlaybackRate(output, permittedMovement.Length()));
        _animationTree.Advance(delta);
        ApplyMotionOnce(frameDelta, permissions);
        UpdateStopCutGating(frameDelta);
        ReportStationaryTurnPresentationLimit(permittedMovement, turn);
    }

    private void ProcessPoseOverride(LocomotionStateTarget target, LocomotionPermissions permissions, float delta)
    {
        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        bool moving = permissions.MovementAllowed && !_movementInput.IsZeroApprox();
        StringName targetState = moving ? target.MovementStateName : target.IdleStateName;
        if (playback.GetCurrentNode() != targetState)
        {
            playback.Start(targetState, reset: true);
        }

        _animationTree!.Advance(delta);
        ApplyMotionOnce(delta, permissions);
    }

    /// <summary>
    /// Aligns every sustained-travel blend point's custom timeline so the serving lane's declared release event
    /// sits exactly at the shared cycle seam (ANIM-002 §7.7 normalise-to-common-cycle and align-release-at-seam).
    /// The spaces are lane-dedicated by construction, so the alignment is one deterministic startup configuration
    /// rather than a per-bout reconfiguration. The entry clips end on their lane release (release at END), so the
    /// entry-to-travel reset starts travel exactly where the entry's gait phase left off instead of half a cycle away.
    /// </summary>
    private void ApplyTravelSeamAlignmentOnce()
    {
        if (_travelSeamAlignmentApplied)
        {
            return;
        }

        _travelSeamAlignmentApplied = true;
        StandingLocomotionCatalogue? catalogue = ResolveLocomotionCatalogue();
        AnimationNodeBlendTree? travel = ResolvePlayerTravelTree();
        if (catalogue is null || travel is null)
        {
            // A missing catalogue already logs through DisableLocomotionCatalogue; travel keeps the unaligned
            // shared clock rather than failing locomotion outright.
            return;
        }

        foreach ((string spaceName, bool rightLane) in _travelBlendSpaces)
        {
            if (travel.GetNode(new StringName(spaceName)) is not AnimationNodeBlendSpace2D space)
            {
                continue;
            }

            float cycle = (float)space.CyclicLength;
            for (int index = 0; index < space.GetBlendPointCount(); index++)
            {
                if (space.GetBlendPointNode(index) is not AnimationNodeAnimation node)
                {
                    continue;
                }

                string role = space.GetBlendPointName(index).ToString();
                if (!catalogue.Clips.TryGetValue(role, out StandingLocomotionClip? clip))
                {
                    continue;
                }

                node.UseCustomTimeline = true;
                node.TimelineLength = cycle;
                node.StretchTimeScale = true;
                node.LoopMode = Animation.LoopModeEnum.Linear;
                node.StartOffset = PlayerTravelSeamAlignment.ResolveStartOffset(clip, rightLane, cycle);
            }
        }

        _animationTree?.ClearCaches();
    }

    /// <summary>
    /// Resolves the sustained travel playback rate for one tick (CTRL-001 TR4): the demanded movement
    /// magnitude scheduled onto the bounded [<see cref="MinimumPlaybackRate" />, <see cref=
    /// "MaximumPlaybackRate" />] range — a demand-magnitude schedule, with the realised-motion
    /// coupling confined to the fixture-declared unit-speed checks the cadence probes assert. An
    /// authored stop fading the travel tree out keeps the last published travel rate so the crossfade
    /// does not step the cadence (a reversal's authored braking stop therefore plays at the travelled
    /// cadence while the body decelerates). The neutral stop freezes the fading travel presentation
    /// outright: its remaining realised motion is already ≈0, so the presented gait agrees with realised
    /// motion by standing still through the crossfade.
    /// </summary>
    private float ResolveTravelPlaybackRate(PlayerLocomotionOutput output, float demandMagnitude)
    {
        float minimumRate = Mathf.Max(0.01f, MinimumPlaybackRate);
        float maximumRate = Mathf.Max(minimumRate, MaximumPlaybackRate);
        float coupledRate = Mathf.Lerp(minimumRate, maximumRate, Mathf.Clamp(demandMagnitude, 0f, 1f));

        if (output.State is PlayerLocomotionState.TravellingForward or PlayerLocomotionState.TravellingDirectional)
        {
            _retainedTravelPlaybackRate = coupledRate;
            return coupledRate;
        }

        return output.State is PlayerLocomotionState.ExitingForward or PlayerLocomotionState.ExitingBackward
            ? _retainedTravelPlaybackRate
            : (output.State == PlayerLocomotionState.ExitingNeutral
                ? StopPresentationFreezeRate
                : coupledRate);
    }

    /// <summary>
    /// Reports the stationary-turn presentation limitation once per session: the player graph wires no continuous
    /// stationary-turn content, so a stationary stick turn presents the idle pose while the actor rotates
    /// (CTRL-001 TR4 requires the degradation to be reported with evidence rather than silently accepted). The
    /// physical response is unaffected — yaw follows the continuous demand regardless.
    /// </summary>
    private void ReportStationaryTurnPresentationLimit(Vector2 movement, float turn)
    {
        if (_stationaryTurnLimitationReported
            || _controller.State != PlayerLocomotionState.Idle
            || !movement.IsZeroApprox()
            || Mathf.IsZeroApprox(turn))
        {
            return;
        }

        // The presentation exists: the degradation is reported only when the authored turn-in-place states
        // are genuinely absent from the wired player tree (a content-wiring gap), not while they present.
        AnimationNodeStateMachine? states = ResolvePlayerStates();
        if (states is not null
            && states.HasNode(new StringName(TurnInPlaceLeftStateName))
            && states.HasNode(new StringName(TurnInPlaceRightStateName)))
        {
            return;
        }

        _stationaryTurnLimitationReported = true;
        if (GameLoggerResolver.TryResolve(out ILogger<CharacterLocomotion>? logger) && logger is not null)
        {
            logger.LogWarning(
                "Stationary authored turning is unavailable: no compatible stationary-turn presentation is wired, so CTRL remains stationary and reports the finite degradation instead of applying synthetic yaw.");
        }
    }

    private AnimationNodeBlendTree? ResolvePlayerTravelTree()
        => ResolvePlayerStates()?.GetNode(_walkingState) as AnimationNodeBlendTree;

    private void ApplyLifecycleCommand(
        AnimationNodeStateMachinePlayback playback,
        PlayerLocomotionCommand command,
        PlayerLocomotionPhaseLane lane,
        PlayerLocomotionState previousState)
    {
        if (playback.GetCurrentNode() == _startState)
        {
            playback.Start(IdleAnimationStateName, reset: true);
        }

        bool finiteTransition = command is PlayerLocomotionCommand.EnterForward or PlayerLocomotionCommand.EnterBackward
            or PlayerLocomotionCommand.ExitForward or PlayerLocomotionCommand.ExitBackward;
        StringName state = ResolveCommandState(command, lane);
        if (state.IsEmpty)
        {
            return;
        }

        // Missing authored content fails closed to idle; no synthetic movement fallback exists.
        if (ResolvePlayerStates()?.HasNode(state) != true)
        {
            state = IdleAnimationStateName;
            finiteTransition = false;
        }

        if (command is PlayerLocomotionCommand.ExitForward
            or PlayerLocomotionCommand.ExitBackward
            or PlayerLocomotionCommand.ExitNeutral)
        {
            ConfigureStopPresentation(command, lane, state);
        }

        // A return to idle from an authored stop, the neutral stop, or from the turn-in-place presentation
        // follows the authored transition (crossfaded); only a genuine permission loss drops straight to
        // idle without one.
        bool authoredReturnToIdle = command == PlayerLocomotionCommand.Idle
            && previousState is PlayerLocomotionState.ExitingForward or PlayerLocomotionState.ExitingBackward
                or PlayerLocomotionState.ExitingNeutral or PlayerLocomotionState.TurningInPlace;
        if (command == PlayerLocomotionCommand.Idle && !authoredReturnToIdle)
        {
            // Permission loss drops straight to idle without a crossfade.
            if (playback.GetCurrentNode() != state)
            {
                playback.Start(state, reset: true);
            }
        }
        else
        {
            // Starts, travel, stops, and the stop's return to idle follow the authored transitions.
            TravelTo(playback, state);
        }

        // The completion observer is armed only for the commanded finite clip itself so the lifecycle handshake
        // cannot fire on any other node's window (see ObserveFiniteTransitionCompletion).
        _transitionObserved = finiteTransition;
        _observedTransitionStateName = _transitionObserved ? state : new StringName();
    }

    private StringName ResolveCommandState(PlayerLocomotionCommand command, PlayerLocomotionPhaseLane lane)
        => command switch
        {
            PlayerLocomotionCommand.EnterForward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerI2FL") : new("PlayerI2FR"),
            PlayerLocomotionCommand.EnterBackward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerI2BL") : new("PlayerI2BR"),
            PlayerLocomotionCommand.Travel => _walkingState,
            PlayerLocomotionCommand.ExitForward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerF2IL") : new("PlayerF2IR"),
            PlayerLocomotionCommand.ExitBackward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerB2IL") : new("PlayerB2IR"),
            PlayerLocomotionCommand.ExitNeutral => IdleAnimationStateName,
            PlayerLocomotionCommand.TurnLeft => new(TurnInPlaceLeftStateName),
            PlayerLocomotionCommand.TurnRight => new(TurnInPlaceRightStateName),
            PlayerLocomotionCommand.Idle => IdleAnimationStateName,
            PlayerLocomotionCommand.None => new StringName(),
            _ => new StringName(),
        };

    /// <summary>
    /// Stop-rate governance (CTRL-001 responsiveness): resolves and publishes the playback rate for the
    /// stop clip a lane just entered, and resets the in-flight stop budget. The rate compresses the authored
    /// clip so its natural completion meets the configured release-to-stop time and post-release
    /// displacement targets; the rate-failure fallback is the authored 1x playback. The neutral exit
    /// carries no stop clip — its presentation freeze is published through the sustained travel rate.
    /// </summary>
    private void ConfigureStopPresentation(PlayerLocomotionCommand command, PlayerLocomotionPhaseLane lane, StringName state)
    {
        _stopCutPending = false;
        _stopDisplacementAccumulatedMetres = 0f;
        _stopElapsedSeconds = 0f;
        _stopReleaseSpeed = AuthoredPlanarVelocity.Length();
        if (command == PlayerLocomotionCommand.ExitNeutral)
        {
            _activeStopRateParameter = new StringName();
            return;
        }

        bool backward = command == PlayerLocomotionCommand.ExitBackward;
        float rate = ResolveAuthoredStopRate(backward, lane);
        _activeStopRateParameter = new StringName($"parameters/States/{state}/Rate/scale");
        _animationTree?.Set(_activeStopRateParameter, rate);
    }

    private float ResolveAuthoredStopRate(bool backward, PlayerLocomotionPhaseLane lane)
    {
        StandingLocomotionCatalogue? catalogue = ResolveLocomotionCatalogue();
        if (catalogue is null)
        {
            return 1f;
        }

        StandingLocomotionAnatomicalSide side = lane == PlayerLocomotionPhaseLane.Right
            ? StandingLocomotionAnatomicalSide.Right
            : StandingLocomotionAnatomicalSide.Left;
        StandingLocomotionCapability capability = StandingLocomotionCapabilities.Exit(backward, side);
        if (!catalogue.Capabilities.TryGetValue(capability, out StandingLocomotionClip? clip) || clip is null)
        {
            return 1f;
        }

        // ANIM-002 scale rule: player consumers of catalogue root-trajectory positions multiply by the
        // bound skeleton's motion scale; yaw is not scaled.
        Vector3 endpoint = clip.EndpointRootDisplacement;
        float displacementMetres = new Vector2(endpoint.X, endpoint.Z).Length() * ResolveBoundSkeletonMotionScale();
        return PlayerStopPolicy.ResolveStopPlaybackRate(
            (float)clip.DurationSeconds,
            displacementMetres,
            StopTargetSeconds,
            StopTargetDisplacementMetres,
            MinimumStopPlaybackRate,
            MaximumStopPlaybackRate);
    }

    private float ResolveBoundSkeletonMotionScale()
    {
        if (_stopMotionScaleResolved)
        {
            return _boundSkeletonMotionScale;
        }

        _stopMotionScaleResolved = true;
        if (_rootMotionReference is not null)
        {
            foreach (Node child in _rootMotionReference.FindChildren("*", "Skeleton3D", true, false))
            {
                if (child is Skeleton3D skeleton)
                {
                    _boundSkeletonMotionScale = skeleton.MotionScale;
                    break;
                }
            }
        }

        return _boundSkeletonMotionScale;
    }

    /// <summary>
    /// Gates an in-flight authored stop against the responsiveness budgets: once the accumulated
    /// post-release displacement approaches the displacement target, or the elapsed stop time approaches
    /// the time target minus the idle-crossfade reserve, the stop clip is frozen (a zero playback rate
    /// halts playback with no pose discontinuity and no further root displacement) and the lifecycle
    /// completes into the authored idle return on the next tick. Release-speed continuity is not gated
    /// here: the UR4 adaptation clamp in <see cref="ApplyMotionOnce" /> enforces it on the adapted root
    /// itself, before this gate could observe a violation.
    /// </summary>
    private void UpdateStopCutGating(float delta)
    {
        if (_controller.State is not (PlayerLocomotionState.ExitingForward or PlayerLocomotionState.ExitingBackward)
            || _activeStopRateParameter.IsEmpty)
        {
            return;
        }

        _stopElapsedSeconds += delta;
        _stopDisplacementAccumulatedMetres += new Vector2(LastRealisedWorldDelta.X, LastRealisedWorldDelta.Z).Length();
        float displacementLimit = Mathf.Max(0.02f, StopTargetDisplacementMetres - StopCutSafetyMarginMetres);
        float timeLimit = Mathf.Max(0.05f, StopTargetSeconds - StopIdleCrossfadeReserveSeconds);
        if (_stopDisplacementAccumulatedMetres < displacementLimit && _stopElapsedSeconds < timeLimit)
        {
            return;
        }

        _animationTree?.Set(_activeStopRateParameter, StopPresentationFreezeRate);
        _stopCutPending = true;
    }

    /// <summary>
    /// The neutral exit has no finite stop clip to observe: it completes once the authored crossfade into
    /// idle has taken over the graph and its fading source has drained.
    /// </summary>
    private bool ObserveNeutralExitCompletion(AnimationNodeStateMachinePlayback playback)
        => _controller.State == PlayerLocomotionState.ExitingNeutral
            && playback.GetCurrentNode() == IdleAnimationStateName
            && playback.GetFadingFromNode().IsEmpty;

    /// <summary>
    /// Applies one lifecycle hop as a crossfaded state-machine travel instead of a teleporting restart. Every command
    /// is issued from a state that owns a direct edge to its target, so the A* shortest path is always that one hop
    /// and interruption never routes through an unwanted finite exit, idle, or entry path. The edges keep Godot's
    /// defaults — travel-only advance mode, immediate switch, reset-on-switch — so the target clip restarts from its
    /// beginning exactly like the former Start(reset: true) while the source crossfades out.
    /// </summary>
    private static void TravelTo(AnimationNodeStateMachinePlayback playback, StringName state)
    {
        if (playback.GetCurrentNode() == state)
        {
            // Defensive no-op: re-issuing the already-current state must not restart it mid-crossfade (the
            // transitions' default reset would otherwise snap the target back to its first frame).
            return;
        }

        playback.Travel(state, resetOnTeleport: false);
    }

    private AnimationNodeStateMachine? ResolvePlayerStates()
        => (_animationTree?.TreeRoot as AnimationNodeBlendTree)?.GetNode(_statesNodeName) as AnimationNodeStateMachine;

    private StandingLocomotionCatalogue? ResolveLocomotionCatalogue()
    {
        if (_catalogue is not null || _catalogueLoadFailed)
        {
            return _catalogue;
        }

        if (string.IsNullOrWhiteSpace(CataloguePath))
        {
            DisableLocomotionCatalogue("no catalogue path is configured");
            return null;
        }

        using var file = Godot.FileAccess.Open(CataloguePath, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            DisableLocomotionCatalogue($"could not open '{CataloguePath}'");
            return null;
        }

        try
        {
            _catalogue = StandingLocomotionCatalogue.Parse(file.GetAsText(), CataloguePath);
            _catalogue.RequireCapabilities(
                nameof(CharacterLocomotion),
                Enum.GetValues<StandingLocomotionCapability>());
            return _catalogue;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            DisableLocomotionCatalogue($"could not parse '{CataloguePath}': {exception.Message}");
            return null;
        }
    }

    private void DisableLocomotionCatalogue(string detail)
    {
        _catalogueLoadFailed = true;
        if (GameLoggerResolver.TryResolve(out ILogger<CharacterLocomotion>? logger) && logger is not null)
        {
            logger.LogError(
                "Player locomotive animation catalogue disabled ({Detail}); travel-seam alignment and speed matching are unavailable.",
                detail);
        }
    }

    private bool ObserveFiniteTransitionCompletion(AnimationNodeStateMachinePlayback playback)
    {
        if (!_transitionObserved)
        {
            return false;
        }

        // The handshake must only complete on the command's own finite state, so a completed entry cannot be
        // mistaken for an exit completion before the lifecycle schedules that exit.
        if (playback.GetCurrentNode() != _observedTransitionStateName)
        {
            return false;
        }

        float length = playback.GetCurrentLength();
        bool completed = length > 0f && playback.GetCurrentPlayPosition() >= length - 0.001f;
        if (completed)
        {
            _transitionObserved = false;
            _observedTransitionStateName = new StringName();
        }
        return completed;
    }

    private void PublishSustainedBlend(PlayerLocomotionOutput output, float playbackRate)
    {
        Vector2 forwardArcBlend = new(output.ForwardArcBlend, 1f - Mathf.Abs(output.ForwardArcBlend));
        _animationTree!.Set(_forwardLeftBlendParameter, forwardArcBlend);
        _animationTree.Set(_forwardRightBlendParameter, forwardArcBlend);
        _animationTree.Set(_directionalLeftBlendParameter, output.Blend);
        _animationTree.Set(_directionalRightBlendParameter, output.Blend);
        float phaseLane = output.PhaseLane == PlayerLocomotionPhaseLane.Right ? 1f : 0f;
        _animationTree.Set(_phaseForwardBlendParameter, phaseLane);
        _animationTree.Set(_phaseDirectionalBlendParameter, phaseLane);
        _animationTree.Set(_domainBlendParameter, output.DomainBlend);
        _animationTree.Set(_playbackRateParameter, playbackRate);
    }

    /// <summary>Consumes the evaluated authored root exactly once in the actor's single collision operation.</summary>
    private void ApplyMotionOnce(float delta, LocomotionPermissions permissions)
    {
        CharacterBody3D body = GetTargetBody();
        MovementOperationCount++;
        Vector3 positionBeforeMovement = body.GlobalPosition;
        Vector3 evaluatedRoot = _animationTree!.GetRootMotionPosition();
        Quaternion evaluatedRotation = _animationTree.GetRootMotionRotation();
        // CTRL-001 rig-independent actor-frame semantics: the authored delta is expressed in the ANIM-002
        // skeleton-authoring convention (avatar forward = skeleton-local +Z, avatar right = −X — the same
        // FIXED convention AuthoredPlanarVelocity applies below), never in the rig container's runtime basis.
        // The conversion composes that fixed convention with the ACTOR's world basis, so identically-authored
        // clips move the actor identically whatever the rig node's own yaw is (the identity-rig control), and
        // for the production rig — whose container's local basis under the body IS this 180° convention —
        // the product equals the previous reference-global-basis conversion exactly, keeping every production
        // cardinal/rotated behaviour unchanged.
        Vector3 semanticDelta = new(-evaluatedRoot.X, 0f, -evaluatedRoot.Z);
        Vector3 authoredWorldDelta = permissions.MovementAllowed && evaluatedRoot.IsFinite()
            ? body.GlobalBasis.Orthonormalized() * semanticDelta
            : Vector3.Zero;
        authoredWorldDelta.Y = 0f;
        // Stop-responsiveness continuity (CTRL-001 UR4 adaptation): while any stop presents — an authored
        // stop clip, or the neutral stop whose fading source may be an entry clip with no rate
        // governance — the adapted root may not accelerate the body beyond the release-tick speed plus
        // the no-rise tolerance. A rate-scaled stop clip's early authored root (authored to continue
        // walk cadence) and a draining entry clip's root would otherwise surge the body — most visibly
        // when the release speed is low.
        if (_controller.State is PlayerLocomotionState.ExitingForward
            or PlayerLocomotionState.ExitingBackward
            or PlayerLocomotionState.ExitingNeutral)
        {
            float limit = (_stopReleaseSpeed + StopNoRiseToleranceMetresPerSecond) * delta;
            Vector2 planar = new(authoredWorldDelta.X, authoredWorldDelta.Z);
            if (planar.Length() > limit)
            {
                Vector2 limited = planar.LengthSquared() > 0f ? planar.Normalized() * limit : Vector2.Zero;
                authoredWorldDelta = new Vector3(limited.X, 0f, limited.Y);
            }
        }
        float authoredYaw = permissions.RotationAllowed && evaluatedRotation.IsFinite()
            ? evaluatedRotation.GetEuler().Y
            : 0f;
        if (!float.IsFinite(authoredYaw))
        {
            authoredYaw = 0f;
        }

        LastAuthoredRootWorldDelta = authoredWorldDelta;
        AuthoredPlanarVelocity = delta > 0f
            ? new Vector2(-evaluatedRoot.X, evaluatedRoot.Z) / delta
            : Vector2.Zero;
        AuthoredYawRate = delta > 0f ? authoredYaw / delta : 0f;
        if (!Mathf.IsZeroApprox(authoredYaw))
        {
            ApplyYawRotation(authoredYaw);
        }

        LastRequestedWorldDelta = authoredWorldDelta;
        Vector3 planarVelocity = authoredWorldDelta / delta;
        body.Velocity = new Vector3(planarVelocity.X, body.Velocity.Y, planarVelocity.Z);
        _ = body.MoveAndSlide();
        LastRealisedWorldDelta = body.GlobalPosition - positionBeforeMovement;
    }

    private void ResolveRuntimeReferences()
    {
        _targetBody = TargetCharacterBodyNode ?? (IsInsideTree() ? GetParentOrNull<CharacterBody3D>() : null);
        _animationTree = AnimationTree;
        _rootMotionReference = RootMotionReference;
        if (_animationTree is not null)
        {
            _animationTree.RootMotionLocal = true;
            _animationTree.Set("process_callback", (int)AnimationMixer.AnimationCallbackModeProcess.Manual);
            _animationTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        }
        SetPhysicsProcess(_targetBody is not null && _animationTree is not null && _rootMotionReference is not null);
    }

    private AnimationNodeStateMachinePlayback ResolvePlayback()
        => _animationTree?.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>()
            ?? throw new InvalidOperationException("Player locomotion requires the nested States playback parameter.");

    private CharacterBody3D GetTargetBody()
        => _targetBody ?? throw new InvalidOperationException($"{nameof(CharacterLocomotion)} target body is not available before _Ready.");

    protected virtual void ApplyYawRotation(float yawDelta) => GetTargetBody().RotateY(yawDelta);

}
