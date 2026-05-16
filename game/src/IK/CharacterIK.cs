using AlleyCat.Body;
using AlleyCat.Common;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Reusable provider-driven IK orchestration for humanoid characters.
/// </summary>
[GlobalClass]
public partial class CharacterIK : Node3D
{
    private Skeleton3D? _skeleton;

    private IKTargetBodyActuator? _headActuator;
    private IKTargetAnimatableActuator? _leftHandActuator;
    private IKTargetAnimatableActuator? _rightHandActuator;
    private IKTargetPipeline? _headTargetPipeline;
    private IKTargetPipeline? _leftHandTargetPipeline;
    private IKTargetPipeline? _rightHandTargetPipeline;

    private IKTargetActivityGate? _headTargetActivityGate;
    private IKTargetActivityGate? _rightHandTargetActivityGate;
    private IKTargetActivityGate? _leftHandTargetActivityGate;
    private IKTargetActivityGate? _rightFootTargetActivityGate;
    private IKTargetActivityGate? _leftFootTargetActivityGate;

    private Transform3D _viewpointLocalTransform = Transform3D.Identity;
    /// <summary>
    /// Avatar viewpoint marker representing eye-centre in avatar space.
    /// </summary>
    [ExportGroup("Targets")]
    [Export]
    public Marker3D? Viewpoint
    {
        get; set;
    }

    /// <summary>
    /// Head IK target body.
    /// </summary>
    [Export]
    public CharacterBody3D? HeadIKTarget
    {
        get; set;
    }

    /// <summary>
    /// Virtual head target consumed by downstream IK after optional stage limiting.
    /// </summary>
    [Export]
    public Node3D? HeadIKSolveTarget
    {
        get; set;
    }

    /// <summary>
    /// Right-hand IK target body.
    /// </summary>
    [Export]
    public AnimatableBody3D? RightHandIKTarget
    {
        get; set;
    }

    /// <summary>
    /// Left-hand IK target body.
    /// </summary>
    [Export]
    public AnimatableBody3D? LeftHandIKTarget
    {
        get; set;
    }

    /// <summary>
    /// Right-foot IK target node.
    /// </summary>
    [Export]
    public Node3D? RightFootIKTarget
    {
        get; set;
    }

    /// <summary>
    /// Left-foot IK target node.
    /// </summary>
    [Export]
    public Node3D? LeftFootIKTarget
    {
        get; set;
    }

    /// <summary>
    /// Physical rig that owns the skeleton driven by IK modifiers and generated body proxy collision integration.
    /// </summary>
    [ExportGroup("Rig Dependencies")]
    [Export]
    public DynamicPhysicalRig? PhysicalRig
    {
        get; set;
    }

    /// <summary>
    /// Provider driving the head target and head/neck modifier influence.
    /// </summary>
    [ExportGroup("Providers")]
    [Export]
    public IKTargetIntentProvider? HeadTargetIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Provider driving the right-hand target and right arm modifier influence.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? RightHandIKTargetIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Optional contributors applied to the right-hand source intent before physical actuation.
    /// </summary>
    public IIKTargetContributor[] RightHandIKTargetContributors { get; set; } = [];

    /// <summary>
    /// Optional contributors applied to the left-hand source intent before physical actuation.
    /// </summary>
    public IIKTargetContributor[] LeftHandIKTargetContributors { get; set; } = [];

    /// <summary>
    /// Provider driving the left-hand target and left arm modifier influence.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? LeftHandIKTargetIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Provider driving the right-foot target and right leg modifier influence.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? RightFootTargetIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Provider driving the left-foot target and left leg modifier influence.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? LeftFootTargetIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Optional fallback provider for the head when no explicit head provider is assigned.
    /// </summary>
    [ExportGroup("Fallbacks")]
    [Export]
    public IKTargetIntentProvider? HeadFallbackIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Optional fallback provider for the right hand when no explicit right-hand provider is assigned.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? RightHandFallbackIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Optional fallback provider for the left hand when no explicit left-hand provider is assigned.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? LeftHandFallbackIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Optional fallback provider for the right foot when no explicit right-foot provider is assigned.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? RightFootFallbackIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Optional fallback provider for the left foot when no explicit left-foot provider is assigned.
    /// </summary>
    [Export]
    public IKTargetIntentProvider? LeftFootFallbackIntentProvider
    {
        get; set;
    }

    /// <summary>
    /// Modifiers controlled by the effective head provider influence.
    /// </summary>
    [ExportGroup("Modifiers")]
    [Export]
    public SkeletonModifier3D[] HeadModifierGroup { get; set; } = [];

    /// <summary>
    /// Modifiers controlled by the effective right-hand provider influence.
    /// </summary>
    [Export]
    public SkeletonModifier3D[] RightHandModifierGroup { get; set; } = [];

    /// <summary>
    /// Modifiers controlled by the effective left-hand provider influence.
    /// </summary>
    [Export]
    public SkeletonModifier3D[] LeftHandModifierGroup { get; set; } = [];

    /// <summary>
    /// Modifiers controlled by the effective right-foot provider influence.
    /// </summary>
    [Export]
    public SkeletonModifier3D[] RightFootModifierGroup { get; set; } = [];

