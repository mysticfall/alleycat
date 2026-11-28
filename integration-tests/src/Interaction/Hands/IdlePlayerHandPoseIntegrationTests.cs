using AlleyCat.Control;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.XR;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Interaction.Hands;

/// <summary>
/// INTR-003 R8 on the production player: a player who has not moved since spawning still presents a requested
/// hand pose on the finger bones. The production player template (its own AnimationTree and MotionBricks
/// wiring) is used unchanged; MotionBricks receives a native-free session before Ready and no stick input.
/// </summary>
public sealed class IdlePlayerHandPoseIntegrationTests
{
    private const string PlayerTemplatePath = "res://assets/characters/reference/ally_player.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string GrabBallAnimationPath = "res://assets/animations/reference/female/Grab-ball-40.tres";

    /// <summary>Existing held-pose agreement bound of the grab/hand-pose facts.</summary>
    private const float AuthoredMatchToleranceRadians = 0.03f;

    /// <summary>
    /// Observation window: the immediate request publishes full weight synchronously, so one evaluation suffices;
    /// 40 physics frames is the established settle window of the player-rig finger facts
    /// (<c>VrikSettlePhysicsFrames</c>), independent of how the tree is evaluated.
    /// </summary>
    private const int ObservationPhysicsFrames = 40;

    /// <summary>
    /// A requested immediate grip appears on the finger bones of an idle, never-moved player within the window.
    /// </summary>
    [Headless]
    [Fact]
    public async Task NeverMovedPlayer_RequestedHandPose_ReachesFingerBones()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);

        TestGame root = new()
        {
            Name = "IdlePlayerHandPoseFixture"
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
        Node player = LoadPackedScene(PlayerTemplatePath).Instantiate();
        root.AddChild(player);

        sceneTree.NodeAdded += BindNativeFreeSession;
        try
        {
            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 2);
        }
        finally
        {
            sceneTree.NodeAdded -= BindNativeFreeSession;
        }

        try
        {
            Assert.True(player.GetNode<PlayerVRIK>("VRIK").BindToXRServices(), "Expected the player VRIK to bind to the mock runtime.");
            Skeleton3D skeleton = player.GetNode<Skeleton3D>("Female/SOMA/Native/GeneralSkeleton");
            var capture = OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture.Attach(skeleton);
            PlayerMotionBricks motion = player.GetNode<PlayerMotionBricks>("MotionBricks");
            HandPoseBehaviour rightHand = player.GetNode<HandPoseBehaviour>("Hands/RightHand");
            Animation grip = ResourceLoader.Load<Animation>(GrabBallAnimationPath);

            await WaitForPhysicsFramesAsync(sceneTree, 4);
            Quaternion[] before = CaptureRightFingers(skeleton, capture);
            rightHand.SetPose(grip, immediate: true);
            await WaitForPhysicsFramesAsync(sceneTree, ObservationPhysicsFrames);

            // Scenario precondition: genuinely idle since spawning — no movement input, no generated window.
            Assert.Equal(0, motion.Requests);
            Assert.Equal("tracked-idle", motion.Phase);
            Assert.True(capture.CaptureCount > 0, "Expected final skeleton poses to be observed.");

            Quaternion[] after = CaptureRightFingers(skeleton, capture);
            Quaternion[] expected = OpticalGrabIntegrationTests.SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath);
            float residual = MaxAngle(after, expected);
            Assert.True(
                residual <= AuthoredMatchToleranceRadians,
                FormattableString.Invariant(
                    $"Idle player right fingers must present the requested grip within {AuthoredMatchToleranceRadians} rad; ") +
                FormattableString.Invariant(
                    $"max residual {residual:R} rad, change since request {MaxAngle(after, before):R} rad, ") +
                FormattableString.Invariant($"first finger now {after[0]} vs authored {expected[0]}."));
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    private static Quaternion[] CaptureRightFingers(
        Skeleton3D skeleton,
        OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture capture)
    {
        var rotations = new Quaternion[OpticalFingerProjectionBinding.FingerBonesPerSide];
        for (int index = 0; index < rotations.Length; index++)
        {
            int bone = skeleton.FindBone("Right" + XRHandJoints.DestinationJoints[index]);
            Assert.True(bone >= 0, $"Expected finger bone Right{XRHandJoints.DestinationJoints[index]}.");
            rotations[index] = capture.Rotations[bone];
        }

        return rotations;
    }

    private static float MaxAngle(Quaternion[] actual, Quaternion[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        float max = 0f;
        for (int index = 0; index < actual.Length; index++)
        {
            Quaternion aligned = FingerRetargetingMath.StabiliseRotationHemisphere(expected[index], actual[index]);
            max = MathF.Max(max, actual[index].AngleTo(aligned));
        }

        return max;
    }

    private static void BindNativeFreeSession(Node node)
    {
        if (node is PlayerMotionBricks motion)
        {
            motion.SessionFactory = () => new NativeFreeSession();
        }
    }

    /// <summary>Native-free endpoint: never invoked without movement input, but keeps the fact CUDA-free.</summary>
    private sealed class NativeFreeSession : AlleyCat.Motion.SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
            => [.. Enumerable.Range(0, 4).Select(_ => (Transform3D[])context[3].Clone())];

        public void Dispose()
        {
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
