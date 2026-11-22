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
    /// <summary>First real completion followed by an ordinary controller floor-ray destination, without ONNX.</summary>
    [Headless]
    [Fact]
    public async Task SourceRateCurve_CompletedTripAllowsSecondFloorDestination()
    {
        const string output = "res://temp/motionbricks/source-pipeline-09/repeat-navigation-diagnosis-01/";
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "RepeatNavigationDiagnosis" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        // Capture the authored target before tree entry; the role reparents this same object on entry.
        Skeleton3D skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        MotionBricksNavigation? nav = null;
        var events = new List<object>();
        var samples = new List<object>();
        var windows = new List<object>();
        var clock = Stopwatch.StartNew();
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
                physicsFrame = Engine.GetPhysicsFrames(),
                nav!.ObservationTime,
                nav.Phase,
                nav.Failure,
                nav.HasDestination,
                nav.Requests,
                nav.SamplesRecorded,
                actor = Pack(actor.GlobalPosition),
                observedRoot = Pose([nav.ObservedRoot])[0],
                ground = Pack(Field<Vector3>("_lastGround")),
                idleGround = Pack(Field<Vector3>("_idleGround")),
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
            if (nav is not null)
            {
                samples.Add(State());
            }
        }
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }

            nav = n;
            // Existing synthetic connector, not a worker/session bypass or manufactured completion state.
            n.SessionFactory = () => new TargetEndpointSession(true);
            n.WindowSubmitted += (ticket, context, target) => windows.Add(new
            {
                ticket,
                state = State(),
                context = context.Select(Pose).ToArray(),
                target = target.Select(Pose).ToArray()
            });
        }
        async Task UntilTerminal(int outcomeCount)
        {
            for (int tick = 0; tick < 600 && nav!.TerminalOutcomes.Count < outcomeCount && nav.Failure.Length == 0; tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
        }
        async Task ObserveTail()
        {
            double start = clock.Elapsed.TotalSeconds;
            while (clock.Elapsed.TotalSeconds - start < 3)
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
            NavigationMesh? mesh = regions.Count == 1 && GodotObject.InstanceFromId(NavigationServer3D.RegionGetOwnerId(regions[0])) is NavigationRegion3D region
                ? region.NavigationMesh : null;
            File.WriteAllText(ProjectSettings.GlobalizePath(output + "trace.json"), JsonSerializer.Serialize(new
            {
                syntheticConnector = "TargetEndpointSession(true),50 frames/context children/root lerp+facing slerp",
                nativeModelCalls = 0,
                events,
                windows,
                samples,
                final = State(),
                vertices = mesh?.GetVertices().Select(Pack).ToArray(),
                polygons = mesh is null ? null : Enumerable.Range(0, mesh.GetPolygonCount()).Select(mesh.GetPolygon).ToArray()
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
            using var firstRay = PhysicsRayQueryParameters3D.Create(new Vector3(0, 2, -.5f), new Vector3(0, -2, -.5f), 1);
            Dictionary firstHit = actor.GetWorld3D().DirectSpaceState.IntersectRay(firstRay);
            Assert.NotEmpty(firstHit);
            Assert.Equal("ClickTargetGround", ((Node)firstHit["collider"]).Name.ToString());
            var firstGoal = (Vector3)firstHit["position"];
            int firstSelection = nav.SelectDestination(new Transform3D(Basis.Identity, firstGoal), []);
            events.Add(new
            {
                kind = "first-destination",
                firstSelection,
                goal = Pack(firstGoal),
                state = State()
            });
            Assert.Equal((int)NavigationDestinationResult.Accepted, firstSelection);
            await UntilTerminal(1);
            events.Add(new
            {
                kind = "first-terminal",
                state = State()
            });
            Assert.Equal("", nav.Failure);
            _ = Assert.Single(nav.TerminalOutcomes);
            Assert.Equal(NavigationTerminalOutcome.Completed, nav.TerminalOutcomes[0].Outcome);
            Assert.Equal("settled-idle", nav.Phase);
            await ObserveTail();
            Camera3D camera = scene.GetNode<Camera3D>("Camera3D");
            // Fixed ordinary pixel selected from preserved engine observations, not an injected Y value.
            Vector2 pixel = new(40, 40);
            Vector3 rayStart = camera.ProjectRayOrigin(pixel), rayEnd = rayStart + (camera.ProjectRayNormal(pixel) * 1000);
            using var secondRay = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, 1);
            Dictionary hit = actor.GetWorld3D().DirectSpaceState.IntersectRay(secondRay);
            Assert.NotEmpty(hit);
            Assert.Equal("ClickTargetGround", ((Node)hit["collider"]).Name.ToString());
            var goal = (Vector3)scene.Call("ground_at", pixel); // Exact existing interactive controller result, no Y edit.
            Assert.Equal((Vector3)hit["position"], goal);
            var facing = (Basis)scene.Call("_facing", goal, goal);
            Vector3 start = actor.GlobalPosition;
            Vector3[] path = NavigationServer3D.MapGetPath(nav.GetNavigationMap(), start, goal, true, nav.NavigationLayers);
            events.Add(new
            {
                kind = "second-before",
                pixel = new[] { pixel.X, pixel.Y },
                rayStart = Pack(rayStart),
                rayEnd = Pack(rayEnd),
                collider = ((Node)hit["collider"]).Name.ToString(),
                goal = Pack(goal),
                closest = Pack(NavigationServer3D.MapGetClosestPoint(nav.GetNavigationMap(), goal)),
                path = path.Select(Pack).ToArray(),
                endpointError = path.Length == 0 ? (float?)null : path[^1].DistanceTo(goal),
                state = State()
            });
            Assert.NotEmpty(path);
            int secondSelection = nav.SelectDestination(new Transform3D(facing, goal), []);
            events.Add(new
            {
                kind = "second-selected",
                secondSelection,
                state = State()
            });
            Assert.Equal((int)NavigationDestinationResult.Accepted, secondSelection);
            await UntilTerminal(2);
            events.Add(new
            {
                kind = "second-terminal",
                state = State()
            });
            await ObserveTail();
            Save();
            Assert.Equal("", nav.Failure);
            Assert.Equal(2, nav.TerminalOutcomes.Count);
            Assert.Equal(NavigationTerminalOutcome.Completed, nav.TerminalOutcomes[1].Outcome);
            Assert.Equal("settled-idle", nav.Phase);
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
