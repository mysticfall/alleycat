using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Normal player composition with synthetic tracking, not a headset comfort verdict.</summary>
public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Generated player body posing must not overwrite controller-grab finger animation.</summary>
    [Headless]
    [Fact]
    public async Task PlayerRetargeting_PreservesExistingFingerPose()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        await TestUtils.WaitForNextFrameAsync(tree);
        Node model = GD.Load<PackedScene>("res://assets/characters/reference/female/reference_female.blend").Instantiate();
        tree.Root.AddChild(model);
        try
        {
            Skeleton3D skeleton = model.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().Single();
            var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
            var rig = new CalibratedSomaRig(data, skeleton, preserveFingerPoses: true);
            int finger = skeleton.FindBone("LeftIndexProximal");
            var authored = new Quaternion(Vector3.Right, 0.4f);
            skeleton.SetBonePoseRotation(finger, authored);
            rig.Apply(data.Idle[0]);
            Assert.Equal(authored, skeleton.GetBonePoseRotation(finger));
        }
        finally
        {
            model.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }

    /// <summary>Current stick ownership, independent of native timing and visual motion quality.</summary>
    [Headless]
    [Fact]
    public void StickOwnership_UsesMagnitudeMovementPriorityAndImmediateRelease()
    {
        using var motion = new PlayerMotionBricks();
        Assert.Equal("res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx", motion.ModelPath);
        motion.SetStick(false, new Vector2(0, 1));
        Assert.Equal(0, motion.TurnIntent);
        motion.SetStick(false, new Vector2(0.6f, 1));
        Assert.Equal(0.6f, motion.TurnIntent);
        motion.SetStick(true, new Vector2(0, 0.4f));
        Assert.Equal(new Vector2(0, 0.4f), motion.MovementIntent);
        Assert.Equal(0.6f, motion.TurnIntent); // Right input steers the active travel intent.
        Assert.Equal("forward", ReviewedRoleSampler.Classify(motion.MovementIntent, motion.TurnIntent, motion.Deadzone));
        motion.SetStick(true, new Vector2(0, 0.9f));
        Assert.True(new Vector2(0, 0.9f).IsEqualApprox(motion.MovementIntent));
        Assert.Equal(0.6f, motion.TurnIntent);
        motion.SetStick(false, Vector2.Zero);
        Assert.True(new Vector2(0, 0.9f).IsEqualApprox(motion.MovementIntent));
        Assert.Equal(0, motion.TurnIntent); // Releasing steering does not release travel.
        motion.SetStick(false, new Vector2(-0.6f, 0));
        Assert.Equal("forward", ReviewedRoleSampler.Classify(motion.MovementIntent, motion.TurnIntent, motion.Deadzone));
        motion.SetStick(true, Vector2.Zero);
        Assert.Equal(Vector2.Zero, motion.MovementIntent);
        Assert.Equal(-0.6f, motion.TurnIntent);
        Assert.Equal("left_turn", ReviewedRoleSampler.Classify(motion.MovementIntent, motion.TurnIntent, motion.Deadzone));
        motion.ClearInput();
        Assert.Equal(Vector2.Zero, motion.MovementIntent);
        Assert.Equal(0, motion.TurnIntent);
        Assert.Equal("settling-requested", motion.Phase);
        Assert.Null(motion.WorkerCompletion);
        Assert.Equal(0, motion.Requests); // Unattached node: no Ready/worker or native construction.
    }

    /// <summary>Production controller-to-model-to-solved-pose turn, release and movement observation.</summary>
    [Fact]
    public async Task ProductionSticks_GeneratePlayerMotionFromRealisedTracking()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        await TestUtils.WaitForNextFrameAsync(tree);
        var root = new ProbeGame { Name = "PlayerGenerationProbe" };
        var manager = new ProbeXRManager { Name = "XR" };
        MockXRRuntimeNode runtime = GD.Load<PackedScene>("res://assets/xr/mock_runtime.tscn").Instantiate<MockXRRuntimeNode>();
        Node3D player = GD.Load<PackedScene>("res://assets/characters/reference/ally_player.tscn").Instantiate<Node3D>();
        root.AddChild(manager);
        root.AddChild(runtime);
        root.AddChild(player);
        root.AddChild(GD.Load<PackedScene>("res://assets/testing/test_environment.tscn").Instantiate());
        using var viewport = new SubViewport();
        Assert.True(runtime.Initialise(viewport, 90));
        manager.SetRuntime(runtime);
        tree.Root.AddChild(root);
        string directory = ProjectSettings.GlobalizePath("res://temp/motionbricks/player-slice-03/" +
            (System.Environment.GetEnvironmentVariable("PLAYER_PROBE_LABEL") ?? "generated"));
        _ = Directory.CreateDirectory(directory);
        var rows = new List<object>();
        var windows = new List<object>();
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 20);
            PlayerVRIK vrik = player.GetNode<PlayerVRIK>("VRIK");
            Assert.True(vrik.BindToXRServices());
            Camera3D head = runtime.Camera.CameraNode;
            head.Transform = new Transform3D(Basis.Identity, new Vector3(0, 1.4f, 0));
            runtime.LeftHandController.HandPositionNode.Position = new Vector3(-0.3f, 1.2f, -0.2f);
            runtime.RightHandController.HandPositionNode.Position = new Vector3(0.3f, 1.2f, -0.2f);
            _ = manager.EmitSignal(XRManager.SignalName.Initialised, true);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 10);
            await WaitPresentedFrameAsync();
            PlayerMotionBricks motion = player.GetNode<PlayerMotionBricks>("MotionBricks");
            Assert.Same(motion, player.GetNode<PlayerController>("PlayerController").Motion);
            Assert.Equal("", motion.Failure);
            Skeleton3D skeleton = player.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            Assert.Equal("PlayerMotionStage", skeleton.GetChild(0).Name.ToString());
            double clock = 0;
            string phase = "idle";
            bool anatomyValid = true;
            motion.WindowSubmitted += (ticket, context, target) => windows.Add(new
            {
                clock,
                phase,
                ticket,
                samples = motion.SamplesRecorded,
                contextRoot = Position(context[3][0].Origin),
                targetRoot = Position(target[0][0].Origin)
            });
            void Observe()
            {
                Vector3 Joint(string name)
                {
                    return skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone(name)).Origin;
                }

                // Independent gross leg sanity: neither knee may fold above the hips or below its ankle.
                anatomyValid &= Joint("LeftLowerLeg").Y > Joint("LeftFoot").Y && Joint("LeftLowerLeg").Y < Joint("Hips").Y
                    && Joint("RightLowerLeg").Y > Joint("RightFoot").Y && Joint("RightLowerLeg").Y < Joint("Hips").Y;

                rows.Add(new
                {
                    clock,
                    phase,
                    state = motion.Phase,
                    motion.Requests,
                    motion.SamplesRecorded,
                    motion.DiscardedResults,
                    motion.LastResponseSeconds,
                    motion.Failure,
                    actor = Position(player.GlobalPosition),
                    actorYaw = player.GlobalRotation.Y,
                    cameraYaw = head.GlobalRotation.Y,
                    vrik.HeadYawLimited,
                    vrik.XRCompensationApplied,
                    vrik.HeadPositionLimited,
                    vrik.HeadTargetReachable,
                    hips = Position(Joint("Hips")),
                    head = Position(Joint("Head")),
                    leftFoot = Position(Joint("LeftFoot")),
                    rightFoot = Position(Joint("RightFoot")),
                    leftKnee = Position(Joint("LeftLowerLeg")),
                    rightKnee = Position(Joint("RightLowerLeg")),
                    leftHand = Position(Joint("LeftHand")),
                    rightHand = Position(Joint("RightHand"))
                });
            }
            skeleton.SkeletonUpdated += Observe;
            var left = (MockXRHandControllerNode)runtime.LeftHandController;
            var right = (MockXRHandControllerNode)runtime.RightHandController;
            // Time windows deliberately include model load, active playback and post-release settling.
            for (int tick = 0; tick < 2400; tick++)
            {
                clock = tick / 60.0;
                if (tick == 60)
                {
                    phase = "right-turn";
                    right.TriggerActionVector2InputChanged("primary", new Vector2(0.6f, 0));
                }
                if (tick == 480)
                {
                    phase = "turn-release";
                    right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                }
                if (tick == 780)
                {
                    phase = "left-turn";
                    right.TriggerActionVector2InputChanged("primary", new Vector2(-0.6f, 0));
                }
                if (tick == 1080)
                {
                    phase = "slow-movement-priority";
                    left.TriggerActionVector2InputChanged("primary", new Vector2(0, 0.4f));
                }
                if (tick == 1380)
                {
                    phase = "fast-movement";
                    left.TriggerActionVector2InputChanged("primary", new Vector2(0, 0.9f));
                }
                if (tick == 1680)
                {
                    phase = "direction-change";
                    left.TriggerActionVector2InputChanged("primary", new Vector2(0.7f, 0));
                }
                if (tick == 1860)
                {
                    phase = "movement-release";
                    right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                    left.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                }
                head.Rotation = new Vector3(0, Mathf.DegToRad(10) * Mathf.Sin((float)clock), 0);
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                if (tick % 120 == 0)
                {
                    await WaitPresentedFrameAsync();
                }
                if (motion.Failure.Length > 0)
                {
                    break;
                }
            }
            skeleton.SkeletonUpdated -= Observe;
            File.WriteAllText(Path.Combine(directory, "motion.json"), JsonSerializer.Serialize(rows));
            File.WriteAllText(Path.Combine(directory, "windows.json"), JsonSerializer.Serialize(windows));
            Assert.Equal("", motion.Failure);
            Assert.True(motion.Requests > 2, "Production stick events must submit generated windows.");
            Assert.True(anatomyValid, "A knee folded above the hips or below its ankle during the complete motion window.");
            Assert.True(motion.SamplesRecorded > 480, "Normal solved-skeleton observations must continue through idle and movement.");
            Task? stopped = motion.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (stopped is not null)
            {
                await stopped.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            if (GodotObject.IsInstanceValid(root))
            {
                root.QueueFree();
            }
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
        // Former capture points keep their presented-frame wait so the observation timing is unchanged.
        async Task WaitPresentedFrameAsync()
        {
            _ = await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }

    /// <summary>Eight-second diagnostic window includes input transitions and four seconds of settling.</summary>
    [Fact]
    public async Task BodyYaw_ObservesTrackingAcrossIdleTurnHeadMotionAndSettling()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        await TestUtils.WaitForNextFrameAsync(tree);
        var root = new ProbeGame { Name = "PlayerMotionProbe" };
        var manager = new ProbeXRManager { Name = "XR" };
        MockXRRuntimeNode runtime = GD.Load<PackedScene>("res://assets/xr/mock_runtime.tscn").Instantiate<MockXRRuntimeNode>();
        Node3D player = GD.Load<PackedScene>("res://assets/characters/reference/ally_player.tscn").Instantiate<Node3D>();
        root.AddChild(manager);
        root.AddChild(runtime);
        root.AddChild(player);
        using var viewport = new SubViewport();
        Assert.True(runtime.Initialise(viewport, 90));
        manager.SetRuntime(runtime);
        tree.Root.AddChild(root);
        var rows = new List<object>();
        string directory = ProjectSettings.GlobalizePath("res://temp/motionbricks/player-slice-03/" +
            (System.Environment.GetEnvironmentVariable("PLAYER_PROBE_LABEL") ?? "candidate"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 20);
            PlayerVRIK vrik = player.GetNode<PlayerVRIK>("VRIK");
            Assert.True(vrik.BindToXRServices());
            Skeleton3D skeleton = player.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Physics;
            Camera3D head = runtime.Camera.CameraNode;
            head.Transform = new Transform3D(Basis.Identity, new Vector3(0, 1.6f, 0));
            runtime.LeftHandController.HandPositionNode.Position = new Vector3(-0.3f, 1.2f, -0.2f);
            runtime.RightHandController.HandPositionNode.Position = new Vector3(0.3f, 1.2f, -0.2f);
            double clock = 0;
            string phase = "idle";
            void Observe()
            {
                rows.Add(new
                {
                    clock,
                    phase,
                    actorYaw = player.GlobalRotation.Y,
                    cameraYaw = head.GlobalRotation.Y,
                    physicalYaw = head.Rotation.Y,
                    head = Position(skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("Head")).Origin),
                    left = Position(skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftHand")).Origin),
                    right = Position(skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("RightHand")).Origin)
                });
            }
            skeleton.SkeletonUpdated += Observe;
            for (int tick = 0; tick < 480; tick++)
            {
                clock = tick / 60.0;
                phase = clock < 1 ? "idle" : clock < 3 ? "body-yaw" : clock < 4 ? "head-motion" : "settled";
                float yaw = Mathf.DegToRad(45) * (float)Math.Clamp((clock - 1) / 2, 0, 1);
                player.Rotation = new Vector3(0, yaw, 0);
                head.Rotation = new Vector3(0, Mathf.DegToRad(20) * (float)Math.Clamp(clock - 3, 0, 1), 0);
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            skeleton.SkeletonUpdated -= Observe;
            File.WriteAllText(Path.Combine(directory, "tracking.json"), JsonSerializer.Serialize(rows));
            // A 45-degree actor rotation must not be simply added to the physical head's 20 degrees.
            // This distinguishes full view carry, not a new comfort or solver-quality bound.
            Assert.True(Mathf.Abs(Mathf.Wrap(head.GlobalRotation.Y - head.Rotation.Y, -Mathf.Pi, Mathf.Pi)) <
                Mathf.Abs(player.GlobalRotation.Y) / 2, "Body yaw still carries the tracked view; inspect the complete trace.");

            // Additional constraint probe is recorded separately; the preceding eight-second regression is unchanged.
            Assert.Equal(100, vrik.HeadLeftYawLimitDegrees);
            Assert.Equal(100, vrik.HeadRightYawLimitDegrees);
            player.Rotation = Vector3.Zero;
            var constraints = new List<object>();
            foreach (float angle in new[] { 0f, 130f, -130f, 0f })
            {
                head.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(angle)), new Vector3(0, 1.4f, 0));
                for (int tick = 0; tick < 120; tick++)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                    constraints.Add(new
                    {
                        angle,
                        tick,
                        vrik.HeadYawLimited,
                        vrik.HeadPositionLimited,
                        vrik.HeadTargetReachable,
                        vrik.XRCompensationApplied,
                        cameraYaw = head.GlobalRotation.Y
                    });
                }
                Assert.Equal(angle != 0, vrik.HeadYawLimited);
                Assert.Equal(angle != 0, vrik.XRCompensationApplied);
                if (angle == 0)
                {
                    Assert.True(vrik.HeadTargetReachable);
                }
            }
            File.WriteAllText(Path.Combine(directory, "constraints.json"), JsonSerializer.Serialize(constraints));
            vrik.HeadLeftYawLimitDegrees = 80;
            vrik.HeadRightYawLimitDegrees = 110;
            head.Rotation = new Vector3(0, Mathf.DegToRad(90), 0);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 60);
            Assert.True(vrik.HeadYawLimited);
            head.Rotation = new Vector3(0, Mathf.DegToRad(-90), 0);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 60);
            Assert.False(vrik.HeadYawLimited);
            vrik.HeadLeftYawLimitDegrees = vrik.HeadRightYawLimitDegrees = 100;

        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }

    private static float[] Position(Vector3 value) => [value.X, value.Y, value.Z];
    private sealed partial class ProbeGame : Game
    {
        public override void _Ready()
        {
        }
    }
    private sealed partial class ProbeXRManager : XRManager
    {
        public override void _Ready()
        {
        }
        public void SetRuntime(IXRRuntime runtime) => Runtime = runtime;
    }
}
