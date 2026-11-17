using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Godot.Collections;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>One fixed three-click same-floor series; distinguish remaining query categories after real completions.</summary>
    [Headless]
    [Fact]
    public async Task SourceRateCurve_ThreeFloorRequestsAfterCompletionRemainReachable()
    {
        const string output = "res://temp/motionbricks/source-pipeline-09/repeat-navigation-remaining-01/series.json";
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "RemainingRepeatNavigationDiagnosis" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        Assert.False(actor.IsInsideTree());
        actor.Position = new Vector3(-1.6f, 0, 2.5f); // Initial setup only; no writes after scene attachment.
        Skeleton3D skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        MotionBricksNavigation? nav = null;
        var clock = Stopwatch.StartNew();
        var events = new List<object>();
        var samples = new List<object>();
        var windows = new List<object>();
        static float[] Pack(Vector3 p)
        {
            return [p.X, p.Y, p.Z];
        }

        static float[][] Pose(Transform3D[] pose)
        {
            return [.. pose.Select(p =>
                {
                    Quaternion q = p.Basis.Orthonormalized().GetRotationQuaternion();
                    return new[] { p.Origin.X, p.Origin.Y, p.Origin.Z, q.X, q.Y, q.Z, q.W };
                })];
        }

        T Field<T>(string name, Type? owner = null)
        {
            return (T)(owner ?? typeof(MotionBricksNavigation))
                    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
        }

        object State()
        {
            return new
            {
                wall = clock.Elapsed.TotalSeconds,
                tick = Engine.GetPhysicsFrames(),
                nav!.ObservationTime,
                nav.Phase,
                nav.Failure,
                nav.Requests,
                nav.HasDestination,
                nav.SamplesRecorded,
                actor = Pack(actor.GlobalPosition),
                root = Pose([nav.ObservedRoot])[0],
                lastGround = Pack(Field<Vector3>("_lastGround")),
                nativeRevision = Field<long>("_nativeRevision"),
                routeRevision = Field<long>("_routeRevision", typeof(NavigationBase)),
                mapIteration = NavigationServer3D.MapGetIterationId(nav.GetNavigationMap()),
                status = nav.CurrentStatus,
                outcomes = nav.TerminalOutcomes.ToArray(),
                intent = nav.SourceRateCurveIntent,
                targetIntent = nav.SourceRateCurveIntent
            };
        }

        void Observe()
        {
            Vector3 Bone(string name)
            {
                return skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone(name)).Origin;
            }

            samples.Add(new
            {
                state = State(),
                leftFoot = Pack(Bone("LeftFoot")),
                rightFoot = Pack(Bone("RightFoot"))
            });
        }
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }

            nav = n;
            n.SessionFactory = () => new TargetEndpointSession(true);
            n.WindowSubmitted += (ticket, context, target) => windows.Add(new
            {
                ticket,
                state = State(),
                context = context.Select(Pose).ToArray(),
                target = target.Select(Pose).ToArray()
            });
        }
        async Task Tail()
        {
            double began = clock.Elapsed.TotalSeconds;
            while (clock.Elapsed.TotalSeconds - began < 3)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
        }
        void Save()
        {
            if (nav is null)
            {
                return;
            }

            Rid map = nav.GetNavigationMap();
            Array<Rid> regions = NavigationServer3D.MapGetRegions(map);
            NavigationRegion3D? region = regions.Count == 1 ? GodotObject.InstanceFromId(NavigationServer3D.RegionGetOwnerId(regions[0])) as NavigationRegion3D : null;
            NavigationMesh? mesh = region?.NavigationMesh;
            CollisionShape3D ground = scene.GetNode<CollisionShape3D>("ClickTargetGround/GroundCollision");
            File.WriteAllText(ProjectSettings.GlobalizePath(output), JsonSerializer.Serialize(new
            {
                initialSetup = new[] { -1.6f, 0, 2.5f },
                initialSetupBeforeTree = true,
                syntheticConnector = "TargetEndpointSession(true); no native model constructed",
                nativeModelCalls = 0,
                events,
                samples,
                windows,
                final = State(),
                map = new
                {
                    regionCount = regions.Count,
                    linkCount = NavigationServer3D.MapGetLinks(map).Count,
                    nav.NavigationLayers,
                    regionEnabled = region?.Enabled,
                    regionLayers = region?.NavigationLayers,
                    regionTransformIdentity = region?.GlobalTransform == Transform3D.Identity,
                    floorTop = ground.GlobalPosition.Y + (((BoxShape3D)ground.Shape).Size.Y / 2),
                    vertices = mesh?.GetVertices().Select(Pack).ToArray(),
                    polygons = mesh is null ? null : Enumerable.Range(0, mesh.GetPolygonCount()).Select(mesh.GetPolygon).ToArray()
                }
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            skeleton.SkeletonUpdated += Observe;
            events.Add(new
            {
                kind = "initial",
                state = State()
            });
            Camera3D camera = scene.GetNode<Camera3D>("Camera3D");
            Vector3[] aims = [new(-1.6f, 0, 2), new(-1.6f, 0, 1.5f), new(-1.6f, 0, 5.15f)];
            for (int index = 0; index < aims.Length; index++)
            {
                Vector2 pixel = camera.UnprojectPosition(aims[index]);
                Vector3 rayStart = camera.ProjectRayOrigin(pixel), rayEnd = rayStart + (camera.ProjectRayNormal(pixel) * 1000);
                using var ray = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, 1);
                Dictionary hit = actor.GetWorld3D().DirectSpaceState.IntersectRay(ray);
                Assert.NotEmpty(hit);
                Assert.Equal("ClickTargetGround", ((Node)hit["collider"]).Name.ToString());
                var goal = (Vector3)scene.Call("ground_at", pixel);
                Assert.Equal((Vector3)hit["position"], goal);
                var facing = (Basis)scene.Call("_facing", goal, goal);
                Vector3[] path = NavigationServer3D.MapGetPath(nav.GetNavigationMap(), actor.GlobalPosition, goal, true, nav.NavigationLayers);
                events.Add(new
                {
                    kind = "before-request",
                    request = index + 1,
                    aim = Pack(aims[index]),
                    pixel = new[] { pixel.X, pixel.Y },
                    rayStart = Pack(rayStart),
                    rayEnd = Pack(rayEnd),
                    collider = ((Node)hit["collider"]).Name.ToString(),
                    goal = Pack(goal),
                    path = path.Select(Pack).ToArray(),
                    endpointError = path.Length == 0 ? (float?)null : path[^1].DistanceTo(goal),
                    closest = Pack(NavigationServer3D.MapGetClosestPoint(nav.GetNavigationMap(), goal)),
                    state = State()
                });
                Assert.NotEmpty(path);
                int selection = nav.SelectDestination(new Transform3D(facing, goal), []);
                events.Add(new
                {
                    kind = "selected",
                    request = index + 1,
                    selection,
                    state = State()
                });
                Assert.Equal((int)NavigationDestinationResult.Accepted, selection);
                for (int tick = 0; tick < 600 && nav.TerminalOutcomes.Count < index + 1 && nav.Failure.Length == 0; tick++)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                }

                events.Add(new
                {
                    kind = "terminal",
                    request = index + 1,
                    state = State()
                });
                await Tail();
                events.Add(new
                {
                    kind = "tail-end",
                    request = index + 1,
                    state = State()
                });
                Save();
                Assert.Equal("", nav.Failure);
                Assert.Equal(index + 1, nav.TerminalOutcomes.Count);
                Assert.Equal(NavigationTerminalOutcome.Completed, nav.TerminalOutcomes[index].Outcome);
                Assert.Equal("settled-idle", nav.Phase);
            }
        }
        finally
        {
            Save();
            skeleton.SkeletonUpdated -= Observe;
            tree.NodeAdded -= Configure;
            Task? completion = nav?.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(30));
            }

        }
    }
}
