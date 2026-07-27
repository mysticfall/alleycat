using AlleyCat.Control.Locomotion;
using AlleyCat.IK.Pose;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// Integration coverage for CharacterLocomotion as a concrete runtime component.
/// </summary>
public sealed partial class CharacterLocomotionIntegrationTests
{
    private const float Tolerance = 1e-4f;
    private const string ReferenceFemaleNpcScenePath = "res://assets/characters/reference/ally_npc.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string PlayerAnimationTreeRootUID = "uid://bge48ng374i85";
    private const string NpcAnimationTreeRootUID = "uid://c485owf86etdu";
    private const string NpcAnimationGraphPath = "res://assets/characters/templates/animation/animation_tree_root_npc.tres";
    private const string LibraryPath = "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres";

    /// <summary>
    /// Verifies the component enables its own physics processing during ready.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_Ready_EnablesPhysicsProcessing()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Assert.True(rig.Locomotion.IsPhysicsProcessing(), "CharacterLocomotion should own physics-tick processing after ready.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies locomotion exposes no dormant snap-turn type or runtime configuration.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterLocomotion_SnapTurnConfiguration_IsAbsent()
    {
        Type locomotionType = typeof(CharacterLocomotion);

        Assert.Null(locomotionType.Assembly.GetType("AlleyCat.Control.Locomotion.TurnMode"));
        Assert.Null(locomotionType.GetProperty("TurnMode"));
        Assert.Null(locomotionType.GetProperty("SnapTurnAngleDegrees"));
        Assert.Null(locomotionType.GetProperty("SnapTurnCooldownSeconds"));
        Assert.Null(locomotionType.GetProperty("SnapTurnActivationThreshold"));
    }

    /// <summary>
    /// Verifies missing root-motion authoring fails explicitly instead of falling back to a reference-specific path.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterLocomotion_MissingRootMotionReference_FailsFastWithAuthoringMessage()
    {
        CharacterBody3D body = new()
        {
            Name = "Body",
        };
        CharacterLocomotion locomotion = new()
        {
            Name = "Locomotion",
            TargetCharacterBodyNode = body,
            AnimationTree = new AnimationTree { Name = "AnimationTree" },
        };
        body.AddChild(locomotion);

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(locomotion._Ready);

            Assert.Contains(nameof(CharacterLocomotion.RootMotionReference), exception.Message, StringComparison.Ordinal);
            Assert.Contains("install a character module", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            body.QueueFree();
        }
    }

    /// <summary>
    /// Verifies movement intent alone does not synthesise direct planar velocity.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_Move_DoesNotDriveDirectPlanarVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: CreateLocomotionAnimationTree());