    /// <summary>
    /// Modifiers controlled by the effective left-foot provider influence.
    /// </summary>
    [Export]
    public SkeletonModifier3D[] LeftFootModifierGroup { get; set; } = [];

    /// <summary>
    /// Maximum follow speed for the head target body.
    /// </summary>
    [ExportGroup("Head Settings")]
    [Export]
    public float HeadTargetMaximumSpeed { get; set; } = 32.0f;

    /// <summary>
    /// Maximum follow speed for each hand target body.
    /// </summary>
    [ExportGroup("Hand Settings")]
    [Export]
    public float HandTargetMaximumSpeed { get; set; } = 28.0f;

    /// <summary>
    /// Position-error gain for hand actuator translation while using body-based hand targets.
    /// </summary>
    [Export]
    public float HandTargetPositionResponsiveness { get; set; } = 14.0f;

    /// <summary>
    /// Maximum hand actuator acceleration while using body-based hand targets.
    /// </summary>
    [Export]
    public float HandTargetMaximumAcceleration { get; set; } = 48.0f;

    /// <summary>
    /// Distance within which hand actuators progressively ease into their final settle movement.
    /// </summary>
    [Export]
    public float HandTargetSettleDistance { get; set; } = 0.03f;

    /// <summary>
    /// Rotation-error gain for hand actuator orientation smoothing.
    /// </summary>
    [Export]
    public float HandTargetRotationResponsiveness { get; set; } = 24.0f;

    /// <summary>
    /// Collision mask queried for explicit hand-only interaction with dynamic rigid bodies.
    /// </summary>
    [ExportGroup("Hand Collision")]
    [Export]
    public uint HandDynamicInteractionCollisionMask { get; set; } = 2;

    /// <summary>
    /// Minimum approach speed before a hand impact impulse may fire.
    /// </summary>
    [Export]
    public float HandDynamicImpactApproachSpeedThreshold
    {
        get; set;
    } =
        HandDynamicBodyInteractionController.DefaultImpactApproachSpeedThreshold;

    /// <summary>
    /// Impact impulse gain applied per metre-per-second of hand approach speed.
    /// </summary>
    [Export]
    public float HandDynamicImpactImpulsePerSpeed
    {
        get; set;
    } =
        HandDynamicBodyInteractionController.DefaultImpactImpulsePerSpeed;

    /// <summary>
    /// Maximum impact impulse magnitude applied by the hand interaction channel.
    /// </summary>
    [Export]
    public float HandDynamicImpactImpulseCap
    {
        get; set;
    } =
        HandDynamicBodyInteractionController.DefaultImpactImpulseCap;

    /// <summary>
    /// Minimum pressing speed before the sustained hand push channel may fire.
    /// </summary>
    [Export]
    public float HandDynamicSustainedPushSpeedThreshold
    {
        get; set;
    } =
        HandDynamicBodyInteractionController.DefaultSustainedPushSpeedThreshold;

    /// <summary>
    /// Sustained push-force gain applied per metre-per-second of hand pressing speed.
    /// </summary>
    [Export]
    public float HandDynamicSustainedForcePerSpeed
    {
        get; set;
    } =
        HandDynamicBodyInteractionController.DefaultSustainedForcePerSpeed;

    /// <summary>
    /// Maximum sustained push-force magnitude applied by the hand interaction channel.
    /// </summary>
    [Export]
    public float HandDynamicSustainedForceCap
    {
        get; set;
    } =
        HandDynamicBodyInteractionController.DefaultSustainedForceCap;

    /// <summary>
    /// When true, enables IK processing. When false, skips provider target updates.
    /// </summary>
    [ExportGroup("General")]
    [Export]
    public bool Active { get; set; } = true;

    /// <summary>
    /// Resolved head-bone index for compensation calculations.
    /// </summary>
    public int HeadBoneIndex { get; private set; } = -1;

    /// <summary>
    /// Number of physics-timed actuator update ticks executed since startup.
    /// </summary>
    public ulong PhysicsActuatorTickCount
    {
        get; private set;
    }

    /// <summary>
    /// Last debug snapshot for the right-hand target pipeline.
    /// </summary>
    public IKTargetPipelineResult RightHandTargetPipelineDebugState
    {
        get; private set;
    } = new(
        Transform3D.Identity,
        Transform3D.Identity,
        Transform3D.Identity,
        IKTargetPipelineFeedback.FromTargets(Transform3D.Identity, Transform3D.Identity, "Inactive"));

    /// <summary>
    /// Last debug snapshot for the left-hand target pipeline.
    /// </summary>
    public IKTargetPipelineResult LeftHandTargetPipelineDebugState
    {
        get; private set;
    } = new(
        Transform3D.Identity,
        Transform3D.Identity,
        Transform3D.Identity,
        IKTargetPipelineFeedback.FromTargets(Transform3D.Identity, Transform3D.Identity, "Inactive"));

    /// <summary>
    /// Last debug snapshot for the head target pipeline.
    /// </summary>
    public IKTargetPipelineResult HeadTargetPipelineDebugState
    {
        get; private set;
    } = new(
        Transform3D.Identity,
        Transform3D.Identity,
        Transform3D.Identity,
        IKTargetPipelineFeedback.FromTargets(Transform3D.Identity, Transform3D.Identity, "Inactive"));

