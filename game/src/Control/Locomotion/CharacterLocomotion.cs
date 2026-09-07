using AlleyCat.Core.Logging;
using AlleyCat.IK;
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
    private static readonly StringName _playbackParameter = HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter();
    private static readonly StringName _startState = new("Start");
    private static readonly StringName _walkingState = new("PlayerTravel");
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
    private readonly PlayerTravelEntryHandoff _entryHandoff = new();
    private readonly PlayerTravelReleaseGate _travelReleaseGate = new();
    private CharacterBody3D? _targetBody;
    private AnimationTree? _animationTree;
    private Node3D? _rootMotionReference;
    private Vector2 _movementInput;
    private Vector2 _rotationInput;
    private bool _transitionObserved;
    private bool _travelSeamAlignmentApplied;
    private float _lastAuthoredPlanarSpeed;
    private StringName _observedTransitionStateName = new();
    private StandingLocomotionCatalogue? _catalogue;
    private bool _catalogueLoadFailed;
    private bool _hasTravelPhase;
    private double _previousTravelPhase;
    private long _travelEvaluationTick;
    private string _travelSourceIdentity = string.Empty;
    private ContactLockCoordinator? _contactLockCoordinator;
    private PlayerContactLockComposition? _contactLockComposition;
    private ulong _contactLockEvaluationTick;
    private long _contactLockClockGeneration;

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

    [ExportGroup("Sustained Blend")]
    [Export(PropertyHint.Range, "0.1,2,0.01")]
    public float MinimumPlaybackRate { get; set; } = 0.75f;

    [Export(PropertyHint.Range, "0.1,2,0.01")]
    public float MaximumPlaybackRate { get; set; } = 1f;

    /// <summary>
    /// Duration the entry-to-travel rate handoff holds its speed-matched rate before ramping toward the
    /// stick-demanded rate: the 0.12 s entry-to-travel crossfade plus a two-frame margin, mirroring the NPC
    /// entry-handoff publication window (CTRL-001 TR10: bounded playback-rate changes).
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float TravelEntryHandoffHoldSeconds { get; set; } = 0.15f;

    [ExportGroup("Residual Correction")]
    [Export(PropertyHint.Range, "0,2,0.01")]
    public float MaximumPlanarResidualRate { get; set; } = 0.35f;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MaximumPlanarResidualPerBout { get; set; } = 0.2f;

    [Export(PropertyHint.Range, "0,3.14,0.01")]
    public float MaximumYawResidualRate { get; set; } = 0.7f;

    [Export(PropertyHint.Range, "0,1.57,0.01")]
    public float MaximumYawResidualPerBout { get; set; } = 0.35f;

    [Export(PropertyHint.Range, "0,30,0.1")]
    public float ResidualSmoothingRate { get; set; } = 10f;

    /// <summary>
    /// Authored planar speed at which i2* entry residual assistance reaches full authority (CTRL-001 UR9): the
    /// weight-shift portion of an entry below this speed keeps proportionally less synthetic correction so the
    /// planted foot cannot skate.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,2,0.01")]
    public float ResidualEntryRampReference { get; set; } = 0.5f;

    [Export(PropertyHint.Range, "0,3,0.01")]
    public float DesiredPlanarSpeed { get; set; } = 1f;

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
            _travelReleaseGate.Cancel();
            _hasTravelPhase = false;
        }
    } =
        "res://assets/characters/reference/female/animations/runtime/locomotion/sad/locomotion_sad_catalogue.json";

    /// <summary>Current player lifecycle state for focused runtime verification.</summary>
    public PlayerLocomotionState PlayerState => _controller.State;

    /// <summary>Retained anatomical lane for the current movement bout.</summary>
    public PlayerLocomotionPhaseLane PhaseLane => _controller.PhaseLane;

    public override void _Ready()
    {
        base._Ready();
        ResolveRuntimeReferences();
        _travelSourceIdentity = $"{GetPath()}#{GetInstanceId()}";
        if (_rootMotionReference is null)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} '{Name}' requires exported {nameof(RootMotionReference)} to be assigned; assign the player root-motion reference or install a character module that binds it.");
        }
    }

    public override void Move(Vector2 input) => _movementInput = ManualTranslationAcceptance.ApplyRadialDeadzone(input, InputDeadzone);

    public override void Rotate(Vector2 input) => _rotationInput = ManualTranslationAcceptance.ApplyRadialDeadzone(input, InputDeadzone);

    /// <inheritdoc />
    public bool IsManualTranslationAccepted(Vector2 input)
        => ManualTranslationAcceptance.IsAccepted(input, InputDeadzone, GetCurrentLocomotionPermissions());

    /// <summary>Reports whether the production movement owner currently accepts contact-lock contributions.</summary>
    public bool IsContactLockCoordinatorBound => _contactLockCoordinator is not null;

    /// <summary>Number of coherent movement operations executed by this locomotion owner.</summary>
    public ulong MovementOperationCount
    {
        get; private set;
    }

    /// <summary>Last immutable contact correction consumption observed inside the coherent movement operation.</summary>
    public ContactLockMovementConsumptionTrace LastContactLockConsumption
    {
        get; private set;
    }
    public PlayerTravelReleaseOccurrenceV1? LastTravelReleaseOccurrence
    {
        get; private set;
    }

    /// <summary>Binds or clears the post-installer player contact-lock correction source.</summary>
    public void SetContactLockCoordinator(
        ContactLockCoordinator? coordinator,
        PlayerContactLockComposition? composition = null)
    {
        _contactLockCoordinator = coordinator;
        _contactLockComposition = coordinator is null ? null : composition;
        if (coordinator is null)
        {
            _contactLockEvaluationTick = 0;
            _contactLockClockGeneration = 0;
        }
    }

    /// <summary>Publishes the same-tick evaluated clock identity used for exact-once correction consumption.</summary>
    public void PublishContactLockEvaluation(ulong physicsTick, long clockGeneration)
    {
        _contactLockEvaluationTick = physicsTick;
        _contactLockClockGeneration = clockGeneration;
    }

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
        bool transitionCompleted = ObserveFiniteTransitionCompletion(playback);
        PlayerTravelReleaseOccurrenceV1? releaseOccurrence = ObserveTravelRelease(playback, frameDelta);
        PlayerTravelReleasePermitV1? releasePermit = null;
        if (_movementInput.IsZeroApprox()
            && releaseOccurrence is { } occurrence
            && _travelReleaseGate.TryConsume(
                occurrence,
                _travelEvaluationTick,
                ToReleaseSide(_controller.PhaseLane),
                out PlayerTravelReleasePermitV1 permit))
        {
            releasePermit = permit;
        }
        float turn = permissions.RotationAllowed
            ? Mathf.Clamp(_rotationInput.X * RotationSpeedMultiplier * SmoothTurnSensitivity, -1f, 1f)
            : 0f;
        PlayerLocomotionOutput output = _controller.Step(
            _movementInput,
            turn,
            permissions,
            transitionCompleted,
            MinimumPlaybackRate,
            MaximumPlaybackRate,
            releasePermit);

        ApplyLifecycleCommand(playback, output.Command, output.CommandLane);
        if (output.Command == PlayerLocomotionCommand.Travel)
        {
            BeginTravelEntryHandoff(output);
        }
        else if (output.Command != PlayerLocomotionCommand.None)
        {
            // Any lifecycle hop away from sustained travel (exit, idle, pivot, side step) returns rate
            // authority to the destination state immediately.
            _entryHandoff.Cancel();
        }

        float playbackRate = _entryHandoff.Active
            ? _entryHandoff.Step(output.PlaybackRate, frameDelta)
            : output.PlaybackRate;
        PublishSustainedBlend(output, playbackRate);
        ApplyMotionOnce(frameDelta, permissions, output);
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

        _entryHandoff.Cancel();
        ApplyMotionOnce(delta, permissions, default);
    }

    /// <summary>
    /// Aligns every sustained-travel blend point's custom timeline so the serving lane's declared release event
    /// sits exactly at the shared cycle seam (ANIM-002 §7.7 normalise-to-common-cycle and align-release-at-seam;
    /// CTRL-001 TR10 phase/contact/cycle-length synchronisation). The spaces are lane-dedicated by construction,
    /// so the alignment is one deterministic startup configuration rather than a per-bout reconfiguration. The
    /// entry clips end on their lane release (release at END), so the entry-to-travel reset starts travel exactly
    /// where the entry's gait phase left off instead of half a cycle away.
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

    private void BeginTravelEntryHandoff(PlayerLocomotionOutput output)
    {
        float sustainedUnitSpeed = ResolveSustainedUnitSpeed(output);
        if (sustainedUnitSpeed <= 0.0001f)
        {
            _entryHandoff.Cancel();
            return;
        }

        // The stored terminal speed is the entry's last completed frame, measured before the travel hop.
        _entryHandoff.Begin(_lastAuthoredPlanarSpeed, sustainedUnitSpeed, TravelEntryHandoffHoldSeconds);
    }

    /// <summary>
    /// Resolves the live travel blend space's rate-1 sustained root speed: the nearest blend point's full-cycle
    /// catalogue displacement over the space's common cycle length, the same analytic unit-speed measure the NPC
    /// handoff uses. The handoff blends onto one dominated sample at the boundary, so the nearest point is the
    /// honest unit measure for the arriving gait.
    /// </summary>
    private float ResolveSustainedUnitSpeed(PlayerLocomotionOutput output)
    {
        StandingLocomotionCatalogue? catalogue = ResolveLocomotionCatalogue();
        AnimationNodeBlendTree? travel = ResolvePlayerTravelTree();
        if (catalogue is null || travel is null)
        {
            return 0f;
        }

        bool rightLane = output.PhaseLane == PlayerLocomotionPhaseLane.Right;
        bool directional = output.DomainBlend > 0.5f;
        string spaceName = directional
            ? rightLane ? "DirectionalR" : "DirectionalL"
            : rightLane ? "ForwardArcR" : "ForwardArcL";
        if (travel.GetNode(new StringName(spaceName)) is not AnimationNodeBlendSpace2D space)
        {
            return 0f;
        }

        Vector2 blend = directional
            ? output.Blend
            : new(output.ForwardArcBlend, 1f - Mathf.Abs(output.ForwardArcBlend));
        int nearest = -1;
        float nearestDistance = float.PositiveInfinity;
        for (int index = 0; index < space.GetBlendPointCount(); index++)
        {
            float distance = blend.DistanceSquaredTo(space.GetBlendPointPosition(index));
            if (distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }

        if (nearest < 0)
        {
            return 0f;
        }

        string role = space.GetBlendPointName(nearest).ToString();
        return catalogue.Clips.TryGetValue(role, out StandingLocomotionClip? clip)
            ? (float)(clip.EndpointRootDisplacement.Length() / space.CyclicLength)
            : 0f;
    }

    private AnimationNodeBlendTree? ResolvePlayerTravelTree()
        => ResolvePlayerStates()?.GetNode(_walkingState) as AnimationNodeBlendTree;

    private PlayerTravelReleaseOccurrenceV1? ObserveTravelRelease(
        AnimationNodeStateMachinePlayback playback,
        float delta)
    {
        _travelEvaluationTick++;
        if (playback.GetCurrentNode() != _walkingState
            || ResolveLocomotionCatalogue() is not { } catalogue
            || ResolveEvaluatedTravelSource(catalogue) is not { } source
            || !TryResolveCompatibleExit(
                catalogue,
                source.Clip,
                ToReleaseSide(_controller.PhaseLane),
                out StandingLocomotionClip? exit,
                out string releaseEvent))
        {
            _travelReleaseGate.Cancel();
            _hasTravelPhase = false;
            return null;
        }

        float cycle = playback.GetCurrentLength();
        float position = playback.GetCurrentPlayPosition();
        float rate = _animationTree!.Get(_playbackRateParameter).AsSingle();
        if (!float.IsFinite(cycle) || cycle <= 0f || !float.IsFinite(position) || !float.IsFinite(rate) || rate <= 0f)
        {
            _travelReleaseGate.Cancel();
            _hasTravelPhase = false;
            return null;
        }

        double currentPhase = Mathf.PosMod(position, cycle) / cycle;
        double previousPhase = _hasTravelPhase ? _previousTravelPhase : currentPhase;
        _previousTravelPhase = currentPhase;
        _hasTravelPhase = true;
        PlayerTravelReleaseSide side = ToReleaseSide(_controller.PhaseLane);
        string provenance = CataloguePath;
        PlayerTravelReleaseCandidateV1 candidate = new(
            _travelSourceIdentity,
            source.Clip.Role,
            source.Clip.Identity,
            source.EvaluatedAnimation,
            PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint,
            releaseEvent,
            side,
            exit!.Role,
            exit.Identity,
            provenance,
            source.ClockTransform)
        {
            EvaluatedCapability = source.Clip.Capability,
            ExitCapability = exit.Capability,
        };
        PlayerTravelClockEvaluationV1 observation = new(
            _travelSourceIdentity,
            source.Clip.Role,
            source.Clip.Identity,
            source.EvaluatedAnimation,
            PlayerTravelEvaluatedSourceAuthority.SingularBlendPoint,
            provenance,
            side,
            _travelEvaluationTick,
            previousPhase,
            currentPhase,
            source.EvaluatedCycleSeconds,
            Math.Min((delta * rate / cycle) + 0.00001d, 0.99999d),
            PlayerTravelReleaseEvidenceOrigin.LiveEvaluatedClock,
            PlayerTravelReleaseFreshness.CurrentPhysicsTick);
        PlayerTravelReleaseOccurrenceV1? occurrence = _travelReleaseGate.Observe(candidate, observation);
        if (occurrence.HasValue)
        {
            LastTravelReleaseOccurrence = occurrence;
        }
        return occurrence;
    }

    private EvaluatedTravelSource? ResolveEvaluatedTravelSource(StandingLocomotionCatalogue catalogue)
    {
        AnimationNodeBlendTree? travel = ResolvePlayerTravelTree();
        if (travel is null)
        {
            return null;
        }

        bool rightLane = _controller.PhaseLane == PlayerLocomotionPhaseLane.Right;
        bool directional = _controller.State == PlayerLocomotionState.TravellingDirectional;
        float expectedLaneBlend = rightLane ? 1f : 0f;
        float phaseBlend = _animationTree!.Get(
            directional ? _phaseDirectionalBlendParameter : _phaseForwardBlendParameter).AsSingle();
        float domainBlend = _animationTree.Get(_domainBlendParameter).AsSingle();
        if (!float.IsFinite(phaseBlend)
            || !float.IsFinite(domainBlend)
            || Mathf.Abs(phaseBlend - expectedLaneBlend) > 0.0001f
            || Mathf.Abs(domainBlend - (directional ? 1f : 0f)) > 0.0001f)
        {
            return null;
        }

        string spaceName = directional
            ? rightLane ? "DirectionalR" : "DirectionalL"
            : rightLane ? "ForwardArcR" : "ForwardArcL";
        if (travel.GetNode(new StringName(spaceName)) is not AnimationNodeBlendSpace2D space)
        {
            return null;
        }

        Vector2 blend = directional
            ? _animationTree!.Get(rightLane ? _directionalRightBlendParameter : _directionalLeftBlendParameter).AsVector2()
            : _animationTree!.Get(rightLane ? _forwardRightBlendParameter : _forwardLeftBlendParameter).AsVector2();
        int singular = -1;
        for (int index = 0; index < space.GetBlendPointCount(); index++)
        {
            float distance = blend.DistanceSquaredTo(space.GetBlendPointPosition(index));
            if (distance > 0.00000001f)
            {
                continue;
            }

            if (singular >= 0)
            {
                return null;
            }
            singular = index;
        }

        if (singular < 0
            || space.GetBlendPointNode(singular) is not AnimationNodeAnimation animationNode)
        {
            return null;
        }

        string evaluatedAnimation = animationNode.Animation.ToString();
        int separator = evaluatedAnimation.LastIndexOf('/');
        if (separator < 0
            || !catalogue.TryGetClipByIdentity(evaluatedAnimation[(separator + 1)..], out StandingLocomotionClip? clip))
        {
            return null;
        }
        if (evaluatedAnimation != $"locomotion/{clip!.Identity}")
        {
            return null;
        }

        float evaluatedCycle = (float)space.CyclicLength;
        PlayerTravelReleaseClockTransformV1 transform = PlayerTravelSeamAlignment.ResolveReleaseClockTransform(
            clip,
            rightLane,
            evaluatedCycle);
        return transform.IsValid()
            ? new EvaluatedTravelSource(clip, evaluatedAnimation, evaluatedCycle, transform)
            : null;
    }

    private readonly record struct EvaluatedTravelSource(
        StandingLocomotionClip Clip,
        string EvaluatedAnimation,
        double EvaluatedCycleSeconds,
        PlayerTravelReleaseClockTransformV1 ClockTransform);

    private static bool TryResolveCompatibleExit(
        StandingLocomotionCatalogue catalogue,
        StandingLocomotionClip evaluated,
        PlayerTravelReleaseSide side,
        out StandingLocomotionClip? exit,
        out string releaseEvent)
    {
        bool left = side == PlayerTravelReleaseSide.Left;
        StandingLocomotionCapability exitCapability = StandingLocomotionCapabilities.Exit(
            evaluated.Capability.IsBackward(),
            left ? StandingLocomotionAnatomicalSide.Left : StandingLocomotionAnatomicalSide.Right);
        StandingLocomotionClip resolvedExit = catalogue[exitCapability];
        StandingLocomotionBoundaryMapping mapping = catalogue.ResolveBoundaryMapping(
            evaluated.Role,
            resolvedExit.Role,
            left ? StandingLocomotionAnatomicalSide.Left : StandingLocomotionAnatomicalSide.Right);
        releaseEvent = mapping.Source.ID;
        exit = resolvedExit;
        return true;
    }

    private static PlayerTravelReleaseSide ToReleaseSide(PlayerLocomotionPhaseLane lane)
        => lane == PlayerLocomotionPhaseLane.Left ? PlayerTravelReleaseSide.Left : PlayerTravelReleaseSide.Right;

    private void ApplyLifecycleCommand(
        AnimationNodeStateMachinePlayback playback,
        PlayerLocomotionCommand command,
        PlayerLocomotionPhaseLane lane)
    {
        if (playback.GetCurrentNode() == _startState)
        {
            playback.Start(IdleAnimationStateName, reset: true);
        }

        StringName state = command switch
        {
            PlayerLocomotionCommand.EnterForward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerI2FL") : new("PlayerI2FR"),
            PlayerLocomotionCommand.EnterBackward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerI2BL") : new("PlayerI2BR"),
            PlayerLocomotionCommand.Travel => _walkingState,
            PlayerLocomotionCommand.ExitForward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerF2IL") : new("PlayerF2IR"),
            PlayerLocomotionCommand.ExitBackward => lane == PlayerLocomotionPhaseLane.Left ? new("PlayerB2IL") : new("PlayerB2IR"),
            PlayerLocomotionCommand.PivotLeft => new("PlayerPivotL"),
            PlayerLocomotionCommand.PivotRight => new("PlayerPivotR"),
            PlayerLocomotionCommand.SideStepLeft => new("PlayerSideStepL"),
            PlayerLocomotionCommand.SideStepRight => new("PlayerSideStepR"),
            PlayerLocomotionCommand.Idle => IdleAnimationStateName,
            PlayerLocomotionCommand.None => new StringName(),
            _ => new StringName(),
        };
        if (!state.IsEmpty)
        {
            ApplyAnimationState(playback, command, state);
            // Pivots and side steps are held loopable manoeuvres, not finite transitions: their release is
            // input-driven, so they never arm the finite-completion observer used by the i2*/*2i handshake. The
            // observer is armed only for the commanded clip itself so the handshake cannot fire on any other
            // node's window (see ObserveFiniteTransitionCompletion).
            _transitionObserved = command is PlayerLocomotionCommand.EnterForward or PlayerLocomotionCommand.EnterBackward
                or PlayerLocomotionCommand.ExitForward or PlayerLocomotionCommand.ExitBackward;
            _observedTransitionStateName = _transitionObserved ? state : new StringName();
        }
    }

    private static void ApplyAnimationState(
        AnimationNodeStateMachinePlayback playback,
        PlayerLocomotionCommand command,
        StringName state)
    {
        if (command is PlayerLocomotionCommand.EnterForward or PlayerLocomotionCommand.EnterBackward)
        {
            TravelTo(playback, state);
        }
        else if (command is PlayerLocomotionCommand.ExitForward or PlayerLocomotionCommand.ExitBackward)
        {
            TravelTo(playback, state);
        }
        else
        {
            TravelTo(playback, state);
        }
    }

    /// <summary>
    /// Applies one lifecycle hop as a crossfaded state-machine travel instead of a teleporting restart. CTRL-001
    /// TR11: every command is issued from the state that owns its single pre-authored edge to the target, so the
    /// A* shortest path is always that one direct hop and no path-selection freedom is semantically involved. The
    /// edges keep Godot's defaults — travel-only advance mode, immediate switch, reset-on-switch — so the target
    /// clip restarts from its beginning exactly like the former Start(reset: true) while the source crossfades out.
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
        // mistaken for an exit completion before authoritative release evidence schedules that exit.
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

    private void ApplyMotionOnce(float delta, LocomotionPermissions permissions, PlayerLocomotionOutput output)
    {
        CharacterBody3D body = GetTargetBody();
        MovementOperationCount++;
        Transform3D actorBeforeMovement = body.GlobalTransform;
        Vector3 positionBeforeMovement = body.GlobalPosition;
        ContactLockMotionContribution contactContribution = default;
        bool contactConsumed = _contactLockCoordinator?.TryConsumeAuthorisedContribution(
            _contactLockEvaluationTick,
            _contactLockClockGeneration,
            out contactContribution) == true;
        Vector3 rootDelta = GetRootMotionPositionDelta();
        Vector3 rootVelocity = permissions.MovementAllowed && rootDelta.IsFinite()
            ? GetRootMotionReferenceBasis() * rootDelta / delta
            : Vector3.Zero;
        Vector2 authoredPlanar = new(rootVelocity.X, rootVelocity.Z);
        // Retained for the entry-to-travel rate handoff: the entry's terminal authored root speed is the last
        // completed entry frame's measurement, taken before the travel hop re-arms the state machine.
        _lastAuthoredPlanarSpeed = authoredPlanar.Length();
        Vector2 desiredLocal = output.Blend * DesiredPlanarSpeed;
        Vector3 desiredWorld3 = GetRootMotionReferenceBasis() * new Vector3(desiredLocal.X, 0f, -desiredLocal.Y);
        Vector2 desiredWorld = new(desiredWorld3.X, desiredWorld3.Z);
        float speedWeight = Mathf.Clamp(output.Blend.Length(), 0f, 1f);
        float phaseWeight = GetPhaseCorrectionWeight();
        Vector2 residual = permissions.MovementAllowed
            ? _controller.CalculatePlanarResidual(desiredWorld, authoredPlanar, speedWeight, phaseWeight, delta, MaximumPlanarResidualRate, MaximumPlanarResidualPerBout, ResidualSmoothingRate, ResidualEntryRampReference)
            : Vector2.Zero;

        float rootYaw = permissions.RotationAllowed ? GetRootMotionYawDelta() : 0f;
        float authoredYawRate = rootYaw / delta;
        // yawDelta shares the ANIM-002 local-Y convention (positive = counter-clockwise/left, so pr45 ends at
        // -pi/4). A rightward stick (+X per the response coordinates) therefore requests a negative local-Y rate;
        // the residual assist must use that same sign or it fights the authored arc and pivot yaw.
        float desiredYawRate = permissions.RotationAllowed
            ? -_rotationInput.X * RotationSpeedMultiplier * SmoothTurnSensitivity
            : 0f;
        float yawResidualRate = permissions.RotationAllowed
            ? _controller.CalculateYawResidual(desiredYawRate, authoredYawRate, authoredPlanar.Length(), Mathf.Max(speedWeight, Mathf.Abs(_rotationInput.X)), phaseWeight, delta, MaximumYawResidualRate, MaximumYawResidualPerBout, ResidualSmoothingRate, ResidualEntryRampReference)
            : 0f;
        float contactYaw = contactConsumed ? contactContribution.Authorised.YawRadians : 0f;
        float yawDelta = rootYaw + (yawResidualRate * delta) + contactYaw;
        if (float.IsFinite(yawDelta) && !Mathf.IsZeroApprox(yawDelta))
        {
            ApplyYawRotation(yawDelta);
        }

        Vector3 contactVelocity = contactConsumed
            ? contactContribution.Authorised.Translation / delta
            : Vector3.Zero;
        Vector2 planar = authoredPlanar + residual + new Vector2(contactVelocity.X, contactVelocity.Z);
        body.Velocity = new Vector3(planar.X, body.Velocity.Y, planar.Y);
        bool collided = body.MoveAndSlide();
        if (contactConsumed)
        {
            Vector3 realisedDisplacement = body.GlobalPosition - positionBeforeMovement;
            Vector2 nonContactPlanarDelta = (authoredPlanar + residual) * delta;
            Vector3 realisedContactTranslation = new(
                realisedDisplacement.X - nonContactPlanarDelta.X,
                0f,
                realisedDisplacement.Z - nonContactPlanarDelta.Y);
            bool realised = _contactLockCoordinator!.RecordMovementRealisation(
                contactContribution,
                realisedContactTranslation,
                permissions.RotationAllowed ? contactYaw : 0f,
                collided);
            LastContactLockConsumption = new ContactLockMovementConsumptionTrace(
                MovementOperationCount,
                contactContribution,
                realisedContactTranslation,
                permissions.RotationAllowed ? contactYaw : 0f,
                realised,
                collided,
                !realised,
                actorBeforeMovement,
                body.GlobalTransform);
            _contactLockComposition?.RecordCorrectionConsumption(LastContactLockConsumption);
        }
    }

    private float GetPhaseCorrectionWeight()
    {
        AnimationNodeStateMachinePlayback playback = ResolvePlayback();
        float length = playback.GetCurrentLength();
        if (length <= 0f)
        {
            return 0.5f;
        }
        float phase = Mathf.PosMod(playback.GetCurrentPlayPosition(), length) / length;
        return 0.35f + (0.65f * Mathf.Abs(Mathf.Cos(phase * Mathf.Tau)));
    }

    private void ResolveRuntimeReferences()
    {
        _targetBody = TargetCharacterBodyNode ?? (IsInsideTree() ? GetParentOrNull<CharacterBody3D>() : null);
        _animationTree = AnimationTree;
        _rootMotionReference = RootMotionReference;
        if (_animationTree is not null)
        {
            _animationTree.RootMotionLocal = true;
            _animationTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;
        }
        SetPhysicsProcess(_targetBody is not null && _animationTree is not null && _rootMotionReference is not null);
    }

    private AnimationNodeStateMachinePlayback ResolvePlayback()
        => _animationTree?.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>()
            ?? throw new InvalidOperationException("Player locomotion requires the nested States playback parameter.");

    private CharacterBody3D GetTargetBody()
        => _targetBody ?? throw new InvalidOperationException($"{nameof(CharacterLocomotion)} target body is not available before _Ready.");

    protected virtual Vector3 GetRootMotionPositionDelta() => _animationTree?.GetRootMotionPosition() ?? Vector3.Zero;
    protected virtual Quaternion GetRootMotionRotation() => _animationTree?.GetRootMotionRotation() ?? Quaternion.Identity;
    protected virtual float GetRootMotionYawDelta()
    {
        Quaternion rotation = GetRootMotionRotation();
        return rotation.IsFinite() ? rotation.GetEuler().Y : 0f;
    }
    protected virtual Basis GetRootMotionReferenceBasis() => _rootMotionReference?.GlobalBasis ?? Basis.Identity;
    protected virtual void ApplyYawRotation(float yawDelta) => GetTargetBody().RotateY(yawDelta);
}
