using System.Diagnostics;
using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.XR;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>RIG-001 excludes head collision; existing anatomical compensation must work with or without a wall.</summary>
    [Fact]
    public async Task PositionalConstraint_PreservesCompensationWithoutInventingHeadCollision()
    {
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
        var rows = new List<object>();
        string phase = "initial";
        int tick = 0;
        void Observe()
        {
            Vector3 solved = fixture.Skeleton.GlobalTransform * fixture.Skeleton.GetBoneGlobalPose(fixture.Skeleton.FindBone("Head")).Origin;
            rows.Add(new
            {
                phase,
                tick,
                physical = Position(fixture.Head.Position),
                target = Position(fixture.Player.GetNode<Node3D>("IKTargets/Head").GlobalPosition),
                solvedHead = Position(solved),
                view = Position(fixture.Head.GlobalPosition),
                fixture.VRIK.HeadPositionLimited,
                fixture.VRIK.XRCompensationApplied
            });
        }
        fixture.Skeleton.SkeletonUpdated += Observe;
        try
        {
            foreach (bool withWall in new[] { false, true })
            {
                if (withWall)
                {
                    var wall = new StaticBody3D { Position = new Vector3(0.4f, 1, 0) };
                    wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.1f, 3, 3) } });
                    fixture.Root.AddChild(wall);
                }
                phase = withWall ? "wall-reset" : "no-wall-reset";
                fixture.Head.Position = new Vector3(0, 1.4f, 0);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 120);
                Assert.False(fixture.VRIK.HeadPositionLimited);
                Assert.False(fixture.VRIK.XRCompensationApplied);
                for (tick = 0; tick < 240; tick++)
                {
                    phase = (withWall ? "wall-" : "no-wall-") + (tick < 120 ? "translation" : "settled");
                    fixture.Head.Position = new Vector3(0.8f * Math.Min(tick / 120f, 1), 1.4f, 0);
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(fixture.VRIK.HeadPositionLimited);
                Assert.True(fixture.VRIK.XRCompensationApplied);
                Node3D target = fixture.Player.GetNode<Node3D>("IKTargets/Head");
                // The tracked target is not stopped by a wall. The existing anatomical envelope limits the view.
                Assert.True(target.GlobalPosition.X > 0.4f);
                Assert.True(fixture.Head.GlobalPosition.X < target.GlobalPosition.X);
            }
        }
        finally
        {
            fixture.Skeleton.SkeletonUpdated -= Observe;
            SaveClosure("positional-constraint.json", rows);
        }
    }

    /// <summary>A real generated result returns late after production release/replacement and must never play.</summary>
    [Fact]
    public async Task NativeLateResult_ProductionReplacementRejectsOldPlaybackAndSettles()
    {
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
        using var gate = new NativePublicationGate();
        fixture.Motion.SessionDecorator = session => new GatedNativeSession(session, gate);
        var clock = Stopwatch.StartNew();
        var rows = new List<object>();
        var submitted = new List<long>();
        var applied = new HashSet<long>();
        string phase = "initial";
        float heldHeadMin = float.PositiveInfinity, heldHeadMax = float.NegativeInfinity;
        bool heldTrackingMatches = true;
        fixture.Motion.WindowSubmitted += (ticket, _, _) => submitted.Add(ticket);
        void Observe()
        {
            _ = applied.Add(fixture.Motion.LastAppliedRequest);
            if (phase is "release-held-result" or "replace-left-held-result")
            {
                heldHeadMin = Math.Min(heldHeadMin, fixture.Head.GlobalRotation.Y);
                heldHeadMax = Math.Max(heldHeadMax, fixture.Head.GlobalRotation.Y);
                heldTrackingMatches &= Mathf.IsEqualApprox(fixture.Head.Rotation.Y, fixture.Head.GlobalRotation.Y);
            }
            rows.Add(new
            {
                seconds = clock.Elapsed.TotalSeconds,
                phase,
                state = fixture.Motion.Phase,
                fixture.Motion.LastAppliedRequest,
                fixture.Motion.DiscardedResults,
                physicalYaw = fixture.Head.Rotation.Y,
                viewYaw = fixture.Head.GlobalRotation.Y,
                root = Position(fixture.Player.GlobalPosition),
                actorYaw = fixture.Player.GlobalRotation.Y,
                fixture.Motion.SamplesRecorded,
                generated = gate.Generated.IsSet
            });
        }
        fixture.Skeleton.SkeletonUpdated += Observe;
        try
        {
            await Step(60);
            phase = "old-right-request";
            fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(0.6f, 0));
            while (!gate.Generated.IsSet && clock.Elapsed < TimeSpan.FromSeconds(25))
            {
                await Step(1);
            }
            Assert.True(gate.Generated.IsSet, "Real native generation did not reach the publication gate.");
            Assert.True(gate.GeneratedFrames > 4);
            long stale = submitted[0];
            Assert.Equal(0, fixture.Motion.LastAppliedRequest);
            phase = "release-held-result";
            fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
            await Step(2);
            Assert.Equal("inference-hold", fixture.Motion.Phase);
            phase = "replace-left-held-result";
            fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(-0.6f, 0));
            await Step(60);
            long current = submitted[^1];
            Assert.NotEqual(stale, current);
            Assert.Equal(0, fixture.Motion.LastAppliedRequest);
            phase = "late-publication-current-playback";
            Assert.True(heldHeadMin < heldHeadMax, "Physical head movement must remain visible while publication is held.");
            Assert.True(heldTrackingMatches, "The held native result must not freeze or carry the tracked view.");
            gate.Release.Set();
            await Step(120);
            Assert.Equal(1, fixture.Motion.DiscardedResults);
            Assert.Contains(current, applied);
            Assert.DoesNotContain(stale, applied);
            phase = "current-release";
            fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
            await Step(240);
            Assert.Equal("tracked-idle", fixture.Motion.Phase);
            Assert.Equal("", fixture.Motion.Failure);
            Assert.DoesNotContain(stale, applied);
        }
        finally
        {
            gate.Release.Set();
            fixture.Skeleton.SkeletonUpdated -= Observe;
            SaveClosure("late-result.json", rows);
            SaveClosure("late-result-requests.json", new
            {
                submitted,
                applied,
                gate.GeneratedFrames
            });
            // Unblock before teardown, and do not dispose synchronisation objects until the worker has exited.
            await fixture.DisposeAsync();
        }

        async Task Step(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                fixture.Head.Rotation = new Vector3(0, Mathf.DegToRad(10) * Mathf.Sin((float)clock.Elapsed.TotalSeconds), 0);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
        }
    }

    /// <summary>Existing root sphere meets one obstacle; continued generation and feedback remain collision-realised.</summary>
    [Fact]
    public async Task RootObstacle_BlocksGeneratedPlaybackAndFeedsRealisedContext()
    {
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
        CollisionShape3D shape = fixture.Player.GetNode<CollisionShape3D>("CollisionShape3D");
        SphereShape3D sphere = Assert.IsType<SphereShape3D>(shape.Shape);
        var wall = new StaticBody3D { Position = new Vector3(0, 0.25f, -0.6f) };
        var box = new BoxShape3D { Size = new Vector3(8, 0.5f, 0.2f) };
        wall.AddChild(new CollisionShape3D { Shape = box });
        fixture.Root.AddChild(wall);
        float front = wall.Position.Z + (box.Size.Z / 2);
        // Jolt allows body penetration up to this project setting; exact mathematical non-overlap
        // is not its contract. Also guard the near plane geometrically, independent of slop.
        float penetrationAllowance = ProjectSettings.GetSetting("physics/jolt_physics_3d/simulation/penetration_slop").AsSingle();
        var rows = new List<object>();
        var windows = new List<object>();
        var recent = new Queue<Transform3D>();
        int tick = 0, requestsAfterBlocked = 0;
        bool blocked = false, crossed = false, contextMatches = true;
        float initialZ = shape.GlobalPosition.Z, minimumZ = initialZ;
        void Observe()
        {
            recent.Enqueue(fixture.Motion.ObservedRoot);
            while (recent.Count > 8)
            {
                _ = recent.Dequeue();
            }
            minimumZ = Math.Min(minimumZ, shape.GlobalPosition.Z);
            crossed |= shape.GlobalPosition.Z < front || shape.GlobalPosition.Z - sphere.Radius < front - penetrationAllowance;
            // A read-only short forward sweep diagnoses contact; it never moves the player or supplies feedback.
            blocked |= fixture.Player.TestMove(fixture.Player.GlobalTransform, Vector3.Forward * 0.01f);
            rows.Add(new
            {
                tick,
                seconds = tick / 60.0,
                fixture.Motion.Phase,
                root = Position(fixture.Player.GlobalPosition),
                sphereCentre = Position(shape.GlobalPosition),
                sphere.Radius,
                front,
                penetrationAllowance,
                blocked,
                observedRoot = Position(fixture.Motion.ObservedRoot.Origin),
                fixture.Motion.Requests
            });
        }
        fixture.Skeleton.SkeletonUpdated += Observe;
        fixture.Motion.WindowSubmitted += (ticket, context, target) =>
        {
            bool matches = recent.Any(pose => pose.IsEqualApprox(context[3][0]));
            contextMatches &= matches;
            if (blocked)
            {
                requestsAfterBlocked++;
            }
            windows.Add(new
            {
                tick,
                ticket,
                blocked,
                matches,
                root = Position(fixture.Player.GlobalPosition),
                contextRoot = Position(context[3][0].Origin),
                targetRoot = Position(target[0][0].Origin)
            });
        };
        try
        {
            for (tick = 0; tick < 840; tick++)
            {
                if (tick == 60)
                {
                    fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, 1));
                }
                if (tick == 600)
                {
                    fixture.Left.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                }
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.Equal("", fixture.Motion.Failure);
            Assert.True(initialZ - minimumZ > sphere.Radius, "The player must actually move towards the obstacle.");
            Assert.True(blocked, "The existing root sphere must meet the obstacle.");
            Assert.False(crossed, "The root crossed the near plane or exceeded the engine's permitted penetration during playback or settling.");
            Assert.True(requestsAfterBlocked >= 2, "Observe continued playback and subsequent requests after contact.");
            Assert.True(contextMatches, "Submitted context must be a preceding actual skin-ready observation.");
            Assert.Equal("tracked-idle", fixture.Motion.Phase);
        }
        finally
        {
            fixture.Skeleton.SkeletonUpdated -= Observe;
            SaveClosure("root-obstacle.json", rows);
            SaveClosure("root-obstacle-windows.json", windows);
        }
    }

    private static void SaveClosure(string name, object value)
    {
        // Per-run default label: every execution owns a fresh evidence directory and never rewrites
        // historical probe output. An explicit PLAYER_PROBE_LABEL still overrides for curated runs.
        string directory = ProjectSettings.GlobalizePath("res://temp/motionbricks/player-slice-03/" +
            (System.Environment.GetEnvironmentVariable("PLAYER_PROBE_LABEL")
                ?? ("closure-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"))));
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value));
    }

    private sealed class NativePublicationGate : IDisposable
    {
        public ManualResetEventSlim Generated { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int GeneratedFrames;
        public int HoldGeneration { get; init; } = 1;
        public void Dispose()
        {
            Generated.Dispose();
            Release.Dispose();
        }
    }

    private sealed class GatedNativeSession(SomaInferenceWorker.ISession inner, NativePublicationGate gate) : SomaInferenceWorker.ISession
    {
        private int _generated;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
            => Publish(inner.Generate(context, target));
        public Transform3D[][] GeneratePresented(Transform3D[][] context, Transform3D[][] target, Transform3D[] start)
            => Publish(inner.GeneratePresented(context, target, start));
        private Transform3D[][] Publish(Transform3D[][] result)
        {
            if (++_generated == gate.HoldGeneration)
            {
                gate.GeneratedFrames = result.Length;
                gate.Generated.Set();
                if (!gate.Release.Wait(TimeSpan.FromSeconds(20)))
                {
                    throw new TimeoutException("Test publication gate was not released.");
                }
            }
            return result;
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class ClosureFixture(ProbeGame root, MockXRRuntimeNode runtime, CharacterBody3D player, SceneTree tree) : IAsyncDisposable
    {
        public ProbeGame Root => root;
        public SceneTree Tree => tree;
        public CharacterBody3D Player => player;
        public Camera3D Head => runtime.Camera.CameraNode;
        public MockXRHandControllerNode Left => (MockXRHandControllerNode)runtime.LeftHandController;
        public MockXRHandControllerNode Right => (MockXRHandControllerNode)runtime.RightHandController;
        public PlayerMotionBricks Motion => player.GetNode<PlayerMotionBricks>("MotionBricks");
        public PlayerVRIK VRIK => player.GetNode<PlayerVRIK>("VRIK");
        public Skeleton3D Skeleton => player.GetNode<Skeleton3D>("Female/SOMA/Native/GeneralSkeleton");

        public static async Task<ClosureFixture> CreateAsync(float? backwardSpeedRatio = null, Action<PlayerMotionBricks>? configure = null)
        {
            SceneTree tree = TestUtils.GetSceneTree();
            await TestUtils.WaitForNextFrameAsync(tree);
            var root = new ProbeGame { Name = "PlayerClosureProbe" };
            var manager = new ProbeXRManager { Name = "XR" };
            MockXRRuntimeNode runtime = GD.Load<PackedScene>("res://assets/xr/mock_runtime.tscn").Instantiate<MockXRRuntimeNode>();
            CharacterBody3D player = GD.Load<PackedScene>("res://assets/characters/reference/ally_player.tscn").Instantiate<CharacterBody3D>();
            // The normal ally installer creates MotionBricks on entry. Configure the actual installed child
            // before its Ready, rather than dereferencing a child that does not exist before installation.
            void ConfigureInstalled(Node node)
            {
                if (node is PlayerMotionBricks motion && motion.GetParent() == player)
                {
                    if (backwardSpeedRatio.HasValue)
                    {
                        motion.BackwardSpeedRatio = backwardSpeedRatio.Value;
                    }
                    configure?.Invoke(motion);
                }
            }
            tree.NodeAdded += ConfigureInstalled;
            root.AddChild(manager);
            root.AddChild(runtime);
            root.AddChild(player);
            using var viewport = new SubViewport();
            Assert.True(runtime.Initialise(viewport, 90));
            manager.SetRuntime(runtime);
            try
            {
                tree.Root.AddChild(root);
                // The role installs its modules deferred, not during AddChild.
                await TestUtils.WaitForPhysicsFramesAsync(tree, 20);
            }
            finally { tree.NodeAdded -= ConfigureInstalled; }
            var fixture = new ClosureFixture(root, runtime, player, tree);
            Assert.True(fixture.VRIK.BindToXRServices());
            fixture.Head.Transform = new Transform3D(Basis.Identity, new Vector3(0, 1.4f, 0));
            runtime.LeftHandController.HandPositionNode.Position = new Vector3(-0.3f, 1.2f, -0.2f);
            runtime.RightHandController.HandPositionNode.Position = new Vector3(0.3f, 1.2f, -0.2f);
            _ = manager.EmitSignal(XRManager.SignalName.Initialised, true);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 10);
            Assert.Equal("", fixture.Motion.Failure);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            if (!GodotObject.IsInstanceValid(root))
            {
                return;
            }
            Task? completion = Motion.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }
}