    /// <summary>
    /// Viewpoint marker resolved from exports or scene structure.
    /// </summary>
    protected Marker3D? ResolvedViewpoint
    {
        get; private set;
    }

    /// <summary>
    /// Head IK target resolved from exports or scene structure.
    /// </summary>
    protected CharacterBody3D? ResolvedHeadIKTarget
    {
        get; private set;
    }
    /// <summary>
    /// Head solve target resolved from exports or scene structure.
    /// </summary>
    protected Node3D? ResolvedHeadIKSolveTarget
    {
        get; private set;
    }
    /// <summary>
    /// Right-hand IK target resolved from exports or scene structure.
    /// </summary>
    protected AnimatableBody3D? ResolvedRightHandIKTarget
    {
        get; private set;
    }
    /// <summary>
    /// Left-hand IK target resolved from exports or scene structure.
    /// </summary>
    protected AnimatableBody3D? ResolvedLeftHandIKTarget
    {
        get; private set;
    }
    /// <summary>
    /// Right-foot IK target resolved from exports or scene structure.
    /// </summary>
    protected Node3D? ResolvedRightFootIKTarget
    {
        get; private set;
    }
    /// <summary>
    /// Left-foot IK target resolved from exports or scene structure.
    /// </summary>
    protected Node3D? ResolvedLeftFootIKTarget
    {
        get; private set;
    }
    /// <summary>
    /// Viewpoint transform relative to the head attachment.
    /// </summary>
    protected Transform3D ViewpointLocalTransform => _viewpointLocalTransform;
    /// <summary>
    /// Inverse viewpoint transform relative to the head attachment.
    /// </summary>
    protected Transform3D ViewpointLocalInverseTransform { get; private set; } = Transform3D.Identity;

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        EnsureResolvedNodes();
        EnsureTargetActivityGates();
        EnsureActuators();
        SetPhysicsProcess(true);
        InsertStageModifiers();
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        if (delta <= 0d)
        {
            return;
        }

