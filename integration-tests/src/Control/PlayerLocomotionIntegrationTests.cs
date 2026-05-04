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
    /// Verifies forward movement input produces planar velocity on the controlled body.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_SetMovementInput_DrivesPlanarVelocity()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Locomotion.MovementSpeedMultiplier = 1.5f;
            rig.Locomotion.InputDeadzone = 0.15f;
            rig.Locomotion.SetMovementInput(new Vector2(0f, 1f));

            rig.Locomotion._PhysicsProcess(0.016d);

            Vector3 velocity = rig.Body.Velocity;
            Assert.True(Mathf.Abs(velocity.X) <= Tolerance, $"Expected no lateral velocity. Got {velocity.X:F6}.");
            Assert.True(Mathf.Abs(velocity.Z + 1.5f) <= Tolerance, $"Expected forward velocity of -1.5 m/s on Z. Got {velocity.Z:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies smooth-turn input changes the movement heading used for later locomotion.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_SetRotationInput_SmoothTurnChangesMovementHeading()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, useHeadingHarness: true);

        try
        {
            rig.Locomotion.TurnMode = TurnMode.Smooth;
            rig.Locomotion.RotationSpeedMultiplier = 2f;
            rig.Locomotion.SmoothTurnSensitivity = 3f;
            rig.Locomotion.MovementSpeedMultiplier = 1.5f;
            rig.Locomotion.InputDeadzone = 0.1f;

            HeadingAwarePlayerLocomotion locomotion = Assert.IsType<HeadingAwarePlayerLocomotion>(rig.Locomotion);

            locomotion.SetRotationInput(new Vector2(-0.5f, 0f));
            locomotion._PhysicsProcess(0.2d);
            await WaitForFramesAsync(sceneTree, 1);

            locomotion.SetRotationInput(Vector2.Zero);
            locomotion.SetMovementInput(new Vector2(0f, 1f));
            locomotion._PhysicsProcess(0.016d);

            Vector3 velocity = rig.Body.Velocity;
            Vector3 expectedDirection = new(-Mathf.Sin(0.6f), 0f, -Mathf.Cos(0.6f));
            Vector3 expectedVelocity = expectedDirection * 1.5f;

            Assert.True(Mathf.Abs(velocity.X - expectedVelocity.X) <= Tolerance, $"Expected rotated X velocity {expectedVelocity.X:F6}. Got {velocity.X:F6}.");
            Assert.True(Mathf.Abs(velocity.Z - expectedVelocity.Z) <= Tolerance, $"Expected rotated Z velocity {expectedVelocity.Z:F6}. Got {velocity.Z:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies snap-turn cooldown prevents a second immediate heading change.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_SnapTurnCooldown_BlocksImmediateSecondTurn()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Locomotion.TurnMode = TurnMode.Snap;
            rig.Locomotion.SnapTurnAngleDegrees = 45f;
            rig.Locomotion.SnapTurnActivationThreshold = 0.5f;
            rig.Locomotion.SnapTurnCooldownSeconds = 0.25f;
            rig.Locomotion.MovementSpeedMultiplier = 1.5f;
            rig.Locomotion.InputDeadzone = 0.15f;

            rig.Locomotion.SetRotationInput(new Vector2(0.8f, 0f));
            rig.Locomotion.SetMovementInput(new Vector2(0f, 1f));
            rig.Locomotion._PhysicsProcess(0.016d);
            Vector3 firstVelocity = rig.Body.Velocity;

            rig.Locomotion._PhysicsProcess(0.016d);
            Vector3 secondVelocity = rig.Body.Velocity;

            Assert.True(Mathf.Abs(firstVelocity.X - secondVelocity.X) <= Tolerance, $"Expected cooldown to preserve X heading. Got {firstVelocity.X:F6} then {secondVelocity.X:F6}.");
            Assert.True(Mathf.Abs(firstVelocity.Z - secondVelocity.Z) <= Tolerance, $"Expected cooldown to preserve Z heading. Got {firstVelocity.Z:F6} then {secondVelocity.Z:F6}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies sub-deadzone movement input is suppressed before locomotion is applied.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_MovementDeadzone_SuppressesLowMagnitudeInput()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotionTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Locomotion.InputDeadzone = 0.15f;
            rig.Locomotion.SetMovementInput(new Vector2(0.1f, 0.1f));

            rig.Locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected deadzoned movement input to produce zero planar velocity. Got {rig.Body.Velocity}.");
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
        LocomotionTestRig rig = await CreateRigAsync(
            sceneTree,
            useHeadingHarness: true,
            permissionSourceNodes: [movementBlockedSource, rotationBlockedSource]);

        try
        {
            HeadingAwarePlayerLocomotion locomotion = Assert.IsType<HeadingAwarePlayerLocomotion>(rig.Locomotion);
            locomotion.TurnMode = TurnMode.Smooth;
            locomotion.RotationSpeedMultiplier = 2f;
            locomotion.SmoothTurnSensitivity = 3f;
            locomotion.MovementSpeedMultiplier = 1.5f;
            locomotion.SetMovementInput(new Vector2(0f, 1f));
            locomotion.SetRotationInput(new Vector2(-0.5f, 0f));

            Basis initialBasis = locomotion.MovementBasis;
            locomotion._PhysicsProcess(0.2d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected aggregated movement block to suppress velocity. Got {rig.Body.Velocity}.");
            Assert.True(locomotion.MovementBasis.IsEqualApprox(initialBasis), "Expected aggregated rotation block to suppress yaw changes.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pose-driven locomotion permissions block movement outside the allowed standing threshold.
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
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, permissionSourceNodes: [stateMachine]);

        try
        {
            rig.Locomotion.MovementSpeedMultiplier = 1.5f;
            rig.Locomotion.SetMovementInput(new Vector2(0f, 1f));

            rig.Locomotion._PhysicsProcess(0.016d);

            Assert.True(rig.Body.Velocity.IsZeroApprox(), $"Expected blocked standing pose to suppress movement. Got {rig.Body.Velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pose-driven locomotion permissions allow movement when standing is near fully upright.
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
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, permissionSourceNodes: [stateMachine]);

        try
        {
            rig.Locomotion.MovementSpeedMultiplier = 1.5f;
            rig.Locomotion.SetMovementInput(new Vector2(0f, 1f));

            rig.Locomotion._PhysicsProcess(0.016d);

            Vector3 velocity = rig.Body.Velocity;
            Assert.True(Mathf.Abs(velocity.Z + 1.5f) <= Tolerance, $"Expected movement to remain allowed in near-full standing. Got {velocity}.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pose-driven locomotion permissions continue to allow rotation across poses.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerLocomotion_PoseSource_AllowsRotationAcrossPoses()
    {
        SceneTree sceneTree = GetSceneTree();
        PoseStateMachine stateMachine = CreatePoseStateMachine(new KneelingPoseState());
        _ = stateMachine.Tick(new PoseStateContext());
        LocomotionTestRig rig = await CreateRigAsync(sceneTree, useHeadingHarness: true, permissionSourceNodes: [stateMachine]);

        try
        {
            HeadingAwarePlayerLocomotion locomotion = Assert.IsType<HeadingAwarePlayerLocomotion>(rig.Locomotion);
            locomotion.TurnMode = TurnMode.Smooth;
            locomotion.RotationSpeedMultiplier = 2f;
            locomotion.SmoothTurnSensitivity = 3f;
            locomotion.SetRotationInput(new Vector2(-0.5f, 0f));

            Basis initialBasis = locomotion.MovementBasis;
            locomotion._PhysicsProcess(0.2d);

            Assert.False(locomotion.MovementBasis.IsEqualApprox(initialBasis), "Expected rotation to remain allowed while kneeling.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    private static async Task<LocomotionTestRig> CreateRigAsync(
        SceneTree sceneTree,
        bool useHeadingHarness = false,
        Node[]? permissionSourceNodes = null)
    {
        Node3D root = new()
        {
            Name = "PlayerLocomotionTestRoot",
        };

        CharacterBody3D body = new()
        {
            Name = "Body",
        };

        AnimationTree animationTree = new()
        {
            Name = "AnimationTree",
        };

        Node3D rootMotionReference = new()
        {
            Name = "RootMotionReference",
        };

        PlayerLocomotion locomotion = useHeadingHarness
            ? new HeadingAwarePlayerLocomotion()
            : new PlayerLocomotion();

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

        return new LocomotionTestRig(root, body, locomotion);
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

    private sealed partial class HeadingAwarePlayerLocomotion : PlayerLocomotion
    {
        private Basis _movementBasis = Basis.Identity;

        public Basis MovementBasis => _movementBasis;

        protected override Basis GetMovementBasis() => _movementBasis;

        protected override void ApplyYawRotation(float yawDelta)
            => _movementBasis = _movementBasis.Rotated(Vector3.Up, yawDelta);
    }

    private sealed partial class StubPermissionSource(LocomotionPermissions permissions) : Node, ILocomotionPermissionSource
    {
        public LocomotionPermissions LocomotionPermissions => permissions;
    }

    private sealed record LocomotionTestRig(Node3D Root, CharacterBody3D Body, PlayerLocomotion Locomotion);
}
