using AlleyCat.IK;
using AlleyCat.IntegrationTests.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Player IK must complete its runtime setup regardless of whether XR binding happens before or after its first physics
/// tick (IK implementation notes, Startup Binding Contract: binding remains correct when startup order varies).
/// Uses the arm-only rest-pose player rig of the optical-grab facts, where binding before the first tick left the
/// hand frozen at its rest pose.
/// </summary>
public sealed class PlayerVRIKBindingOrderIntegrationTests
{
    private const string FixtureScenePath = "res://assets/testing/optical_grab/optical_grab_fixture.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";

    private static readonly string[] _stageNames =
    [
        "CharacterIKBeginStage",
        "CharacterIKFootProviderStage",
        "CharacterIKHandAuthorityStage",
        "CharacterIKEndStage",
    ];

    /// <summary>
    /// Observation window after binding: one second at the 60 Hz physics rate. A correctly initialised rig has
    /// already brought the hand from its rest pose onto the authored wrist within about 47 ticks of binding in the
    /// optical-grab fixture, and the window stays shorter than that fixture's 90-tick pending non-convergence bound.
    /// </summary>
    private const int ObservationPhysicsTicks = 60;

    /// <summary>
    /// Authored, reachable right controller wrist of the optical-grab rest-pose layout (shoulder about 0.45 m away).
    /// </summary>
    private static readonly Vector3 _rightWrist = new Vector3(0.25f, 1.08f, -0.43f) + OpticalGrabRestPoseRig.LayoutOffset;

    private static readonly Vector3 _leftWrist = new Vector3(-0.25f, 1.08f, -0.43f) + OpticalGrabRestPoseRig.LayoutOffset;

    /// <summary>
    /// A tracking hand reaches the authored reachable wrist; the failed setup leaves it at the rest pose about 0.5 m
    /// away, so one centimetre separates the outcomes with ample margin for the arm solver's residual.
    /// </summary>
    private const float HandTrackingToleranceMetres = 0.01f;

    /// <summary>Binding in the same frame as the rig enters the tree, before any physics tick.</summary>
    [Headless]
    [Fact]
    public async Task BindBeforeFirstPhysicsTick_InstallsStagesAndTracksHand()
        => await AssertBoundRigTracksAsync(bindBeforeFirstPhysicsTick: true);

    /// <summary>Control: the ordinary order, binding after the rig's first physics tick.</summary>
    [Headless]
    [Fact]
    public async Task BindAfterFirstPhysicsTick_InstallsStagesAndTracksHand()
        => await AssertBoundRigTracksAsync(bindBeforeFirstPhysicsTick: false);

    private static async Task AssertBoundRigTracksAsync(bool bindBeforeFirstPhysicsTick)
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);

        using TestGame root = new()
        {
            Name = "PlayerVRIKBindingOrderFixture"
        };
        TestXRManager xrManager = new()
        {
            Name = "XR"
        };
        root.AddChild(xrManager);
        Node3D originHolder = new()
        {
            Name = "OriginHolder"
        };
        root.AddChild(originHolder);
        MockXRRuntimeNode runtime = LoadPackedScene(MockRuntimeScenePath).Instantiate<MockXRRuntimeNode>();
        originHolder.AddChild(runtime);
        _ = runtime.Initialise(new SubViewport(), maximumRefreshRate: 90);
        xrManager.SetRuntime(runtime);

        Node fixtureScene = LoadPackedScene(FixtureScenePath).Instantiate();
        root.AddChild(fixtureScene);
        Node playerRoot = fixtureScene.GetNode("Subject/Player");
        OpticalGrabRestPoseRig.Prepare(playerRoot);
        EnsureCharacterRuntimeInstalled(playerRoot);

        sceneTree.Root.AddChild(root);
        try
        {
            ulong enteredTreeTick = Engine.GetPhysicsFrames();
            if (!bindBeforeFirstPhysicsTick)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 2);
                await WaitForNextFrameAsync(sceneTree);
            }

            if (runtime.GetNodeOrNull<Camera3D>("MainCamera") is { } mainCamera)
            {
                mainCamera.GlobalTransform = OpticalGrabRestPoseRig.RestViewpoint(playerRoot);
            }

            var rightWrist = new Transform3D(Basis.Identity, _rightWrist);
            runtime.RightHandController.HandPositionNode.GlobalTransform = rightWrist;
            runtime.LeftHandController.HandPositionNode.GlobalTransform = new Transform3D(Basis.Identity, _leftWrist);
            playerRoot.GetNode<AnimationTree>("AnimationTree").Active = true;

            PlayerVRIK vrik = playerRoot.GetNode<PlayerVRIK>("VRIK");
            if (bindBeforeFirstPhysicsTick)
            {
                Assert.Equal(enteredTreeTick, Engine.GetPhysicsFrames());
            }
            else
            {
                Assert.True(Engine.GetPhysicsFrames() > enteredTreeTick, "The control must bind after a physics tick.");
            }

            Assert.True(vrik.BindToXRServices(), "Expected the player VRIK to bind to the mock runtime.");

            // Hold the authored wrist against the XR-origin swing through the observation window.
            for (int tick = 0; tick < ObservationPhysicsTicks; tick++)
            {
                runtime.RightHandController.HandPositionNode.GlobalTransform = rightWrist;
                await WaitForNextPhysicsFrameAsync(sceneTree);
            }

            await WaitForNextFrameAsync(sceneTree);

            Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            string[] missingStages = [.. _stageNames.Where(name => skeleton.GetNodeOrNull(name) is null)];
            Assert.True(
                missingStages.Length == 0,
                $"CharacterIK runtime stages missing after {ObservationPhysicsTicks} physics ticks: "
                + string.Join(", ", missingStages));

            BoneAttachment3D hand = playerRoot.GetNode<HandPoseBehaviour>("Hands/RightHand").HandBoneAttachment
                ?? throw new Xunit.Sdk.XunitException("Expected the right hand bone attachment.");
            float handToWrist = hand.GlobalPosition.DistanceTo(_rightWrist);
            Assert.True(
                handToWrist <= HandTrackingToleranceMetres,
                $"The right hand must track the authored controller wrist within {HandTrackingToleranceMetres} m after "
                + $"{ObservationPhysicsTicks} physics ticks; hand {hand.GlobalPosition}, wrist {_rightWrist}, "
                + $"distance {handToWrist:F4} m.");
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void SetRuntime(IXRRuntime runtime) => Runtime = runtime;
    }

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