        UpdatePhysicalActuators(delta);
    }

    /// <summary>
    /// Determines whether provider-driven stage target orchestration should run.
    /// </summary>
    protected virtual bool CanProcessProviderTargets => true;

    /// <summary>
    /// Determines whether physics-timed hand target actuators should run.
    /// </summary>
    protected virtual bool CanProcessPhysicalActuators => CanProcessProviderTargets;

    /// <summary>
    /// Runs before provider target pipelines are updated.
    /// </summary>
    protected virtual void BeforeProviderTargetProcessing()
    {
    }

    /// <summary>
    /// Runs after provider target pipelines are updated in the begin stage.
    /// </summary>
    protected virtual void AfterProviderTargetProcessing(Skeleton3D skeleton, double delta)
    {
    }

    /// <summary>
    /// Runs at the end of skeleton modifier stage processing.
    /// </summary>
    protected virtual void AfterEndStage(Skeleton3D skeleton, double delta)
    {
    }

    /// <summary>
    /// Runs immediately before physics-timed hand target actuators move their bodies.
    /// </summary>
    protected virtual void BeforeHandTargetActuators()
    {
    }

    /// <summary>
    /// Returns the head target used when no explicit or fallback head provider is configured.
    /// </summary>
    protected virtual Transform3D GetDefaultHeadTargetTransform()
        => ResolvedHeadIKTarget?.GlobalTransform ?? Transform3D.Identity;

    /// <summary>
    /// Allows subclasses to resolve additional nodes after the shared skeleton is available.
    /// </summary>
    protected virtual void EnsureSubclassResolvedNodes(Skeleton3D skeleton)
    {
    }

    /// <summary>
    /// Returns the resolved skeleton or fails when resolution has not completed.
    /// </summary>
    protected Skeleton3D GetResolvedSkeleton()
        => _skeleton ?? throw new InvalidOperationException($"{GetType().Name} skeleton not resolved before use.");

    /// <summary>
    /// Resolves exported or convention-based target, modifier, and physical-rig-owned skeleton nodes.
    /// </summary>
    protected void EnsureResolvedNodes()
    {
        if (_skeleton is not null)
        {
            return;
        }

        ResolvedViewpoint = Viewpoint ?? this.RequireNode<Marker3D>("Female_export/GeneralSkeleton/Head/Viewpoint");
        ResolvedHeadIKTarget = HeadIKTarget ?? this.RequireNode<CharacterBody3D>("IKTargets/Head");
        ResolvedHeadIKSolveTarget = HeadIKSolveTarget ?? this.RequireNode<Node3D>("IKTargets/HeadSolve");
        ResolvedRightHandIKTarget = RightHandIKTarget ?? this.RequireNode<AnimatableBody3D>("IKTargets/RightHand");
        ResolvedLeftHandIKTarget = LeftHandIKTarget ?? this.RequireNode<AnimatableBody3D>("IKTargets/LeftHand");
        ResolvedRightFootIKTarget = RightFootIKTarget ?? GetNodeOrNull<Node3D>("IKTargets/RightFoot");
        ResolvedLeftFootIKTarget = LeftFootIKTarget ?? GetNodeOrNull<Node3D>("IKTargets/LeftFoot");
        _skeleton = ResolveDrivenSkeleton();
        EnsureDefaultModifierGroups(_skeleton);
        EnsureSubclassResolvedNodes(_skeleton);

        HeadBoneIndex = _skeleton.FindBone("Head");
        if (HeadBoneIndex < 0)
        {
            throw new InvalidOperationException($"Unable to resolve Head bone on skeleton '{_skeleton.Name}'.");
        }

        _viewpointLocalTransform = ResolvedViewpoint.Transform;
        ViewpointLocalInverseTransform = _viewpointLocalTransform.Inverse();
    }

    private Skeleton3D ResolveDrivenSkeleton()
    {
        DynamicPhysicalRig physicalRig = PhysicalRig
                                         ?? this.RequireNode<DynamicPhysicalRig>(
                                             "Female_export/GeneralSkeleton/DynamicPhysicalRig");

        return physicalRig.TargetSkeleton
            ?? (physicalRig.GetParent() is Skeleton3D parentSkeleton
            ? parentSkeleton
            : throw new InvalidOperationException(
                $"{GetType().Name} '{Name}' requires {nameof(PhysicalRig)} '{physicalRig.Name}' to have either an explicit {nameof(DynamicPhysicalRig.TargetSkeleton)} or a parent {nameof(Skeleton3D)}."));
    }

    /// <summary>
    /// Applies runtime binding to all configured fallback providers.
    /// </summary>
    protected void ConfigureFallbackProviders(Action<IKTargetIntentProvider> configureProvider)
    {
        ConfigureFallbackProvider(HeadFallbackIntentProvider, configureProvider);
        ConfigureFallbackProvider(RightHandFallbackIntentProvider, configureProvider);
        ConfigureFallbackProvider(LeftHandFallbackIntentProvider, configureProvider);
        ConfigureFallbackProvider(RightFootFallbackIntentProvider, configureProvider);
        ConfigureFallbackProvider(LeftFootFallbackIntentProvider, configureProvider);
    }

    private static void ConfigureFallbackProvider(
        IKTargetIntentProvider? fallbackProvider,
        Action<IKTargetIntentProvider> configureProvider)
    {
        if (fallbackProvider is not null && IsInstanceValid(fallbackProvider))
        {
            configureProvider(fallbackProvider);
        }
    }

    /// <summary>
    /// Applies the head solve target transform for the current skeleton modifier stage.
    /// </summary>
    protected void ApplyHeadSolveTargetTransform(Transform3D? limitedHeadTargetTransform)
    {
        if (ResolvedHeadIKSolveTarget is null || ResolvedHeadIKTarget is null)
        {
            return;
        }

        SetWorldTransform(ResolvedHeadIKSolveTarget, limitedHeadTargetTransform ?? GetWorldTransform(ResolvedHeadIKTarget));
    }

    /// <summary>
    /// Builds the current head target transform and applies head modifier influence.
    /// </summary>
    protected Transform3D BuildHeadTargetTransform()
        => BuildHeadTargetFollowState().WorldTransform;

    private IKTargetFollowState BuildHeadTargetFollowState()
    {
        if (HeadTargetIntentProvider is not null || HeadFallbackIntentProvider is not null)
        {
            IKTargetIntent intent = ResolveTargetIntent(
                HeadTargetIntentProvider,
                HeadFallbackIntentProvider,
                ResolvedHeadIKTarget?.GlobalTransform ?? Transform3D.Identity);
            float influence = ApplyModifierInfluence(intent, null, HeadModifierGroup);
            bool active = influence > 0.0f;
            _headTargetActivityGate?.Apply(active);
            Transform3D currentTargetTransform = ResolvedHeadIKTarget?.GlobalTransform ?? Transform3D.Identity;
            return new IKTargetFollowState(active ? intent.WorldTransform : currentTargetTransform, active);
        }

        _ = ApplyModifierInfluence(new IKTargetIntent(GetDefaultHeadTargetTransform(), 0.0f), null, HeadModifierGroup);
        _headTargetActivityGate?.Apply(active: false);
        return new IKTargetFollowState(GetDefaultHeadTargetTransform(), active: false);
    }

    /// <summary>
    /// Builds the current right-hand target transform and applies right-hand modifier influence.
    /// </summary>
    protected Transform3D BuildRightHandTargetTransform()
    {
        return BuildHandTargetTransform(
            RightHandIKTargetIntentProvider,
            RightHandFallbackIntentProvider,
            ResolvedRightHandIKTarget,
            RightHandModifierGroup);
    }

    /// <summary>
    /// Builds the current left-hand target transform and applies left-hand modifier influence.
    /// </summary>
    protected Transform3D BuildLeftHandTargetTransform()
    {
        return BuildHandTargetTransform(
            LeftHandIKTargetIntentProvider,
            LeftHandFallbackIntentProvider,
            ResolvedLeftHandIKTarget,
            LeftHandModifierGroup);
    }

    private Transform3D BuildHandTargetTransform(
        IKTargetIntentProvider? provider,
        IKTargetIntentProvider? fallbackProvider,
        AnimatableBody3D? target,
        SkeletonModifier3D[] modifierGroup)
        => BuildHandTargetFollowState(provider, fallbackProvider, target, modifierGroup).WorldTransform;

    private IKTargetFollowState BuildRightHandTargetFollowState()
        => BuildHandTargetFollowState(
            RightHandIKTargetIntentProvider,
            RightHandFallbackIntentProvider,
            ResolvedRightHandIKTarget,
            RightHandModifierGroup);

    private IKTargetFollowState BuildLeftHandTargetFollowState()
    {
        return BuildHandTargetFollowState(
            LeftHandIKTargetIntentProvider,
            LeftHandFallbackIntentProvider,
            ResolvedLeftHandIKTarget,
            LeftHandModifierGroup);
    }

    private IKTargetFollowState BuildHandTargetFollowState(
        IKTargetIntentProvider? provider,
        IKTargetIntentProvider? fallbackProvider,
        AnimatableBody3D? target,
        SkeletonModifier3D[] modifierGroup)
    {
        Transform3D currentTargetTransform = target?.GlobalTransform ?? Transform3D.Identity;
        IKTargetActivityGate? targetActivityGate = ResolveHandTargetActivityGate(target);
        if (provider is null && fallbackProvider is null)
        {
            _ = ApplyModifierInfluence(new IKTargetIntent(currentTargetTransform, 0.0f), modifierGroup);
            targetActivityGate?.Apply(active: false);
            return new IKTargetFollowState(currentTargetTransform, active: false);
        }

        if (provider is not null && IsInstanceValid(provider))
        {
            IKTargetIntent providerIntent = provider.GetTargetIntent();
            float influence = ApplyModifierInfluence(providerIntent, modifierGroup);
            bool active = influence > 0.0f;
            targetActivityGate?.Apply(active);
            return new IKTargetFollowState(active ? providerIntent.WorldTransform : currentTargetTransform, active);
        }

        if (fallbackProvider is not null && IsInstanceValid(fallbackProvider))
        {
            IKTargetIntent fallbackIntent = fallbackProvider.GetTargetIntent();
            float influence = ApplyModifierInfluence(fallbackIntent, modifierGroup);
            bool active = influence > 0.0f;
            targetActivityGate?.Apply(active);
            return new IKTargetFollowState(active ? fallbackIntent.WorldTransform : currentTargetTransform, active);
        }

        targetActivityGate?.Apply(active: false);
        return new IKTargetFollowState(currentTargetTransform, active: false);
    }

    private IKTargetActivityGate? ResolveHandTargetActivityGate(AnimatableBody3D? target)
    {
        return target is not null && ReferenceEquals(target, ResolvedRightHandIKTarget)
            ? _rightHandTargetActivityGate
            : target is not null && ReferenceEquals(target, ResolvedLeftHandIKTarget)
            ? _leftHandTargetActivityGate
            : null;
    }

    private void OnBeginStage(double delta)
    {
        ApplyHeadSolveTargetTransform(limitedHeadTargetTransform: null);

        if (!Active || !CanProcessProviderTargets)
        {
            return;
        }

        BeforeProviderTargetProcessing();
        HeadTargetPipelineDebugState = _headTargetPipeline?.Run(delta) ?? HeadTargetPipelineDebugState;
        ApplyHeadSolveTargetTransform(limitedHeadTargetTransform: null);
        AfterProviderTargetProcessing(GetResolvedSkeleton(), delta);
    }

    private void OnFootProviderStage(double delta)
    {
        _ = delta;
        if (!Active || !CanProcessProviderTargets)
        {
            return;
        }

        ApplyFootTargetProviders();
    }

    private void OnEndStage(double delta)
    {
        if (!Active || !CanProcessProviderTargets)
        {
            return;
        }

        AfterEndStage(GetResolvedSkeleton(), delta);
    }

    private void UpdatePhysicalActuators(double delta)
    {
        if (!Active || !CanProcessPhysicalActuators)
        {
            return;
        }

        BeforeProviderTargetProcessing();
        BeforeHandTargetActuators();
        PhysicsActuatorTickCount += 1;
        RightHandTargetPipelineDebugState = _rightHandTargetPipeline?.Run(delta) ?? RightHandTargetPipelineDebugState;
        LeftHandTargetPipelineDebugState = _leftHandTargetPipeline?.Run(delta) ?? LeftHandTargetPipelineDebugState;
    }

    private void InsertStageModifiers()
    {
        Skeleton3D skeleton = GetResolvedSkeleton();

        StageModifier beginModifier = new()
        {
            Name = "CharacterIKBeginStage",
            Callback = OnBeginStage,
        };

        FootProviderStageModifier footProviderModifier = new()
        {
            Name = "CharacterIKFootProviderStage",
            CharacterIK = this,
        };

        StageModifier endModifier = new()
        {
            Name = "CharacterIKEndStage",
            Callback = OnEndStage,
        };

        skeleton.AddChild(beginModifier);
        skeleton.MoveChild(beginModifier, ResolveBeginStageIndex(skeleton));

        skeleton.AddChild(footProviderModifier);
        skeleton.MoveChild(footProviderModifier, beginModifier.GetIndex() + 1);

        skeleton.AddChild(endModifier);
        skeleton.MoveChild(endModifier, skeleton.GetChildCount() - 1);
    }

    private static int ResolveBeginStageIndex(Skeleton3D skeleton)
    {
        Node? footSyncController = skeleton.GetNodeOrNull("FootTargetSyncController");
        return footSyncController is not null && IsInstanceValid(footSyncController)
            ? footSyncController.GetIndex() + 1
            : 0;
    }

    private void ApplyFootTargetProviders()
    {
        ApplyFootTargetProvider(
            RightFootTargetIntentProvider,
            RightFootFallbackIntentProvider,
            SelectValidTarget(ResolvedRightFootIKTarget, RightFootIKTarget),
            RightFootModifierGroup,
            _rightFootTargetActivityGate);
        ApplyFootTargetProvider(
            LeftFootTargetIntentProvider,
            LeftFootFallbackIntentProvider,
            SelectValidTarget(ResolvedLeftFootIKTarget, LeftFootIKTarget),
            LeftFootModifierGroup,
            _leftFootTargetActivityGate);
    }

    private static Node3D? SelectValidTarget(Node3D? resolvedTarget, Node3D? exportedTarget)
        => resolvedTarget is not null && IsInstanceValid(resolvedTarget) ? resolvedTarget : exportedTarget;

    private static void ApplyFootTargetProvider(
        IKTargetIntentProvider? provider,
        IKTargetIntentProvider? fallbackProvider,
        Node3D? target,
        SkeletonModifier3D[] modifierGroup,
        IKTargetActivityGate? targetActivityGate)
    {
        if (provider is null && fallbackProvider is null)
        {
            _ = ApplyFootModifierInfluence(
                new IKTargetIntent(target?.GlobalTransform ?? Transform3D.Identity, 0.0f),
                modifierGroup);
            targetActivityGate?.Apply(active: false);
            return;
        }

        IKTargetIntentProvider? effectiveProvider = SelectEffectiveProvider(provider, fallbackProvider);
        IKTargetIntent intent = effectiveProvider?.GetTargetIntent()
                              ?? new IKTargetIntent(target?.GlobalTransform ?? Transform3D.Identity, 0.0f);
        float influence = ApplyFootModifierInfluence(intent, modifierGroup);
        targetActivityGate?.Apply(influence > 0.0f);
        if (target is not null && influence > 0.0f && effectiveProvider?.ShouldApplyTargetTransform == true)
        {
            SetWorldTransform(target, intent.WorldTransform);
        }
    }

    private static IKTargetIntentProvider? SelectEffectiveProvider(
        IKTargetIntentProvider? provider,
        IKTargetIntentProvider? fallbackProvider)
        => provider is not null && IsInstanceValid(provider)
            ? provider
            : fallbackProvider is not null && IsInstanceValid(fallbackProvider)
            ? fallbackProvider
            : null;

    private static void SetWorldTransform(Node3D node, Transform3D worldTransform)
    {
        Transform3D orthonormalWorldTransform = new(worldTransform.Basis.Orthonormalized(), worldTransform.Origin);
        node.GlobalTransform = orthonormalWorldTransform;
        node.Transform = node.GetParent() is Node3D parent
            ? parent.GlobalTransform.AffineInverse() * orthonormalWorldTransform
            : orthonormalWorldTransform;
        if (node.IsInsideTree())
        {
            node.ForceUpdateTransform();
        }
    }

    private static Transform3D GetWorldTransform(Node3D node)
        => node.GetParent() is Node3D parent ? parent.GlobalTransform * node.Transform : node.GlobalTransform;

    private static float ApplyFootModifierInfluence(IKTargetIntent intent, SkeletonModifier3D[] modifierGroup)
    {
        float influence = Mathf.Clamp(intent.DesiredInfluence, 0.0f, 1.0f);
        bool active = influence > 0.0f;

        foreach (SkeletonModifier3D modifier in modifierGroup)
        {
            if (modifier is FootTargetSyncController)
            {
                continue;
            }

            ApplyModifierInfluence(modifier, influence, active);
        }

        return influence;
    }

    private void EnsureDefaultModifierGroups(Skeleton3D skeleton)
    {
        if (HeadModifierGroup.Length == 0)
        {
            HeadModifierGroup = ResolveSkeletonModifierGroup(
                skeleton,
                "NeckSpineIK",
                "HeadCopyRotation",
                "NeckTwistDisperser");
        }

        if (RightHandModifierGroup.Length == 0)
        {
            RightHandModifierGroup = ResolveSkeletonModifierGroup(
                skeleton,
                "RightArmIKController",
                "RightArmTwoBoneIKController",
                "RightHandCopyRotation");
        }

        if (LeftHandModifierGroup.Length == 0)
        {
            LeftHandModifierGroup = ResolveSkeletonModifierGroup(
                skeleton,
                "LeftArmIKController",
                "LeftArmTwoBoneIKController",
                "LeftHandCopyRotation");
        }

        if (RightFootModifierGroup.Length == 0)
        {
            RightFootModifierGroup = ResolveSkeletonModifierGroup(
                skeleton,
                "RightLegIKController",
                "RightLegTwoBoneIKController",
                "CopyRightFootRotation");
        }

        if (LeftFootModifierGroup.Length == 0)
        {
            LeftFootModifierGroup = ResolveSkeletonModifierGroup(
                skeleton,
                "LeftLegIKController",
                "LeftLegTwoBoneIKController",
                "CopyLeftFootRotation");
        }
    }

    private static SkeletonModifier3D[] ResolveSkeletonModifierGroup(Skeleton3D skeleton, params string[] childNames)
    {
        List<SkeletonModifier3D> modifiers = [];
        foreach (string childName in childNames)
        {
            if (skeleton.GetNodeOrNull<SkeletonModifier3D>(childName) is SkeletonModifier3D modifier)
            {
                modifiers.Add(modifier);
            }
        }

        return [.. modifiers];
    }

    private void EnsureTargetActivityGates()
    {
        _headTargetActivityGate ??= new IKTargetActivityGate(ResolvedHeadIKTarget, ResolvedHeadIKSolveTarget);
        _rightHandTargetActivityGate ??= new IKTargetActivityGate(ResolvedRightHandIKTarget);
        _leftHandTargetActivityGate ??= new IKTargetActivityGate(ResolvedLeftHandIKTarget);
        _rightFootTargetActivityGate ??= new IKTargetActivityGate(ResolvedRightFootIKTarget);
        _leftFootTargetActivityGate ??= new IKTargetActivityGate(ResolvedLeftFootIKTarget);
    }

    private sealed class IKTargetActivityGate(Node3D? primary, Node3D? secondary = null)
    {
        private readonly TargetNodeState? _primaryState = primary is not null && IsInstanceValid(primary)
            ? new TargetNodeState(primary)
            : null;
        private readonly TargetNodeState? _secondaryState = secondary is not null && IsInstanceValid(secondary)
            ? new TargetNodeState(secondary)
            : null;
        private bool _active = true;

        public void Apply(bool active)
        {
            if (_active == active)
            {
                return;
            }

            _active = active;
            _primaryState?.Apply(active);
            _secondaryState?.Apply(active);
        }
    }

    private sealed class TargetNodeState(Node3D node)
    {
        private readonly Node3D _node = node;
        private readonly ProcessModeEnum _processMode = node.ProcessMode;
        private readonly uint? _collisionLayer = node is CollisionObject3D collisionObject ? collisionObject.CollisionLayer : null;
        private readonly uint? _collisionMask = node is CollisionObject3D collisionObject ? collisionObject.CollisionMask : null;
        private readonly List<CollisionShapeState> _collisionShapeStates = BuildCollisionShapeStates(node);

        public void Apply(bool active)
        {
            if (!IsInstanceValid(_node))
            {
                return;
            }

            _node.ProcessMode = active ? _processMode : ProcessModeEnum.Disabled;
            if (_node is CollisionObject3D collisionObject && _collisionLayer.HasValue && _collisionMask.HasValue)
            {
                collisionObject.CollisionLayer = active ? _collisionLayer.Value : 0u;
                collisionObject.CollisionMask = active ? _collisionMask.Value : 0u;
            }

            foreach (CollisionShapeState collisionShapeState in _collisionShapeStates)
            {
                collisionShapeState.Apply(active);
            }
        }

        private static List<CollisionShapeState> BuildCollisionShapeStates(Node node)
        {
            List<CollisionShapeState> states = [];
            AddCollisionShapeStates(node, states);
            return states;
        }

        private static void AddCollisionShapeStates(Node node, List<CollisionShapeState> states)
        {
            int childCount = node.GetChildCount();
            for (int i = 0; i < childCount; i++)
            {
                Node child = node.GetChild(i);
                if (child is CollisionShape3D collisionShape)
                {
                    states.Add(new CollisionShapeState(collisionShape));
                }

                AddCollisionShapeStates(child, states);
            }
        }
    }

    private sealed class CollisionShapeState(CollisionShape3D collisionShape)
    {
        private readonly CollisionShape3D _collisionShape = collisionShape;
        private readonly bool _disabled = collisionShape.Disabled;

        public void Apply(bool active)
        {
            if (!IsInstanceValid(_collisionShape))
            {
                return;
            }

            _collisionShape.SetDeferred(CollisionShape3D.PropertyName.Disabled, !active || _disabled);
        }
    }

    private void EnsureActuators()
    {
        if (_headActuator is not null && _rightHandActuator is not null && _leftHandActuator is not null)
        {
            return;
        }

        CharacterBody3D headTarget = ResolvedHeadIKTarget
                                     ?? throw new InvalidOperationException(
                                          $"{GetType().Name} head target not resolved before actuator setup.");
        AnimatableBody3D rightHandTarget = ResolvedRightHandIKTarget
                                           ?? throw new InvalidOperationException(
                                                $"{GetType().Name} right-hand target not resolved before actuator setup.");
        AnimatableBody3D leftHandTarget = ResolvedLeftHandIKTarget
                                          ?? throw new InvalidOperationException(
                                               $"{GetType().Name} left-hand target not resolved before actuator setup.");

        _headActuator = new IKTargetBodyActuator(headTarget)
        {
            MaximumSpeed = HeadTargetMaximumSpeed,
            SnapDistance = float.MaxValue,
        };

        _headTargetPipeline = new IKTargetPipeline(BuildHeadTargetFollowState, [], _headActuator);

        _rightHandActuator = new IKTargetAnimatableActuator(
            rightHandTarget,
            ResolveRightHandDynamicInteractionShapes())
        {
            MaximumSpeed = HandTargetMaximumSpeed,
            PositionResponsiveness = HandTargetPositionResponsiveness,
            MaximumAcceleration = HandTargetMaximumAcceleration,
            SnapDistance = HandTargetSettleDistance,
            RotationResponsiveness = HandTargetRotationResponsiveness,
            DynamicBodyInteractionCollisionMask = HandDynamicInteractionCollisionMask,
            DynamicImpactApproachSpeedThreshold = HandDynamicImpactApproachSpeedThreshold,
            DynamicImpactImpulsePerSpeed = HandDynamicImpactImpulsePerSpeed,
            DynamicImpactImpulseCap = HandDynamicImpactImpulseCap,
            DynamicSustainedPushSpeedThreshold = HandDynamicSustainedPushSpeedThreshold,
            DynamicSustainedForcePerSpeed = HandDynamicSustainedForcePerSpeed,
            DynamicSustainedForceCap = HandDynamicSustainedForceCap,
        };

        _rightHandTargetPipeline = new IKTargetPipeline(
            BuildRightHandTargetFollowState,
            () => RightHandIKTargetContributors,
            _rightHandActuator);

        _leftHandActuator = new IKTargetAnimatableActuator(
            leftHandTarget,
            ResolveLeftHandDynamicInteractionShapes())
        {
            MaximumSpeed = HandTargetMaximumSpeed,
            PositionResponsiveness = HandTargetPositionResponsiveness,
            MaximumAcceleration = HandTargetMaximumAcceleration,
            SnapDistance = HandTargetSettleDistance,
            RotationResponsiveness = HandTargetRotationResponsiveness,
            DynamicBodyInteractionCollisionMask = HandDynamicInteractionCollisionMask,
            DynamicImpactApproachSpeedThreshold = HandDynamicImpactApproachSpeedThreshold,
            DynamicImpactImpulsePerSpeed = HandDynamicImpactImpulsePerSpeed,
            DynamicImpactImpulseCap = HandDynamicImpactImpulseCap,
            DynamicSustainedPushSpeedThreshold = HandDynamicSustainedPushSpeedThreshold,
            DynamicSustainedForcePerSpeed = HandDynamicSustainedForcePerSpeed,
            DynamicSustainedForceCap = HandDynamicSustainedForceCap,
        };

        _leftHandTargetPipeline = new IKTargetPipeline(
            BuildLeftHandTargetFollowState,
            () => LeftHandIKTargetContributors,
            _leftHandActuator);
    }

    /// <summary>
    /// Returns profile-backed query shapes for right-hand dynamic body interaction.
    /// </summary>
    protected virtual IReadOnlyList<HandDynamicInteractionShape> ResolveRightHandDynamicInteractionShapes()
        => [];

    /// <summary>
    /// Returns profile-backed query shapes for left-hand dynamic body interaction.
    /// </summary>
    protected virtual IReadOnlyList<HandDynamicInteractionShape> ResolveLeftHandDynamicInteractionShapes()
        => [];

    private sealed partial class StageModifier : SkeletonModifier3D
    {
        public Action<double>? Callback
        {
            get; set;
        }

        public override void _ProcessModificationWithDelta(double delta)
            => Callback?.Invoke(delta);
    }

    private sealed partial class FootProviderStageModifier : SkeletonModifier3D
    {
        public CharacterIK? CharacterIK
        {
            get;
            set;
        }

        public override void _ProcessModificationWithDelta(double delta)
            => CharacterIK?.OnFootProviderStage(delta);
    }

    /// <summary>
    /// Resolves a target intent using explicit provider, fallback provider, and finally a safe transform.
    /// </summary>
    protected static IKTargetIntent ResolveTargetIntent(
        IKTargetIntentProvider? provider,
        IKTargetIntentProvider? fallbackProvider,
        Transform3D safeFallbackTransform)
    {
        return provider is not null && IsInstanceValid(provider)
            ? provider.GetTargetIntent()
            : fallbackProvider is not null && IsInstanceValid(fallbackProvider)
            ? fallbackProvider.GetTargetIntent()
            : new IKTargetIntent(safeFallbackTransform, 0.0f);
    }

    /// <summary>
    /// Applies provider influence to all direct and side-effect modifiers for a limb.
    /// </summary>
    protected static float ApplyModifierInfluence(IKTargetIntent intent, params SkeletonModifier3D?[] modifiers)
    {
        float influence = Mathf.Clamp(intent.DesiredInfluence, 0.0f, 1.0f);
        bool active = influence > 0.0f;

        foreach (SkeletonModifier3D? modifier in modifiers)
        {
            ApplyModifierInfluence(modifier, influence, active);
        }

        return influence;
    }

    /// <summary>
    /// Applies provider influence to all direct and side-effect modifiers for a limb.
    /// </summary>
    protected static float ApplyModifierInfluence(IKTargetIntent intent, SkeletonModifier3D? primaryModifier, SkeletonModifier3D[] modifierGroup)
    {
        float influence = Mathf.Clamp(intent.DesiredInfluence, 0.0f, 1.0f);
        bool active = influence > 0.0f;

        ApplyModifierInfluence(primaryModifier, influence, active);
        foreach (SkeletonModifier3D modifier in modifierGroup)
        {
            ApplyModifierInfluence(modifier, influence, active);
        }

        return influence;
    }

    private static void ApplyModifierInfluence(SkeletonModifier3D? modifier, float influence, bool active)
    {
        if (modifier is null || !IsInstanceValid(modifier))
        {
            return;
        }

        modifier.Influence = influence;
        modifier.Active = active;
    }
}
