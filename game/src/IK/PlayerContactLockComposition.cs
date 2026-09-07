using System.Runtime.ExceptionServices;
using AlleyCat.Control.Locomotion;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.IK;

#pragma warning disable CS1591

/// <summary>Post-installer production player publisher and contact-lock composition owner.</summary>
[GlobalClass]
public partial class PlayerContactLockComposition : Node
{
    private static readonly StringName _playbackParameter = HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter();
    private ContactLockProductionBinding? _binding;
    private Node3D? _actor;
    private CharacterIK? _characterIK;
    private CharacterLocomotion? _locomotion;
    private AnimationTree? _animationTree;
    private AnimationNodeStateMachine? _states;
    private StandingLocomotionCatalogue? _catalogue;
    private Skeleton3D? _skeleton;
    private TwoBoneIK3D? _leftNativeModifier;
    private TwoBoneIK3D? _rightNativeModifier;
    private int _leftFootBoneIndex = -1;
    private int _rightFootBoneIndex = -1;
    private IK003NativeSignalObservation _leftNative = IK003NativeSignalObservation.Empty;
    private IK003NativeSignalObservation _rightNative = IK003NativeSignalObservation.Empty;
    private ContactLockMovementConsumptionTrace _lastConsumption;
    private StringName _lastEvaluatedState = new();
    private long _clockGeneration = 1;
    private ulong _evaluationTick;
    private ulong _lastCompleteTraceTick;

    public ContactLockCoordinator? Coordinator => _binding?.Coordinator;
    public ContactLockFootTargetProvider? LeftProvider => _binding?.LeftProvider;
    public ContactLockFootTargetProvider? RightProvider => _binding?.RightProvider;
    public bool IsInstalled => _binding is not null;
    public PlayerContactLockTraceV1 LastTrace
    {
        get; private set;
    }
    public ulong ContactPublicationCount
    {
        get; private set;
    }
    public ulong FiniteEvaluationCount
    {
        get; private set;
    }
    public ulong CatalogueEvaluationCount
    {
        get; private set;
    }
    public ulong ProviderPreparationFailureCount
    {
        get; private set;
    }
    public ulong ConsumedCorrectionCount
    {
        get; private set;
    }
    public ulong CollisionDivergenceCount
    {
        get; private set;
    }
    public ulong CompleteTraceCount
    {
        get; private set;
    }

    public event Action<PlayerContactLockTraceV1>? TraceRecorded;

    [Export(PropertyHint.File, "*.json")]
    public string ContactEvidencePathOverride { get; set; } = string.Empty;

    [Export]
    public float MaximumContactTranslation
    {
        get; set;
    }

    public bool Enabled
    {
        get; set
        {
            field = value;
            SetPhysicsProcess(value);
            if (!value)
            {
                _binding?.Coordinator.Cancel();
                _locomotion?.SetContactLockCoordinator(null);
                _lastEvaluatedState = new StringName();
                _clockGeneration++;
            }
            else if (_binding is not null)
            {
                _locomotion?.SetContactLockCoordinator(_binding.Coordinator, this);
            }
        }
    } = true;

    public override void _Ready()
    {
        ProcessPhysicsPriority = 99;
        SetPhysicsProcess(Enabled);
    }