        try
        {
            rig.Locomotion.Move(new Vector2(0f, 1f));

            rig.Locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected movement intent alone to avoid synthesising planar velocity. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies locomotion leaves the AnimationTree start sentinel and enters the configured idle state.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_StartSentinel_StartsConfiguredIdleState()
    {
        SceneTree sceneTree = GetSceneTree();
        AnimationTree animationTree = CreateLocomotionAnimationTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: animationTree);

        try
        {
            string initialState = ResolvePlayback(animationTree).GetCurrentNode().ToString();
            Assert.True(initialState is "Start" or "Idle", $"Expected Start or Idle before manual physics; got {initialState}.");

            rig.Locomotion._PhysicsProcess(0.016d);
            animationTree.Advance(0.0);

            Assert.Equal("Idle", ResolvePlayback(animationTree).GetCurrentNode().ToString());
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies player-specific trees can keep their standing/crouching idle state while generic NPC trees use Idle.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_CustomIdleState_StartsAndTravelsToWalking()
    {
        SceneTree sceneTree = GetSceneTree();
        AnimationTree animationTree = CreatePlayerLocomotionAnimationTree();
        CharacterLocomotion locomotion = new()
        {
            IdleAnimationStateName = new StringName("StandingCrouching"),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: animationTree,
            locomotion: locomotion);

        try
        {
            rig.Locomotion._PhysicsProcess(0.016d);
            animationTree.Advance(0.0);

            Assert.Equal("StandingCrouching", ResolvePlayback(animationTree).GetCurrentNode().ToString());

            rig.Locomotion.Move(new Vector2(0f, 1f));
            rig.Locomotion._PhysicsProcess(0.016d);
            animationTree.Advance(0.0);

            Assert.Equal("Walking", ResolvePlayback(animationTree).GetCurrentNode().ToString());
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies movement and signed turn parameters update independently and reverse without transition delay.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_BlendParameters_PreserveMovementAndReverseTurnImmediately()
    {
        SceneTree sceneTree = GetSceneTree();
        AnimationTree animationTree = CreateBlendedLocomotionAnimationTree();
        CharacterLocomotion locomotion = new()
        {
            AnimationBlendParameter = new StringName("parameters/Walking/Movement/blend_position"),
            AnimationTurnBlendParameter = new StringName("parameters/Walking/Turn/blend_amount"),
            RotationSpeedMultiplier = 1f,
            SmoothTurnSensitivity = 2.5f,
        };
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: animationTree, locomotion: locomotion);

        try
        {
            locomotion.Move(new Vector2(0.25f, 0.5f));
            locomotion.Rotate(new Vector2(-0.2f, 0f));
            locomotion._PhysicsProcess(0.016d);

            Vector2 input = new(0.25f, 0.5f);
            float remappedLength = (input.Length() - locomotion.InputDeadzone) / (1.0f - locomotion.InputDeadzone);
            Vector2 expectedMovement = input.Normalized() * remappedLength;
            Assert.Equal(expectedMovement, animationTree.Get(locomotion.AnimationBlendParameter).AsVector2());
            Assert.Equal(-0.1470588f, animationTree.Get(locomotion.AnimationTurnBlendParameter).AsSingle(), Tolerance);

            locomotion.Rotate(new Vector2(0.2f, 0f));
            locomotion._PhysicsProcess(0.016d);

            Assert.Equal(0.1470588f, animationTree.Get(locomotion.AnimationTurnBlendParameter).AsSingle(), Tolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies shipped character scenes expose one active animation tree per actor with the expected start state.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_ReferenceScenes_WireAnimationTreesForRuntimePlayback()
    {
        await AssertReferenceSceneAnimationTreesAsync(
            ReferenceFemaleNpcScenePath,
            [new ExpectedAnimationTree(NpcAnimationTreeRootUID, "Idle")]);

        await AssertReferenceSceneAnimationTreesAsync(
            PlayerScenePath,
            [new ExpectedAnimationTree(PlayerAnimationTreeRootUID, "StandingCrouching")]);

    }

    /// <summary>
    /// Verifies smooth-turn input enters Walking but cannot rotate without animation root yaw.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_Rotate_SmoothTurnDoesNotApplyDirectYaw()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new();
        AnimationTree animationTree = CreateLocomotionAnimationTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: animationTree, locomotion: locomotion);

        try
        {
            locomotion.Rotate(new Vector2(-0.5f, 0f));

            locomotion._PhysicsProcess(0.2d);
            animationTree.Advance(0d);
            locomotion._PhysicsProcess(0.2d);

            Assert.Equal("Walking", ResolvePlayback(animationTree).GetCurrentNode().ToString());
            Assert.Equal(0f, locomotion.TotalAppliedYawDelta, Tolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies every finite walking root-yaw sample is consumed without snap clamping or cooldown.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_RootYaw_ConsumesEachWalkingSampleContinuously()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionYawDelta = -1f,
        };
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: CreateLocomotionAnimationTree(), locomotion: locomotion);

        try
        {
            locomotion.Rotate(new Vector2(0.8f, 0f));
            StartPlayback(rig.AnimationTree, "Walking");

            locomotion._PhysicsProcess(0.016d);
            locomotion._PhysicsProcess(0.016d);

            Assert.Equal(-2f, locomotion.TotalAppliedYawDelta, Tolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies production left arc root yaw reaches the actor once with its authored positive sign.
    /// </summary>
    [Fact]
    public Task ProductionGraph_WalkArcLeft_AppliesAuthoredRootYawToActorOnce()
        => AssertProductionRootYawApplicationAsync("WalkArcLeft", expectedYawSign: 1f);

    /// <summary>
    /// Verifies production right pivot root yaw reaches the actor once with its authored negative sign.
    /// </summary>
    [Fact]
    public Task ProductionGraph_TurnInPlaceRight_AppliesAuthoredRootYawToActorOnce()
        => AssertProductionRootYawApplicationAsync("TurnInPlaceRight90", expectedYawSign: -1f);

    /// <summary>
    /// Verifies sub-deadzone movement intent remains suppressed.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_MovementDeadzone_SuppressesLowMagnitudeInput()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: CreateLocomotionAnimationTree());

        try
        {
            rig.Locomotion.InputDeadzone = 0.15f;
            rig.Locomotion.Move(new Vector2(0.1f, 0.1f));

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
    public async Task CharacterLocomotion_MultiplePermissionSources_AggregatePredictably()
    {
        SceneTree sceneTree = GetSceneTree();
        StubPermissionSource movementBlockedSource = new(LocomotionPermissions.RotationOnly);
        StubPermissionSource rotationBlockedSource = new(new LocomotionPermissions(MovementAllowed: true, RotationAllowed: false));
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.RotationSpeedMultiplier = 2f;
            locomotion.SmoothTurnSensitivity = 3f;
            locomotion.Move(new Vector2(0f, 1f));
            locomotion.Rotate(new Vector2(-0.5f, 0f));

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
    public async Task CharacterLocomotion_PoseSource_BlocksMovementOutsideAllowedStandingThreshold()
    {
        SceneTree sceneTree = GetSceneTree();
        StandingPoseState standingState = new()
        {
            MovementAllowedMaximumPoseBlend = 0.15f,
            FullCrouchReferenceHipHeightRatio = 0.45f,
        };

        PoseStateMachine stateMachine = CreatePoseStateMachine(standingState);
        _ = stateMachine.Tick(CreateStandingPoseContext(restHeadHeight: 1.6f, restHeadY: 1.6f, currentHeadY: 1.384f));
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.Move(new Vector2(0f, 1f));

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
    public async Task CharacterLocomotion_PoseSource_AllowsMovementInNearFullStanding()
    {
        SceneTree sceneTree = GetSceneTree();
        StandingPoseState standingState = new()
        {
            MovementAllowedMaximumPoseBlend = 0.15f,
            FullCrouchReferenceHipHeightRatio = 0.45f,
        };

        PoseStateMachine stateMachine = CreatePoseStateMachine(standingState);
        _ = stateMachine.Tick(CreateStandingPoseContext(restHeadHeight: 1.6f, restHeadY: 1.6f, currentHeadY: 1.528f));
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.Move(new Vector2(0f, 1f));

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
    public async Task CharacterLocomotion_PoseSource_AllowsRotationAcrossPoses()
    {
        SceneTree sceneTree = GetSceneTree();
        PoseStateMachine stateMachine = CreatePoseStateMachine(new KneelingPoseState());
        _ = stateMachine.Tick(new PoseStateContext());
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionYawDelta = 0.2f,
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            permissionSourceNodes: [stateMachine],
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            locomotion.Rotate(new Vector2(-0.5f, 0f));
            StartPlayback(rig.AnimationTree, "Walking");

            locomotion._PhysicsProcess(0.2d);

            Assert.Equal(0.2f, locomotion.TotalAppliedYawDelta, Tolerance);
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
    public async Task CharacterLocomotion_RootMotionActive_UsesRuntimeRootMotionVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.Move(new Vector2(0f, 0.25f));

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
    /// Verifies independently sampled root-motion deltas retain per-frame delta semantics across a simulated clip loop.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_RootMotionDeltasAcrossLoopBoundary_PreservePerFrameDeltaSemantics()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.1f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.Move(Vector2.Up);

            float integratedDisplacement = 0f;
            for (int sample = 0; sample < 3; sample++)
            {
                locomotion._PhysicsProcess(0.1d);
                Assert.Equal(-1f, rig.Body.Velocity.Z, Tolerance);
                integratedDisplacement += rig.Body.Velocity.Z * 0.1f;
            }

            Assert.Equal(-0.3f, integratedDisplacement, Tolerance);
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
    public async Task CharacterLocomotion_RootMotionActive_TransformsVelocityIntoWorldSpace()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.Move(new Vector2(0f, 1f));

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
    /// Verifies one walking sample drives simultaneous translation and yaw exactly once.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_RootMotionActive_AppliesSimultaneousTranslationAndYawOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0064f),
            RootMotionYawDelta = 0.15f,
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.Move(Vector2.Up);
            locomotion.Rotate(new Vector2(-0.5f, 0f));

            locomotion._PhysicsProcess(0.016d);

            Assert.Equal(-0.4f, rig.Body.Velocity.Z, Tolerance);
            Assert.Equal(0.15f, locomotion.TotalAppliedYawDelta, Tolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies smooth animation yaw reverses immediately with the selected root-motion sample.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_SmoothRootYaw_ReversesImmediately()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionYawDelta = -0.2f,
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.Rotate(Vector2.Right);
            locomotion._PhysicsProcess(0.016d);

            locomotion.RootMotionYawDelta = 0.3f;
            locomotion.Rotate(Vector2.Left);
            locomotion._PhysicsProcess(0.016d);

            Assert.Equal(0.1f, locomotion.TotalAppliedYawDelta, Tolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies movement and rotation permissions independently gate their matching root-motion components.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_RootMotionPermissions_GateComponentsIndependently()
    {
        SceneTree sceneTree = GetSceneTree();
        MutablePermissionSource permissions = new(LocomotionPermissions.RotationOnly);
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0064f),
            RootMotionYawDelta = 0.2f,
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            permissionSourceNodes: [permissions],
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.Move(Vector2.Up);
            locomotion.Rotate(Vector2.Left);
            locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox());
            Assert.Equal(0.2f, locomotion.TotalAppliedYawDelta, Tolerance);

            permissions.LocomotionPermissions = new LocomotionPermissions(MovementAllowed: true, RotationAllowed: false);
            locomotion._PhysicsProcess(0.016d);

            Assert.Equal(-0.4f, rig.Body.Velocity.Z, Tolerance);
            Assert.Equal(0.2f, locomotion.TotalAppliedYawDelta, Tolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies non-finite root translation and yaw cannot reach the character body.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CharacterLocomotion_RootMotionNonFinite_IgnoresBothComponents()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(float.NaN, 0f, float.PositiveInfinity),
            RootMotionYawDelta = float.NaN,
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            locomotion.Move(Vector2.Up);
            locomotion.Rotate(Vector2.Left);

            locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox());
            Assert.Equal(0f, locomotion.TotalAppliedYawDelta);
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
    public async Task CharacterLocomotion_RootMotionActive_ZeroDelta_DoesNotInventPlanarVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.Move(new Vector2(0f, 1f));

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
    public async Task CharacterLocomotion_RootMotionInactive_DoesNotSynthesizeVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        RootMotionCharacterLocomotion locomotion = new()
        {
            RootMotionPositionDelta = new Vector3(0f, 0f, -0.0128f),
        };
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            animationTree: CreateLocomotionAnimationTree(),
            locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Idle");
            locomotion.Move(new Vector2(0f, 1f));

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
    public async Task CharacterLocomotion_AnimationSource_UsesCrawlLocomotionStatePairAndRootMotionPath()
    {
        SceneTree sceneTree = GetSceneTree();
        StubAnimationSource source = new(
            LocomotionPermissions.Allowed,
            new LocomotionStateTarget(
                new StringName("AllFours"),
                new StringName("AllFoursForward")));
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.Move(new Vector2(0f, 1f));

            locomotion._PhysicsProcess(0.016d);
            rig.AnimationTree.Advance(0.0);

            Assert.Equal("AllFoursForward", ResolvePlayback(rig.AnimationTree).GetCurrentNode().ToString());

            locomotion._PhysicsProcess(0.016d);

            Assert.True(Mathf.Abs(rig.Body.Velocity.Z + 0.4f) <= Tolerance, $"Expected crawl locomotion override to use root-motion velocity. Got {rig.Body.Velocity}.");

            locomotion.Move(Vector2.Zero);
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
    public async Task CharacterLocomotion_AllFoursPoseStateMachine_KeepsCrawlLocomotionActiveAcrossTicks()
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
        RootMotionCharacterLocomotion locomotion = new()
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
            locomotion.Move(new Vector2(0f, 1f));
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
        CharacterLocomotion? locomotion = null)
    {
        Node3D root = new()
        {
            Name = "CharacterLocomotionTestRoot",
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

        locomotion ??= new CharacterLocomotion();

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
        await AddChildToRootAsync(sceneTree, root);

        await WaitForFramesAsync(sceneTree, 2);
        locomotion._Ready();

        return new LocomotionTestRig(root, body, animationTree, rootMotionReference, locomotion);
    }

    private static async Task AssertProductionRootYawApplicationAsync(string graphPointName, float expectedYawSign)
    {
        SceneTree sceneTree = GetSceneTree();
        Quaternion rootRotation = GetProductionGraphRootRotation(graphPointName);
        RootRotationCharacterLocomotion locomotion = new()
        {
            RootMotionRotation = rootRotation,
        };
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, animationTree: CreateLocomotionAnimationTree(), locomotion: locomotion);

        try
        {
            StartPlayback(rig.AnimationTree, "Walking");
            float rootYaw = rootRotation.GetEuler().Y;
            float actorYawBefore = rig.Body.GlobalRotation.Y;
            Assert.True(float.IsFinite(rootYaw) && (rootYaw * expectedYawSign) > 0.0001f,
                $"Expected production graph point {graphPointName} Root yaw sign {expectedYawSign:F0}; got {rootYaw:F6}.");

            locomotion._PhysicsProcess(1.0 / 30.0);
            float actorYawAfterFirstApplication = rig.Body.GlobalRotation.Y;
            float actorYawDelta = Mathf.Wrap(actorYawAfterFirstApplication - actorYawBefore, -Mathf.Pi, Mathf.Pi);
            Assert.Equal(rootYaw, actorYawDelta, 0.001f);

            locomotion.RootMotionRotation = Quaternion.Identity;
            locomotion._PhysicsProcess(1.0 / 30.0);
            float actorYawAfterSecondApplication = rig.Body.GlobalRotation.Y;
            Assert.Equal(actorYawAfterFirstApplication, actorYawAfterSecondApplication, 0.001f);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    private static Quaternion GetProductionGraphRootRotation(string graphPointName)
    {
        AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(ResourceLoader.Load(NpcAnimationGraphPath), exactMatch: false);
        AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(root.GetNode("States"), exactMatch: false);
        AnimationNodeBlendTree walking = Assert.IsType<AnimationNodeBlendTree>(states.GetNode("Walking"), exactMatch: false);
        AnimationNodeBlendSpace2D locomotion = Assert.IsType<AnimationNodeBlendSpace2D>(walking.GetNode("Locomotion"), exactMatch: false);
        AnimationNodeAnimation graphPoint = Assert.IsType<AnimationNodeAnimation>(
            locomotion.GetBlendPointNode(locomotion.FindBlendPointByName(graphPointName)), exactMatch: false);
        string key = graphPoint.Animation.ToString()["locomotion/".Length..];
        Animation animation = Assert.IsType<AnimationLibrary>(ResourceLoader.Load(LibraryPath), exactMatch: false).GetAnimation(key);
        int track = animation.FindTrack(new NodePath("%GeneralSkeleton:Root"), Animation.TrackType.Rotation3D);
        Assert.True(track >= 0, $"Production graph point {graphPointName} must retain its Root rotation track.");
        Quaternion start = animation.RotationTrackInterpolate(track, 0.0);
        Quaternion sample = animation.RotationTrackInterpolate(track, animation.Length);
        return start.Inverse() * sample;
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
        stateMachine.AddNode("Idle", new AnimationNodeAnimation(), Vector2.Zero);
        stateMachine.AddNode("Walking", new AnimationNodeAnimation(), Vector2.Right * 200f);
        stateMachine.AddNode("AllFours", new AnimationNodeAnimation(), Vector2.Up * 200f);
        stateMachine.AddNode("AllFoursForward", new AnimationNodeAnimation(), new Vector2(200f, -200f));
        stateMachine.AddTransition("Start", "Idle", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Start", "AllFours", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Idle", "Walking", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Walking", "Idle", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("AllFours", "AllFoursForward", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("AllFoursForward", "AllFours", new AnimationNodeStateMachineTransition());

        return new AnimationTree
        {
            Name = "AnimationTree",
            TreeRoot = stateMachine,
            Active = true,
        };
    }

    private static AnimationTree CreatePlayerLocomotionAnimationTree()
    {
        AnimationNodeStateMachine stateMachine = new();
        stateMachine.AddNode("StandingCrouching", new AnimationNodeAnimation(), Vector2.Zero);
        stateMachine.AddNode("Walking", new AnimationNodeAnimation(), Vector2.Right * 200f);
        stateMachine.AddTransition("Start", "StandingCrouching", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("StandingCrouching", "Walking", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Walking", "StandingCrouching", new AnimationNodeStateMachineTransition());

        AnimationNodeBlendTree root = new();
        root.AddNode("States", stateMachine, Vector2.Zero);
        root.ConnectNode("output", 0, "States");

        return new AnimationTree
        {
            Name = "AnimationTree",
            TreeRoot = root,
            Active = true,
        };
    }

    private static AnimationTree CreateBlendedLocomotionAnimationTree()
    {
        AnimationNodeBlendSpace2D movement = new();
        movement.AddBlendPoint(new AnimationNodeAnimation(), Vector2.Zero);

        AnimationNodeBlend2 turn = new();
        AnimationNodeBlendTree walking = new();
        walking.AddNode("Movement", movement, Vector2.Zero);
        walking.AddNode("Turn", turn, Vector2.Right * 200f);
        walking.ConnectNode("Turn", 0, "Movement");
        walking.ConnectNode("Turn", 1, "Movement");
        walking.ConnectNode("output", 0, "Turn");

        AnimationNodeStateMachine stateMachine = new();
        stateMachine.AddNode("Idle", new AnimationNodeAnimation(), Vector2.Zero);
        stateMachine.AddNode("Walking", walking, Vector2.Right * 200f);
        stateMachine.AddTransition("Start", "Idle", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Idle", "Walking", new AnimationNodeStateMachineTransition());
        stateMachine.AddTransition("Walking", "Idle", new AnimationNodeStateMachineTransition());

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
        => animationTree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>()
           // Compatibility fixture: most tests intentionally build a simple state-machine-only tree.
           ?? animationTree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>()
           ?? throw new Xunit.Sdk.XunitException("Expected AnimationTree playback to be available.");

    private static async Task AssertReferenceSceneAnimationTreesAsync(
        string scenePath,
        IReadOnlyList<ExpectedAnimationTree> expectedTrees)
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene packedScene = Assert.IsType<PackedScene>(ResourceLoader.Load(scenePath), exactMatch: false);
        Node root = packedScene.Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            EnsureInstallerInventory(root);

            List<AnimationTree> animationTrees = [];
            CollectAnimationTrees(root, animationTrees);

            foreach (ExpectedAnimationTree expectedTree in expectedTrees)
            {
                AnimationTree animationTree = animationTrees.FirstOrDefault(tree => GetTreeRootUID(tree) == expectedTree.TreeRootUID)
                    ?? throw new Xunit.Sdk.XunitException(
                        $"Expected an AnimationTree with root UID {expectedTree.TreeRootUID} in {scenePath}. Found: "
                        + string.Join(", ", animationTrees.Select(tree => $"{tree.GetPath()}={GetTreeRootUID(tree)} path={GetAuthoredTreeRootResourcePath(tree)}")));

                Assert.True(animationTree.Active, $"Expected {scenePath} AnimationTree {expectedTree.TreeRootUID} to be active.");
                Assert.Equal(
                    Variant.Type.Vector2,
                    animationTree.Get("parameters/States/Walking/Locomotion/Movement/blend_position").VariantType);
                Assert.Equal(
                    Variant.Type.Vector2,
                    animationTree.Get("parameters/States/Walking/Locomotion/blend_position").VariantType);

                AnimationNodeStateMachine stateMachine = Assert.IsType<AnimationNodeStateMachine>(
                    Assert.IsType<AnimationNodeBlendTree>(animationTree.TreeRoot, exactMatch: false).GetNode("States"),
                    exactMatch: false);
                Assert.NotNull(stateMachine.GetNode(expectedTree.ExpectedIdleState));
                AssertStateTransition(stateMachine, "Start", expectedTree.ExpectedIdleState);

                AnimationNodeStateMachinePlayback playback = ResolvePlayback(animationTree);
                string currentState = playback.GetCurrentNode().ToString();
                Assert.True(
                    currentState == "Start" || currentState == expectedTree.ExpectedIdleState,
                    $"Expected playback to be waiting at Start or running {expectedTree.ExpectedIdleState}; got {currentState}.");
            }
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static void CollectAnimationTrees(Node node, List<AnimationTree> animationTrees)
    {
        if (node is AnimationTree animationTree)
        {
            animationTrees.Add(animationTree);
        }

        foreach (Node child in node.GetChildren())
        {
            CollectAnimationTrees(child, animationTrees);
        }
    }

    private static void EnsureInstallerInventory(Node root)
    {
        if (root.FindChild("AnimationTree", recursive: true, owned: false) is not null)
        {
            return;
        }

        Node? installer = root.GetNodeOrNull("PlayerCharacterInstaller")
            ?? root.GetNodeOrNull("NPCCharacterInstaller")
            ?? root.GetNodeOrNull("BaseCharacterInstaller");
        if (installer is null)
        {
            return;
        }

        Type installerType = installer.GetType();
        Type contextType = installerType.Assembly.GetType("AlleyCat.Core.Installer.SceneInstallationContext")
            ?? throw new InvalidOperationException("Failed to resolve loaded SceneInstallationContext type.");
        object context = Activator.CreateInstance(contextType, root, "alleycat.scene_installer")
            ?? throw new InvalidOperationException("Failed to create loaded scene installation context.");
        object result = installerType.GetMethod("Install")?.Invoke(installer, [context])
            ?? throw new InvalidOperationException("Failed to invoke reference scene installer.");
        bool succeeded = (bool)(result.GetType().GetProperty("Succeeded")?.GetValue(result) ?? false);
        if (!succeeded)
        {
            object? errors = result.GetType().GetProperty("Errors")?.GetValue(result);
            throw new Xunit.Sdk.XunitException(errors?.ToString() ?? "Reference scene installer failed.");
        }
    }

    private static async Task AddChildToRootAsync(SceneTree sceneTree, Node child)
    {
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, child);
        await WaitForNextFrameAsync(sceneTree);
        Assert.True(child.IsInsideTree(), $"Expected '{child.Name}' to enter the test scene tree.");
    }

    private static string GetTreeRootUID(AnimationTree animationTree)
    {
        if (animationTree.TreeRoot is null)
        {
            throw new Xunit.Sdk.XunitException($"AnimationTree {animationTree.GetPath()} has no tree root.");
        }

        long resourceUID = ResourceLoader.GetResourceUid(GetAuthoredTreeRootResourcePath(animationTree));
        return ResourceUid.IdToText(resourceUID);
    }

    private static string GetAuthoredTreeRootResourcePath(AnimationTree animationTree)
    {
        string? resourcePath = animationTree.TreeRoot?.ResourcePath;
        return !string.IsNullOrEmpty(resourcePath)
            ? resourcePath
            : animationTree.GetMeta("authored_tree_root_resource_path").AsString();
    }

    private static void AssertStateTransition(AnimationNodeStateMachine stateMachine, string from, string to)
    {
        Godot.Collections.Array transitions = stateMachine.Get("transitions").AsGodotArray();
        for (int index = 0; index < transitions.Count; index += 3)
        {
            if (transitions[index].AsStringName() == new StringName(from)
                && transitions[index + 1].AsStringName() == new StringName(to))
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected state transition {from} -> {to}.");
    }

    private sealed record ExpectedAnimationTree(string TreeRootUID, string ExpectedIdleState);

    private sealed partial class RootMotionCharacterLocomotion : CharacterLocomotion
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

        public float RootMotionYawDelta
        {
            get; set;
        }

        public float TotalAppliedYawDelta
        {
            get; private set;
        }

        protected override Vector3 GetRootMotionPositionDelta() => RootMotionPositionDelta;

        protected override float GetRootMotionYawDelta() => RootMotionYawDelta;

        protected override Basis GetRootMotionReferenceBasis() => RootMotionBasis;

        protected override void ApplyYawRotation(float yawDelta)
        {
            TotalAppliedYawDelta += yawDelta;
            base.ApplyYawRotation(yawDelta);
        }
    }

    private sealed partial class RootRotationCharacterLocomotion : CharacterLocomotion
    {
        public Quaternion RootMotionRotation { get; set; } = Quaternion.Identity;

        protected override Quaternion GetRootMotionRotation() => RootMotionRotation;
    }

    private sealed partial class StubPermissionSource(LocomotionPermissions permissions) : Node, ILocomotionPermissionSource
    {
        public LocomotionPermissions LocomotionPermissions => permissions;
    }

    private sealed partial class MutablePermissionSource(LocomotionPermissions permissions) : Node, ILocomotionPermissionSource
    {
        public LocomotionPermissions LocomotionPermissions
        {
            get; set;
        } = permissions;
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
        CharacterLocomotion Locomotion);
}
