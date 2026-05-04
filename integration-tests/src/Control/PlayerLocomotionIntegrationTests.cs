using AlleyCat.Control;
using AlleyCat.IK.Pose;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// Integration coverage for PlayerLocomotion as a concrete runtime component.
/// </summary>
public sealed partial class PlayerLocomotionIntegrationTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>
    /// Verifies the component enables its own physics processing during ready.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_Ready_EnablesPhysicsProcessing()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Assert.True(rig.Locomotion.IsPhysicsProcessing(), "PlayerLocomotion should own physics-tick processing after ready.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies movement intent alone does not synthesise direct planar velocity.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_SetMovementInput_DoesNotDriveDirectPlanarVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: CreateLocomotionAnimationTree());

        try
        {
            rig.Locomotion.SetMovementInput(new Vector2(0f, 1f));

            rig.Locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected movement intent alone to avoid synthesising planar velocity. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies smooth-turn input rotates the controlled body.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_SetRotationInput_SmoothTurnRotatesBody()
    {
        SceneTree sceneTree = GetSceneTree();
        RecordingTurnPlayerLocomotion locomotion = new();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, locomotion: locomotion);

        try
        {
            locomotion.TurnMode = TurnMode.Smooth;
            locomotion.RotationSpeedMultiplier = 2f;
            locomotion.SmoothTurnSensitivity = 3f;
            locomotion.SetRotationInput(new Vector2(-0.5f, 0f));

            locomotion._PhysicsProcess(0.2d);

            Assert.True(Mathf.Abs(locomotion.LastAppliedYawDelta - 0.6f) <= Tolerance, $"Expected smooth turn to apply 0.6 radians of yaw. Got {locomotion.LastAppliedYawDelta:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies snap-turn cooldown prevents a second immediate turn.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_SnapTurnCooldown_BlocksImmediateSecondTurn()
    {
        SceneTree sceneTree = GetSceneTree();
        RecordingTurnPlayerLocomotion locomotion = new();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, locomotion: locomotion);

        try
        {
            locomotion.TurnMode = TurnMode.Snap;
            locomotion.SnapTurnAngleDegrees = 45f;
            locomotion.SnapTurnActivationThreshold = 0.5f;
            locomotion.SnapTurnCooldownSeconds = 0.25f;
            locomotion.SetRotationInput(new Vector2(0.8f, 0f));

            locomotion._PhysicsProcess(0.016d);
            float firstTurnDelta = locomotion.LastAppliedYawDelta;

            locomotion._PhysicsProcess(0.016d);

            Assert.True(Mathf.Abs(firstTurnDelta + (Mathf.Pi * 0.25f)) <= Tolerance, $"Expected first snap turn to apply -45 degrees. Got {firstTurnDelta:F6}.");
            Assert.True(Mathf.IsZeroApprox(locomotion.LastAppliedYawDelta), $"Expected snap-turn cooldown to block the second immediate turn. Got {locomotion.LastAppliedYawDelta:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies sub-deadzone movement intent remains suppressed.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_MovementDeadzone_SuppressesLowMagnitudeInput()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: CreateLocomotionAnimationTree());

        try
        {
            rig.Locomotion.InputDeadzone = 0.15f;
            rig.Locomotion.SetMovementInput(new Vector2(0.1f, 0.1f));

            rig.Locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected deadzoned movement input to preserve zero planar velocity. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies multiple permission sources aggregate movement and rotation decisions independently.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_MultiplePermissionSources_AggregatePredictably()
    {
        SceneTree sceneTree = GetSceneTree();
        StubPermissionSource movementBlockedSource = new(LocomotionPermissions.RotationOnly);
        StubPermissionSource rotationBlockedSource = new(new LocomotionPermissions(MovementAllowed: true, RotationAllowed: false));
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0064f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            permissionSourceNodes: [movementBlockedSource, rotationBlockedSource],
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            locomotion.TurnMode = TurnMode.Smooth;
            locomotion.RotationSpeedMultiplier = 2f;
            locomotion.SmoothTurnSensitivity = 3f;
            locomotion.SetMovementInput(new Vector2(0f, 1f));
            locomotion.SetRotationInput(new Vector2(-0.5f, 0f));

            Basis initialBasis = rig.Body.GlobalBasis;
            StartPlayback(rig.AnimationTree, "Walking");

            locomotion._PhysicsProcess(0.2d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected aggregated movement block to suppress root-motion velocity. Got {rig.Body.Velocity}.");
            Assert.True(rig.Body.GlobalBasis.IsEqualApprox(initialBasis), "Expected aggregated rotation block to suppress yaw changes.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pose-driven permissions block locomotion velocity outside the allowed standing threshold.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_PoseSource_BlocksMovementOutsideAllowedStandingThreshold()
    {
        SceneTree sceneTree = GetSceneTree();
        StandingPoseState standingState = new()
        {
            MovementAllowedMaximumPoseBlend = 0.15f,
            FullCrouchReferenceHipHeightRatio = 0.45f,
        };

        PoseStateMachine stateMachine = CreatePoseStateMachine(standingState);
        _ = stateMachine.Tick(CreateStandingPoseContext(restHeadHeight: 1.6f, restHeadY: 1.6f, currentHeadY: 1.384f));
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0064f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            permissionSourceNodes: [stateMachine],
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.SetMovementInput(new Vector2(0f, 1f));

            locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected blocked standing pose to suppress locomotion velocity. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pose-driven permissions preserve root-motion locomotion near full standing.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_PoseSource_AllowsMovementInNearFullStanding()
    {
        SceneTree sceneTree = GetSceneTree();
        StandingPoseState standingState = new()
        {
            MovementAllowedMaximumPoseBlend = 0.15f,
            FullCrouchReferenceHipHeightRatio = 0.45f,
        };

        PoseStateMachine stateMachine = CreatePoseStateMachine(standingState);
        _ = stateMachine.Tick(CreateStandingPoseContext(restHeadHeight: 1.6f, restHeadY: 1.6f, currentHeadY: 1.528f));
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0064f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            permissionSourceNodes: [stateMachine],
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.SetMovementInput(new Vector2(0f, 1f));

            locomotion._PhysicsProcess(0.016d);

            Vector3 velocity = rig.Body.Velocity;
            Assert.True(Mathf.Abs(velocity.Z + 0.4f) <= Tolerance, $"Expected allowed standing pose to preserve root-motion velocity. Got {velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pose-driven permissions continue to allow rotation across poses.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_PoseSource_AllowsRotationAcrossPoses()
    {
        SceneTree sceneTree = GetSceneTree();
        PoseStateMachine stateMachine = CreatePoseStateMachine(new KneelingPoseState());
        _ = stateMachine.Tick(new PoseStateContext());
        RecordingTurnPlayerLocomotion locomotion = new();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, permissionSourceNodes: [stateMachine], locomotion: locomotion);

        try
        {
            locomotion.TurnMode = TurnMode.Smooth;
            locomotion.RotationSpeedMultiplier = 2f;
            locomotion.SmoothTurnSensitivity = 3f;
            locomotion.SetRotationInput(new Vector2(-0.5f, 0f));

            locomotion._PhysicsProcess(0.2d);

            Assert.True(Mathf.Abs(locomotion.LastAppliedYawDelta - 0.6f) <= Tolerance, $"Expected rotation to remain allowed while kneeling. Got {locomotion.LastAppliedYawDelta:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies active locomotion velocity comes from runtime root motion rather than input magnitude.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_RootMotionActive_UsesRuntimeRootMotionVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0128f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.SetMovementInput(new Vector2(0f, 0.25f));

            locomotion._PhysicsProcess(0.016d);

            Vector3 velocity = rig.Body.Velocity;
            Assert.True(Mathf.Abs(velocity.X) <= Tolerance, $"Expected root motion to stay on the authored forward axis. Got {velocity.X:F6}.");
            Assert.True(Mathf.Abs(velocity.Z + 0.8f) <= Tolerance, $"Expected runtime root motion to resolve to -0.8 m/s on Z. Got {velocity.Z:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies locomotion root motion is transformed through the configured world-space reference.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_RootMotionActive_TransformsVelocityIntoWorldSpace()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0128f),
            RootMotionBasis = Basis.Identity.Rotated(Vector3.Up, Mathf.Pi * 0.5f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.SetMovementInput(new Vector2(0f, 1f));

            locomotion._PhysicsProcess(0.016d);

            Vector3 velocity = rig.Body.Velocity;
            Assert.True(Mathf.Abs(velocity.X + 0.8f) <= Tolerance, $"Expected rotated root motion to resolve to -0.8 m/s on X. Got {velocity.X:F6}.");
            Assert.True(Mathf.Abs(velocity.Z) <= Tolerance, $"Expected rotated root motion to remove forward Z velocity. Got {velocity.Z:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies zero root motion does not invent planar velocity while locomotion input is held.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_RootMotionActive_ZeroDelta_DoesNotInventPlanarVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = Vector3.Zero,
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.SetMovementInput(new Vector2(0f, 1f));

            locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected zero root motion to remain stationary even while locomotion input is held. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies inactive locomotion states do not synthesise velocity from input.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_RootMotionInactive_DoesNotSynthesizeVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0128f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "StandingCrouching");
            locomotion.SetMovementInput(new Vector2(0f, 1f));

            locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected inactive locomotion root motion to avoid synthesising velocity. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies crawl locomotion overrides still resolve the correct movement state and root-motion path.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_AnimationSource_UsesCrawlLocomotionStatePairAndRootMotionPath()
    {
        SceneTree sceneTree = GetSceneTree();
        StubAnimationSource source = new(
            LocomotionPermissions.Allowed,
            new LocomotionStateTarget(
                new StringName("AllFours"),
                new StringName("AllFoursForward")));
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0064f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            permissionSourceNodes: [source],
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "AllFours");
            locomotion.SetMovementInput(new Vector2(0f, 1f));

            locomotion._PhysicsProcess(0.016d);
            rig.AnimationTree.Advance(0.0);

            Assert.Equal("AllFoursForward", ResolvePlayback(rig.AnimationTree).GetCurrentNode().ToString());

            locomotion._PhysicsProcess(0.016d);

            Assert.True(Mathf.Abs(rig.Body.Velocity.Z + 0.4f) <= Tolerance, $"Expected crawl locomotion override to use root-motion velocity. Got {rig.Body.Velocity}.");

            locomotion.SetMovementInput(Vector2.Zero);
            locomotion._PhysicsProcess(0.016d);
            rig.AnimationTree.Advance(0.0);

            Assert.Equal("AllFours", ResolvePlayback(rig.AnimationTree).GetCurrentNode().ToString());
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the real all-fours pose state machine keeps crawl locomotion root-motion-driven across repeated ticks.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_AllFoursPoseStateMachine_KeepsCrawlLocomotionActiveAcrossTicks()
    {
        SceneTree sceneTree = GetSceneTree();
        AnimationTree animationTree = CreateLocomotionAnimationTree();
        AllFoursPoseState allFoursState = new();
        PoseStateMachine stateMachine = new()
        {
            States = [allFoursState],
            InitialStateId = allFoursState.Id,
            Active = true,
            AnimationTree = animationTree,
        };
        RootMotionPlayerLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0064f),
        };

        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            permissionSourceNodes: [stateMachine],
            animationTree: animationTree,
            locomotion: locomotion);

        try
        {
            stateMachine.EnsureInitialStateResolved();

            PoseStateContext crawlContext = new()
            {
                Skeleton = new Skeleton3D
                {
                    GlobalTransform = Transform3D.Identity,
                },
                AnimationTree = animationTree,
                HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0.95f, 0.80f)),
                RestHeadHeight = 1.0f,
                Delta = 0.016,
            };

            _ = stateMachine.Tick(crawlContext);
            _ = stateMachine.Tick(crawlContext);

            Assert.Equal(LocomotionPermissions.Allowed, stateMachine.LocomotionPermissions);
            Assert.True(stateMachine.LocomotionStateTarget.HasValue);

            StartPlayback(animationTree, "AllFours");
            locomotion.SetMovementInput(new Vector2(0f, 1f));
            locomotion._PhysicsProcess(0.016d);
            animationTree.Advance(0.0);

            Assert.Equal("AllFoursForward", ResolvePlayback(animationTree).GetCurrentNode().ToString());

            locomotion._PhysicsProcess(0.016d);

            Assert.True(Mathf.Abs(rig.Body.Velocity.Z + 0.4f) <= Tolerance, $"Expected crawl locomotion to remain root-motion-driven across repeated ticks. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    private static async Task<LocomotionTestRig> CreateRigAsync(
        SceneTree sceneTree,
        Node[]? permissionSourceNodes = null,
        AnimationTree? animationTree = null,
        PlayerLocomotion? locomotion = null)
    {
        Node3D root = new()
        {
            Name = "PlayerLocomotionTestRoot",
        };

        CharacterBody3D body = new()
        {
            Name = "Body",
        };

        animationTree ??= new AnimationTree
        {
            Name = "AnimationTree",
        };

        Node3D rootMotionReference = new()
        {
            Name = "RootMotionReference",
        };

        locomotion ??= new PlayerLocomotion();

        locomotion.Name = "Locomotion";
        locomotion.TargetCharacterBodyNode = body;
        locomotion.AnimationTree = animationTree;
        locomotion.RootMotionReference = rootMotionReference;
        locomotion.PermissionSourceNodes = permissionSourceNodes ?? [];

        root.AddChild(body);
        root.AddChild(animationTree);
        root.AddChild(rootMotionReference);

        if (permissionSourceNodes is not null)
        {
            foreach (Node permissionSourceNode in permissionSourceNodes)
            {
                root.AddChild(permissionSourceNode);
            }
        }

        body.AddChild(locomotion);
        sceneTree.Root.AddChild(root);

        await WaitForFramesAsync(sceneTree, 2);
        locomotion._Ready();

        return new LocomotionTestRig(root, body, animationTree, rootMotionReference, locomotion);
    }

    private static async Task DestroyRigAsync(SceneTree sceneTree, LocomotionTestRig rig)
    {
        rig.Root.QueueFree();
        await WaitForFramesAsync(sceneTree, 1);
    }

    private static PoseStateMachine CreatePoseStateMachine(PoseState initialState)
    {
        PoseStateMachine stateMachine = new()
        {
            States = [initialState],
            InitialStateId = initialState.Id,
            Active = true,
        };

        stateMachine.EnsureInitialStateResolved();
        return stateMachine;
    }

    private static PoseStateContext CreateStandingPoseContext(float restHeadHeight, float restHeadY, float currentHeadY)
        => new()
        {
            RestHeadHeight = restHeadHeight,
            HeadTargetRestTransform = new Transform3D(Basis.Identity, new Vector3(0f, restHeadY, 0f)),
            HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, currentHeadY, 0f)),
        };

    private static AnimationTree CreateLocomotionAnimationTree()
    {
        AnimationNodeStateMachine stateMachine = new();
        stateMachine.AddNode("StandingCrouching", new AnimationNodeAnimation(), Vector2.Zero);
        stateMachine.AddNode("Walking", new AnimationNodeAnimation(), Vector2.Right * 200f);
        stateMachine.AddNode("AllFours", new AnimationNodeAnimation(), Vector2.Up * 200f);
        stateMachine.AddNode("AllFoursForward", new AnimationNodeAnimation(), new Vector2(200f, -200f));
        stateMachine.AddTransition("Start", "StandingCrouching", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Start", "AllFours", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("StandingCrouching", "Walking", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Walking", "StandingCrouching", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("AllFours", "AllFoursForward", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("AllFoursForward", "AllFours", new AnimationNodeStateMachineTransition());

        return new AnimationTree
        {
            Name = "AnimationTree",
            TreeRoot = stateMachine,
            Active = true,
        };
    }

    private static void StartPlayback(AnimationTree animationTree, string nodeName)
    {
        AnimationNodeStateMachinePlayback playback = ResolvePlayback(animationTree);
        playback.Start(nodeName, true);
        animationTree.Advance(0.0);
    }

    private static AnimationNodeStateMachinePlayback ResolvePlayback(AnimationTree animationTree)
        => animationTree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>()
           ?? throw new Xunit.Sdk.XunitException("Expected AnimationTree playback to be available.");

    private sealed partial class RootMotionPlayerLocomotion : PlayerLocomotion
    {
        public Vector3 RootMotionPositionDelta
        {
            get;
            set;
        }

        public Basis RootMotionBasis
        {
            get;
            set;
        } = Basis.Identity;

        protected override Vector3 GetRootMotionPositionDelta() => RootMotionPositionDelta;

        protected override Basis GetRootMotionReferenceBasis() => RootMotionBasis;
    }

    private sealed partial class RecordingTurnPlayerLocomotion : PlayerLocomotion
    {
        public float LastAppliedYawDelta
        {
            get;
            private set;
        }

        protected override void ApplyYawRotation(float yawDelta)
        {
            LastAppliedYawDelta = yawDelta;
            base.ApplyYawRotation(yawDelta);
        }

        public override void _PhysicsProcess(double delta)
        {
            LastAppliedYawDelta = 0f;
            base._PhysicsProcess(delta);
        }
    }

    private sealed partial class StubPermissionSource(LocomotionPermissions permissions) : Node, ILocomotionPermissionSource
    {
        public LocomotionPermissions LocomotionPermissions => permissions;
    }

    private sealed partial class StubAnimationSource(
        LocomotionPermissions permissions,
        LocomotionStateTarget? target) : Node, ILocomotionPermissionSource, ILocomotionAnimationSource
    {
        public LocomotionPermissions LocomotionPermissions => permissions;

        public LocomotionStateTarget? LocomotionStateTarget => target;
    }

    private sealed record LocomotionTestRig(
        Node3D Root,
        CharacterBody3D Body,
        AnimationTree AnimationTree,
        Node3D RootMotionReference,
        PlayerLocomotion Locomotion);
}