    public void Configure(
        Node3D actor,
        CharacterIK characterIK,
        CharacterLocomotion locomotion,
        AnimationTree animationTree,
        Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(characterIK);
        ArgumentNullException.ThrowIfNull(locomotion);
        ArgumentNullException.ThrowIfNull(animationTree);
        ArgumentNullException.ThrowIfNull(skeleton);
        PreparedConfiguration prepared = PrepareConfiguration(locomotion, animationTree, skeleton);
        ContactLockProductionBinding? provisionalBinding = null;
        bool provisionalLeftSubscription = false;
        bool provisionalRightSubscription = false;
        bool reuseLeftSubscription = _binding is not null && ReferenceEquals(_leftNativeModifier, prepared.LeftNativeModifier);
        bool reuseRightSubscription = _binding is not null && ReferenceEquals(_rightNativeModifier, prepared.RightNativeModifier);
        try
        {
            provisionalBinding = ContactLockProductionComposition.Install(
                actor,
                characterIK,
                prepared.Coordinator,
                characterIK.LeftFootFallbackIntentProvider,
                characterIK.RightFootFallbackIntentProvider);
            if (!reuseLeftSubscription)
            {
                prepared.LeftNativeModifier.ModificationProcessed += OnLeftNativeModificationProcessed;
                provisionalLeftSubscription = true;
            }
            if (!reuseRightSubscription)
            {
                prepared.RightNativeModifier.ModificationProcessed += OnRightNativeModificationProcessed;
                provisionalRightSubscription = true;
            }
        }
        catch (Exception configurationError)
        {
            RollbackProvisionalConfiguration(
                configurationError,
                provisionalBinding,
                prepared,
                provisionalLeftSubscription,
                provisionalRightSubscription);
            throw;
        }

        ContactLockProductionBinding? previousBinding = _binding;
        CharacterLocomotion? previousLocomotion = _locomotion;
        TwoBoneIK3D? previousLeftNativeModifier = _leftNativeModifier;
        TwoBoneIK3D? previousRightNativeModifier = _rightNativeModifier;
        previousBinding?.TransferRestorationStateTo(provisionalBinding);
        previousBinding?.Teardown();
        if (!reuseLeftSubscription && previousLeftNativeModifier is not null)
        {
            previousLeftNativeModifier.ModificationProcessed -= OnLeftNativeModificationProcessed;
        }
        if (!reuseRightSubscription && previousRightNativeModifier is not null)
        {
            previousRightNativeModifier.ModificationProcessed -= OnRightNativeModificationProcessed;
        }
        previousLocomotion?.SetContactLockCoordinator(null);

        _binding = provisionalBinding;
        provisionalBinding = null;
        _actor = actor;
        _characterIK = characterIK;
        _locomotion = locomotion;
        _animationTree = animationTree;
        _skeleton = skeleton;
        _states = prepared.States;
        _catalogue = prepared.Catalogue;
        _leftNativeModifier = prepared.LeftNativeModifier;
        _rightNativeModifier = prepared.RightNativeModifier;
        _leftFootBoneIndex = prepared.LeftFootBoneIndex;
        _rightFootBoneIndex = prepared.RightFootBoneIndex;
        locomotion.SetContactLockCoordinator(Enabled ? prepared.Coordinator : null, Enabled ? this : null);
        _lastEvaluatedState = new StringName();
        _clockGeneration++;
        ContactPublicationCount = 0;
        FiniteEvaluationCount = 0;
        CatalogueEvaluationCount = 0;
        ProviderPreparationFailureCount = 0;
        ConsumedCorrectionCount = 0;
        CollisionDivergenceCount = 0;
        CompleteTraceCount = 0;
        _lastConsumption = default;
        _leftNative = IK003NativeSignalObservation.Empty;
        _rightNative = IK003NativeSignalObservation.Empty;
        _lastCompleteTraceTick = 0;
        LastTrace = default;
    }

    private PreparedConfiguration PrepareConfiguration(
        CharacterLocomotion locomotion,
        AnimationTree animationTree,
        Skeleton3D skeleton)
    {
        AnimationNodeStateMachine states = (animationTree.TreeRoot as AnimationNodeBlendTree)?.GetNode("States") as AnimationNodeStateMachine
            ?? throw new InvalidOperationException("Player contact-lock composition requires the production States animation machine.");
        string json = Godot.FileAccess.GetFileAsString(locomotion.CataloguePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"Player contact-lock composition requires catalogue '{locomotion.CataloguePath}'.");
        }

        var catalogue = StandingLocomotionCatalogue.Parse(json, locomotion.CataloguePath);
        TwoBoneIK3D leftNativeModifier = skeleton.GetNodeOrNull<TwoBoneIK3D>("LeftLegTwoBoneIKController")
            ?? throw new InvalidOperationException("Player contact-lock traces require the left production TwoBoneIK3D modifier.");
        TwoBoneIK3D rightNativeModifier = skeleton.GetNodeOrNull<TwoBoneIK3D>("RightLegTwoBoneIKController")
            ?? throw new InvalidOperationException("Player contact-lock traces require the right production TwoBoneIK3D modifier.");
        int leftFootBoneIndex = skeleton.FindBone("LeftFoot");
        int rightFootBoneIndex = skeleton.FindBone("RightFoot");
        _ = leftFootBoneIndex >= 0 && rightFootBoneIndex >= 0
            ? true
            : throw new InvalidOperationException("Player contact-lock traces require both production foot bones.");

