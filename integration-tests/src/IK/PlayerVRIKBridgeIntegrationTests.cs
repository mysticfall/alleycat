using System.Reflection;
using AlleyCat.IK;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for player XR↔IK bridge runtime behaviour.
/// </summary>
public sealed partial class PlayerVRIKBridgeIntegrationTests
{
    /// <summary>
    /// Verifies startup binder does not attempt VRIK binding after XR initialisation failure.
    /// </summary>
    [Fact]
    public async Task StartupBinder_WhenXRInitialisationFails_DoesNotBindPlayerVRIK()
    {
        SceneTree sceneTree = GetSceneTree();

        Node root = new()
        {
            Name = "BinderFailureFixture",
        };

        TestXRManager xrManager = new()
        {
            Name = "XR",
        };

        TestPlayerVRIKStartupBinder binder = new()
        {
            Name = "PlayerVRIKStartupBinder",
        };

        Node3D playerNode = new()
        {
            Name = "Player",
        };

        TestPlayerVRIK playerVRIK = new()
        {
            Name = "VRIK",
        };
        binder.PlayerVRIKToBind = playerVRIK;

        root.AddChild(xrManager);
        root.AddChild(binder);
        root.AddChild(playerNode);
        playerNode.AddChild(playerVRIK);

        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            binder.XRManagerPath = xrManager.GetPath();
            if (!binder.ReadyCalled)
            {
                binder._Ready();
            }

            xrManager.EmitInitialisedResult(succeeded: false);
            binder._Process(1.0d / 60.0d);
            await WaitForFramesAsync(sceneTree, 4);

            Assert.Equal(0, playerVRIK.BindCallCount);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies startup binder still binds when XR initialised successfully before binder subscription.
    /// </summary>
    [Fact]
    public async Task StartupBinder_WhenXRAlreadyInitialisedBeforeSubscription_BindsPlayerVRIK()
    {
        SceneTree sceneTree = GetSceneTree();

        Node root = new()
        {
            Name = "BinderLateSubscriptionFixture",
        };

        TestXRManager xrManager = new()
        {
            Name = "XR",
        };

        TestPlayerVRIKStartupBinder binder = new()
        {
            Name = "PlayerVRIKStartupBinder",
        };

        Node3D playerNode = new()
        {
            Name = "Player",
        };

        TestPlayerVRIK playerVRIK = new()
        {
            Name = "VRIK",
        };
        binder.PlayerVRIKToBind = playerVRIK;

        root.AddChild(xrManager);
        root.AddChild(playerNode);
        playerNode.AddChild(playerVRIK);

        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            xrManager.EmitInitialisedResult(succeeded: true);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.True(xrManager.InitialisationAttempted);
            Assert.True(xrManager.InitialisationSucceeded);
            binder.XRManagerPath = xrManager.GetPath();
            root.AddChild(binder);
            await WaitForNextFrameAsync(sceneTree);
            if (!binder.ReadyCalled)
            {
                binder._Ready();
            }

            binder._Process(1.0d / 60.0d);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.True(binder.ReadyCalled);
            Assert.Equal(1, playerVRIK.BindCallCount);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies calibration guard preserves existing scale when XR camera height is near zero.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKCalibration_WhenXRHeightIsNearZero_PreservesWorldScale()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 0.0f, initialWorldScale: 2.5f);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);
            Assert.True(float.IsFinite(fixture.Origin.WorldScale));
            Assert.Equal(2.5f, fixture.Origin.WorldScale);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies end-stage origin compensation still aligns the physical and virtual head poses
    /// when the virtual head pose has diverged from the physical camera.
    /// </summary>
    [Fact]
    public async Task ComputeCompensatedOriginTransform_WhenVirtualHeadDiffers_RealignsPhysicalHeadPose()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);

            fixture.Origin.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.3f, 0.2f, -0.4f));
            fixture.Skeleton.SetBonePosePosition(fixture.HeadBoneIndex, new Vector3(0.05f, -0.35f, 0.08f));

            Transform3D compensatedOrigin = fixture.PlayerVRIK.ComputeCompensatedOriginTransform(
                fixture.Skeleton,
                fixture.Camera.CameraNode,
                fixture.Origin);

            Transform3D physicalHeadPose = fixture.Camera.CameraNode.GlobalTransform * fixture.PlayerVRIK.Viewpoint!.Transform.Inverse();
            Transform3D localPose = fixture.Origin.GlobalTransform.Inverse() * physicalHeadPose;
            Transform3D virtualHeadPose = fixture.Skeleton.GlobalTransform * fixture.Skeleton.GetBoneGlobalPose(fixture.HeadBoneIndex);
            Transform3D recomposedHeadPose = compensatedOrigin * localPose;

            AssertTransformApproximately(virtualHeadPose, recomposedHeadPose);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies inactive begin-stage processing clears any stale limited head solve target.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKBeginStage_WhenInactive_ResetsHeadSolveTargetToHeadTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            bool bound = fixture.PlayerVRIK.TryBind(
                fixture.Origin,
                fixture.Camera,
                fixture.RightHandController,
                fixture.LeftHandController);

            Assert.True(bound);

            fixture.HeadIKTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.4f, 1.2f, -0.3f));
            fixture.HeadIKSolveTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(-1.5f, 0.1f, 2.2f));
            fixture.PlayerVRIK.Active = false;

            InvokeOnBeginStage(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, fixture.HeadIKSolveTarget.GlobalTransform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies unbound begin-stage processing also clears any stale limited head solve target.
    /// </summary>
    [Fact]
    public async Task PlayerVRIKBeginStage_WhenUnbound_ResetsHeadSolveTargetToHeadTarget()
    {
        SceneTree sceneTree = GetSceneTree();
        VrikFixture fixture = await CreateVrikFixtureAsync(sceneTree, xrCameraHeight: 1.6f, initialWorldScale: 1.0f);

        try
        {
            fixture.HeadIKTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(-0.6f, 1.4f, 0.25f));
            fixture.HeadIKSolveTarget.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(2.0f, -0.3f, 1.1f));

            InvokeOnBeginStage(fixture.PlayerVRIK, 1.0d / 60.0d);

            AssertTransformApproximately(fixture.HeadIKTarget.GlobalTransform, fixture.HeadIKSolveTarget.GlobalTransform);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static async Task<VrikFixture> CreateVrikFixtureAsync(SceneTree sceneTree, float xrCameraHeight, float initialWorldScale)
    {
        Node3D root = new()
        {
            Name = "PlayerVrikFixture",
        };

        Node3D player = new()
        {
            Name = "Player",
        };
        root.AddChild(player);

        Node3D femaleExport = new()
        {
            Name = "Female_export",
        };
        player.AddChild(femaleExport);

        Skeleton3D skeleton = new()
        {
            Name = "GeneralSkeleton",
        };
        femaleExport.AddChild(skeleton);

        int headBoneIndex = skeleton.AddBone("Head");
        skeleton.SetBoneRest(headBoneIndex, new Transform3D(Basis.Identity, new Vector3(0.0f, 1.55f, 0.0f)));
        skeleton.SetBonePosePosition(headBoneIndex, new Vector3(0.0f, -0.15f, 0.0f));

        Node3D headAttachment = new()
        {
            Name = "Head",
        };
        skeleton.AddChild(headAttachment);

        Marker3D viewpoint = new()
        {
            Name = "Viewpoint",
            Transform = new Transform3D(Basis.Identity, new Vector3(0.0f, 0.1f, 0.0f)),
        };
        headAttachment.AddChild(viewpoint);

        Node3D ikTargets = new()
        {
            Name = "IKTargets",
        };
        player.AddChild(ikTargets);

        CharacterBody3D headIKTarget = new()
        {
            Name = "Head",
        };
        ikTargets.AddChild(headIKTarget);

        Node3D headIKSolveTarget = new()
        {
            Name = "HeadSolve",
        };
        ikTargets.AddChild(headIKSolveTarget);

        CharacterBody3D rightHandIKTarget = new()
        {
            Name = "RightHand",
        };
        ikTargets.AddChild(rightHandIKTarget);

        CharacterBody3D leftHandIKTarget = new()
        {
            Name = "LeftHand",
        };
        ikTargets.AddChild(leftHandIKTarget);

        PlayerVRIK playerVRIK = new()
        {
            Name = "VRIK",
            Viewpoint = viewpoint,
            HeadIKTarget = headIKTarget,
            HeadIKSolveTarget = headIKSolveTarget,
            RightHandIKTarget = rightHandIKTarget,
            LeftHandIKTarget = leftHandIKTarget,
            Skeleton = skeleton,
        };
        player.AddChild(playerVRIK);

        TestXROrigin origin = new(initialWorldScale)
        {
            Name = "Origin",
        };
        root.AddChild(origin);

        Camera3D cameraNode = new()
        {
            Name = "MainCamera",
            Position = new Vector3(0.0f, xrCameraHeight, 0.0f),
        };
        origin.AddChild(cameraNode);

        Node3D rightControllerNode = new()
        {
            Name = "RightController",
        };
        origin.AddChild(rightControllerNode);

        Node3D rightHandPosition = new()
        {
            Name = "RightHandPosition",
        };
        rightControllerNode.AddChild(rightHandPosition);

        Node3D leftControllerNode = new()
        {
            Name = "LeftController",
        };
        origin.AddChild(leftControllerNode);

        Node3D leftHandPosition = new()
        {
            Name = "LeftHandPosition",
        };
        leftControllerNode.AddChild(leftHandPosition);

        TestXRCamera camera = new(cameraNode);
        TestXRHandController rightHandController = new(rightControllerNode, rightHandPosition);
        TestXRHandController leftHandController = new(leftControllerNode, leftHandPosition);

        sceneTree.Root.AddChild(root);
        await WaitForFramesAsync(sceneTree, 2);

        return new VrikFixture(
            root,
            playerVRIK,
            skeleton,
            headBoneIndex,
            headIKTarget,
            headIKSolveTarget,
            origin,
            camera,
            rightHandController,
            leftHandController);
    }

    private static void InvokeOnBeginStage(PlayerVRIK playerVRIK, double delta)
    {
        MethodInfo method = typeof(PlayerVRIK).GetMethod(
                                "OnBeginStage",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("PlayerVRIK.OnBeginStage was not found.");

        _ = method.Invoke(playerVRIK, [delta]);
    }

    private sealed class VrikFixture(
        Node3D root,
        PlayerVRIK playerVRIK,
        Skeleton3D skeleton,
        int headBoneIndex,
        CharacterBody3D headIKTarget,
        Node3D headIKSolveTarget,
        TestXROrigin origin,
        TestXRCamera camera,
        TestXRHandController rightHandController,
        TestXRHandController leftHandController)
    {
        public Node3D Root { get; } = root;

        public PlayerVRIK PlayerVRIK { get; } = playerVRIK;

        public Skeleton3D Skeleton { get; } = skeleton;

        public int HeadBoneIndex { get; } = headBoneIndex;

        public CharacterBody3D HeadIKTarget { get; } = headIKTarget;

        public Node3D HeadIKSolveTarget { get; } = headIKSolveTarget;

        public TestXROrigin Origin { get; } = origin;

        public TestXRCamera Camera { get; } = camera;

        public TestXRHandController RightHandController { get; } = rightHandController;

        public TestXRHandController LeftHandController { get; } = leftHandController;

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual, float epsilon = 1e-4f)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin, epsilon);
        AssertBasisApproximately(expected.Basis, actual.Basis, epsilon);
    }

    private static void AssertBasisApproximately(Basis expected, Basis actual, float epsilon)
    {
        AssertVectorApproximately(expected.X, actual.X, epsilon);
        AssertVectorApproximately(expected.Y, actual.Y, epsilon);
        AssertVectorApproximately(expected.Z, actual.Z, epsilon);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual, float epsilon)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void EmitInitialisedResult(bool succeeded)
        {
            InitialisationAttempted = true;
            InitialisationSucceeded = succeeded;
            _ = EmitSignal("Initialised", succeeded);
        }
    }

    private sealed partial class TestPlayerVRIKStartupBinder : PlayerVRIKStartupBinder
    {
        public bool ReadyCalled
        {
            get;
            private set;
        }

        public PlayerVRIK? PlayerVRIKToBind
        {
            get;
            set;
        }

        public override void _Ready()
        {
            ReadyCalled = true;
            base._Ready();
        }

        protected override PlayerVRIK? ResolvePlayerVRIK()
            => PlayerVRIKToBind;
    }

    private sealed partial class TestPlayerVRIK : PlayerVRIK
    {
        public int BindCallCount
        {
            get;
            private set;
        }

        public override void _Ready()
        {
        }

        public override bool TryBind(IXRRuntime runtime)
        {
            BindCallCount++;
            return true;
        }
    }

    private sealed partial class TestXROrigin(float worldScale) : Node3D, IXROrigin
    {
        public Node3D OriginNode => this;

        public float WorldScale
        {
            get;
            set;
        } = worldScale;
    }

    private sealed class TestXRCamera(Camera3D cameraNode) : IXRCamera
    {
        public Camera3D CameraNode => cameraNode;
    }

    private sealed class TestXRHandController(Node3D controllerNode, Node3D handPositionNode) : IXRHandController
    {
#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

        public event Action<string, float>? ActionFloatInputChanged;

        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => controllerNode;

        public Node3D HandPositionNode => handPositionNode;
    }
}