        return new(
            states,
            catalogue,
            ContactLockProductionComposition.LoadCoordinator(
                catalogue,
                locomotion.CataloguePath,
                string.IsNullOrWhiteSpace(ContactEvidencePathOverride) ? null : ContactEvidencePathOverride,
                MaximumContactTranslation > 0f ? MaximumContactTranslation : null),
            leftNativeModifier,
            rightNativeModifier,
            leftFootBoneIndex,
            rightFootBoneIndex);
    }

    private void RollbackProvisionalConfiguration(
        Exception configurationError,
        ContactLockProductionBinding? provisionalBinding,
        PreparedConfiguration prepared,
        bool provisionalLeftSubscription,
        bool provisionalRightSubscription)
    {
        List<Exception> rollbackErrors = [];
        TryRollback(() =>
        {
            if (provisionalRightSubscription)
            {
                prepared.RightNativeModifier.ModificationProcessed -= OnRightNativeModificationProcessed;
            }
        }, rollbackErrors);
        TryRollback(() =>
        {
            if (provisionalLeftSubscription)
            {
                prepared.LeftNativeModifier.ModificationProcessed -= OnLeftNativeModificationProcessed;
            }
        }, rollbackErrors);
        TryRollback(() => provisionalBinding?.Teardown(), rollbackErrors);
        TryRollback(prepared.Coordinator.Teardown, rollbackErrors);
        if (rollbackErrors.Count > 0)
        {
            rollbackErrors.Insert(0, configurationError);
            throw new AggregateException(
                "Player contact-lock configuration failed and rollback also reported errors; authority was cleared fail-closed.",
                rollbackErrors);
        }

        ExceptionDispatchInfo.Capture(configurationError).Throw();
    }

    private static void TryRollback(Action rollback, List<Exception> rollbackErrors)
    {
        try
        {
            rollback();
        }
        catch (Exception rollbackError)
        {
            rollbackErrors.Add(rollbackError);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Enabled || delta <= 0d || _binding is null || _actor is null || _characterIK is null
            || _locomotion is null || _animationTree is null || _states is null || _catalogue is null)
        {
            return;
        }

        _evaluationTick = Engine.GetPhysicsFrames();
        AnimationNodeStateMachinePlayback? playback = _animationTree.Get(_playbackParameter).As<AnimationNodeStateMachinePlayback>();
        if (playback is null
            || !TryResolveEvaluatedCatalogueSource(
                playback,
                _states,
                _catalogue,
                out StringName evaluatedState,
                out StandingLocomotionClip? clip,
                out float phase,
                out float finiteClockPosition))
        {
            InvalidateLiveState(delta);
            return;
        }

        if (_lastEvaluatedState != evaluatedState)
        {
            _lastEvaluatedState = evaluatedState;
            _clockGeneration++;
        }

        ContactLockLiveState live = new(
            true,
            clip!.Role,
            clip.Identity,
            phase,
            _catalogue.PackageVariant,
            clip.Identity,
            _catalogue.ContactEvidence!.Generation,
            _evaluationTick,
            (float)delta)
        {
            ClockGeneration = _clockGeneration,
            ActorTransform = _actor.GlobalTransform,
            FiniteClockPositionSeconds = finiteClockPosition,
            FiniteClockDurationSeconds = (float)clip.DurationSeconds,
        };
        CatalogueEvaluationCount++;
        if (!clip.IsLooping)
        {
            FiniteEvaluationCount++;
        }
        _binding.Coordinator.ObserveLiveState(live);
        _locomotion.PublishContactLockEvaluation(_evaluationTick, _clockGeneration);
        if (!_characterIK.PrepareFootTargetsForMovement(_evaluationTick, delta))
        {
            ProviderPreparationFailureCount++;
        }
        UpdateTrace(_evaluationTick, complete: false);
        if (LastTrace.Left.ContactPublished || LastTrace.Right.ContactPublished)
        {
            ContactPublicationCount++;
        }
    }

    public override void _ExitTree()
    {
        _locomotion?.SetContactLockCoordinator(null);
        UnsubscribeNativeSignals();
        _binding?.Teardown();
        _binding = null;
    }

    public void RecordCorrectionConsumption(ContactLockMovementConsumptionTrace consumption)
    {
        if (_binding is not null)
        {
            _lastConsumption = consumption;
            PlayerContactCorrectionDisposition disposition = consumption.Applied
                ? PlayerContactCorrectionDisposition.Consumed
                : PlayerContactCorrectionDisposition.CollisionDivergence;
            ConsumedCorrectionCount += disposition == PlayerContactCorrectionDisposition.Consumed ? 1UL : 0UL;
            CollisionDivergenceCount += disposition == PlayerContactCorrectionDisposition.CollisionDivergence ? 1UL : 0UL;
            UpdateTrace(consumption.Contribution.PhysicsTick, complete: false);
        }
    }

    private void InvalidateLiveState(double delta)
    {
        if (!_lastEvaluatedState.IsEmpty)
        {
            _lastEvaluatedState = new StringName();
            _clockGeneration++;
        }

        _binding!.Coordinator.ObserveLiveState(new ContactLockLiveState(
            false,
            string.Empty,
            string.Empty,
            0f,
            string.Empty,
            string.Empty,
            string.Empty,
            _evaluationTick,
            (float)delta)
        {
            ClockGeneration = _clockGeneration,
            ActorTransform = _actor!.GlobalTransform,
        });
        _locomotion!.PublishContactLockEvaluation(_evaluationTick, _clockGeneration);
        UpdateTrace(_evaluationTick, complete: false);
    }

    private void UnsubscribeNativeSignals()
    {
        if (_leftNativeModifier is not null)
        {
            _leftNativeModifier.ModificationProcessed -= OnLeftNativeModificationProcessed;
        }
        if (_rightNativeModifier is not null)
        {
            _rightNativeModifier.ModificationProcessed -= OnRightNativeModificationProcessed;
        }

        _leftNativeModifier = null;
        _rightNativeModifier = null;
    }

    private void OnLeftNativeModificationProcessed() => ObserveNative(LimbSide.Left);
    private void OnRightNativeModificationProcessed() => ObserveNative(LimbSide.Right);

    private void ObserveNative(LimbSide side)
    {
        if (_skeleton is null || _characterIK is null)
        {
            return;
        }

        ulong tick = Engine.GetPhysicsFrames();
        Node3D target = side == LimbSide.Left ? _characterIK.LeftFootIKTarget! : _characterIK.RightFootIKTarget!;
        int boneIndex = side == LimbSide.Left ? _leftFootBoneIndex : _rightFootBoneIndex;
        IK003WorldTransform endFoot = ContactLockPerFootTraceBuilder.ToWorldTransform(
            _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex));
        IK003NativeSignalObservation observation = new(
            true,
            tick,
            ContactLockPerFootTraceBuilder.ToWorldTransform(target.GlobalTransform),
            endFoot)
        {
            // ModificationProcessed is emitted after this TwoBoneIK3D modifier has written its output.
            PostIKOutputObserved = true,
            PostIKOutput = endFoot,
        };
        if (side == LimbSide.Left)
        {
            _leftNative = observation;
        }
        else
        {
            _rightNative = observation;
        }

        UpdateTrace(tick, complete: _leftNative.PhysicsTick == tick && _rightNative.PhysicsTick == tick);
    }

    private void UpdateTrace(ulong tick, bool complete)
    {
        if (_binding is null)
        {
            return;
        }

        ContactLockMovementConsumptionTrace? consumption = _lastConsumption.Contribution.PhysicsTick == tick
            ? _lastConsumption
            : null;
        IK003PerFootResolverSample left = ContactLockPerFootTraceBuilder.Build(
            _binding.Coordinator,
            LimbSide.Left,
            tick,
            _leftNative,
            _characterIK?.GetFootActuationTrace(LimbSide.Left) ?? default,
            consumption);
        IK003PerFootResolverSample right = ContactLockPerFootTraceBuilder.Build(
            _binding.Coordinator,
            LimbSide.Right,
            tick,
            _rightNative,
            _characterIK?.GetFootActuationTrace(LimbSide.Right) ?? default,
            consumption);
        bool hasConsumption = _lastConsumption.Contribution.IsAuthorised
            && _lastConsumption.Contribution.PhysicsTick == tick;
        PlayerContactCorrectionDisposition disposition = hasConsumption
            ? _lastConsumption.Applied
                ? PlayerContactCorrectionDisposition.Consumed
                : PlayerContactCorrectionDisposition.CollisionDivergence
            : PlayerContactCorrectionDisposition.Unavailable;
        LastTrace = new PlayerContactLockTraceV1(
            tick,
            left,
            right,
            hasConsumption
                ? ContactLockPerFootTraceBuilder.BuildSelected(_lastConsumption)
                : ContactLockPerFootTraceBuilder.EmptySelected,
            disposition,
            hasConsumption ? _lastConsumption.MovementOperationIdentity : 0UL,
            hasConsumption ? _lastConsumption.Contribution.ConsumptionIdentity : 0L);
        if (complete && _lastCompleteTraceTick != tick)
        {
            _lastCompleteTraceTick = tick;
            CompleteTraceCount++;
            TraceRecorded?.Invoke(LastTrace);
        }
    }

    internal static bool TryResolveEvaluatedCatalogueSource(
        AnimationNodeStateMachinePlayback playback,
        AnimationNodeStateMachine states,
        StandingLocomotionCatalogue catalogue,
        out StringName evaluatedState,
        out StandingLocomotionClip? clip,
        out float phase,
        out float finiteClockPosition)
    {
        evaluatedState = playback.GetCurrentNode();
        float position = playback.GetCurrentPlayPosition();
        if (!TryResolveClip(states, catalogue, evaluatedState, out clip)
            && !playback.GetFadingFromNode().IsEmpty)
        {
            evaluatedState = playback.GetFadingFromNode();
            position = playback.GetFadingFromPlayPosition();
            _ = TryResolveClip(states, catalogue, evaluatedState, out clip);
        }

        if (clip is null || !float.IsFinite(position) || position < 0f)
        {
            phase = 0f;
            finiteClockPosition = 0f;
            return false;
        }

        float duration = (float)clip.DurationSeconds;
        finiteClockPosition = clip.IsLooping ? position % duration : Math.Min(position, duration);
        phase = clip.IsLooping
            ? finiteClockPosition / duration
            : Mathf.Clamp(finiteClockPosition / duration, 0f, 1f);
        return true;
    }

    internal static bool TryResolveClip(
        AnimationNodeStateMachine states,
        StandingLocomotionCatalogue catalogue,
        StringName state,
        out StandingLocomotionClip? clip)
    {
        clip = null;
        if (states.GetNode(state) is not AnimationNodeAnimation animationNode)
        {
            return false;
        }

        string animation = animationNode.Animation.ToString();
        string prefix = $"{catalogue.Package}/";
        if (!animation.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        string identity = animation[prefix.Length..];
        clip = catalogue.Clips.Values.SingleOrDefault(candidate => candidate.Identity == identity);
        return clip is not null;
    }

    private readonly record struct PreparedConfiguration(
        AnimationNodeStateMachine States,
        StandingLocomotionCatalogue Catalogue,
        ContactLockCoordinator Coordinator,
        TwoBoneIK3D LeftNativeModifier,
        TwoBoneIK3D RightNativeModifier,
        int LeftFootBoneIndex,
        int RightFootBoneIndex);
}

public enum PlayerContactCorrectionDisposition
{
    Unavailable, Available, Consumed, CollisionDivergence
}

public readonly record struct PlayerContactLockTraceV1(
    ulong PhysicsTick,
    IK003PerFootResolverSample Left,
    IK003PerFootResolverSample Right,
    IK003SelectedSupportContribution SelectedSupportContribution,
    PlayerContactCorrectionDisposition CorrectionDisposition,
    ulong MovementOperationIdentity,
    long ConsumptionIdentity);
