using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>The actual normal-scene detour must complete, not merely refuse without poisoning inference.</summary>
    [Headless]
    [Fact]
    public async Task ActualDetourCompletesAndOutgoingSuccessorRemainsResponsive()
    {
        const string output = "res://temp/motionbricks/source-pipeline-09/detour-following-01/";
        using var input = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(output + "user-diagnostic.json")));
        using var preflight = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(output + "successor-preflight.json")));
        static Vector3 Vector(JsonElement p)
        {
            return new(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
        }

        static float[] Pack(Vector3 p)
        {
            return [p.X, p.Y, p.Z];
        }

        Vector3 start = Vector(input.RootElement.GetProperty("actorStart")), goal = Vector(input.RootElement.GetProperty("requestedTarget"));
        Vector3 successor = Vector(preflight.RootElement.GetProperty("target"));
        Vector3 corner = Vector(input.RootElement.GetProperty("route").GetProperty("path").GetProperty("points")[4]);
        Vector3 incoming = (corner - start).Normalized();
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "ActualDetourDelivery" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        Assert.False(actor.IsInsideTree());
        actor.Position = start;
        // Capture the authored target before tree entry; the role reparents this same object on entry.
        Skeleton3D skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        NavigationMesh mesh = scene.GetNode<NavigationRegion3D>("NavigationRegion3D").NavigationMesh;
        Vector3[] vertices = mesh.GetVertices();
        var rectangles = new List<(Vector2 Low, Vector2 High)>();
        for (int i = 0; i < mesh.GetPolygonCount(); i++)
        {
            int[] polygon = mesh.GetPolygon(i);
            if (polygon.Length != 4)
            {
                continue;
            }

            Vector3[] p = [.. polygon.Select(index => vertices[index])];
            rectangles.Add((new(p.Min(v => v.X), p.Min(v => v.Z)), new(p.Max(v => v.X), p.Max(v => v.Z))));
        }
        static Vector2 Flat(Vector3 p)
        {
            return new(p.X, p.Z);
        }

        bool OnMap(Vector2 p)
        {
            return rectangles.Any(r => p.X >= r.Low.X && p.X <= r.High.X && p.Y >= r.Low.Y && p.Y <= r.High.Y);
        }

        bool SegmentOnMap(Vector2 a, Vector2 b)
        {
            var fractions = new SortedSet<double> { 0, 1 };
            Vector2 d = b - a;
            foreach ((Vector2 Low, Vector2 High) in rectangles)
            {
                foreach (float x in new[] { Low.X, High.X })
                {
                    if (d.X != 0 && (x - a.X) / d.X is > 0 and < 1)
                    {
                        _ = fractions.Add((x - a.X) / d.X);
                    }
                }
                foreach (float z in new[] { Low.Y, High.Y })
                {
                    if (d.Y != 0 && (z - a.Y) / d.Y is > 0 and < 1)
                    {
                        _ = fractions.Add((z - a.Y) / d.Y);
                    }
                }
            }
            double[] f = [.. fractions];
            return OnMap(a) && OnMap(b) && f.Zip(f.Skip(1)).All(pair => OnMap(a.Lerp(b, (float)((pair.First + pair.Second) / 2))));
        }
        var provider = new LifecycleProviderState();
        MotionBricksNavigation? nav = null;
        var clock = Stopwatch.StartNew();
        var samples = new List<object>();
        var events = new List<object>();
        var windows = new List<object>();
        var contexts = new List<bool>();
        var safety = new List<string>();
        var payloads = new List<JsonElement>();
        Vector2? last = null;
        bool passedCorner = false;
        bool firstFinalLegObserved = false;
        T Field<T>(string name)
        {
            return (T)typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
        }

        float Heading()
        {
            Transform3D[][] history = Field<RealisedPoseHistory>("_history").Context();
            return (float)typeof(MotionBricksNavigation).GetMethod("AnatomicalHeading", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nav, [history[3]])! + Mathf.Pi;
        }
        object State()
        {
            return new
            {
                wall = clock.Elapsed.TotalSeconds,
                tick = Engine.GetPhysicsFrames(),
                nav!.Phase,
                nav.Failure,
                nav.Requests,
                nav.ObservationTime,
                actor = Pack(actor.GlobalPosition),
                heading = Heading(),
                observedRoot = Pack(nav.ObservedRoot.Origin),
                nav.HasDestination,
                nav.CurrentStatus,
                route = nav.CurrentPath.Select(Pack).ToArray(),
                nav.CurrentPathIndex,
                selection = Field<ReviewedRoleSampler>("_reviewed").Selection,
                nav.ReviewedRole,
                nav.SourceRateCurveIntent,
                unavailable = Field<bool>("_unavailable"),
                nativeRevision = Field<long>("_nativeRevision"),
                outcomes = nav.TerminalOutcomes.ToArray(),
                calls = provider.Calls,
                faults = provider.Faults,
                workerCompleted = nav.WorkerCompletion?.IsCompleted
            };
        }

        void Observe()
        {
            Vector2 now = Flat(actor.GlobalPosition);
            if (last is Vector2 before)
            {
                if (!SegmentOnMap(before, now))
                {
                    safety.Add("Observed centre sweep left NAV");
                }

            }
            last = now;
            if (nav!.CurrentStatus.ActiveRequest?.RequestID == 1 && (actor.GlobalPosition - corner).Dot(incoming) >= 0 && actor.GlobalPosition.X > corner.X)
            {
                passedCorner = true;
            }
            NavigationRouteSnapshot? route = RefusalRoute(nav!);
            if (nav.CurrentStatus.ActiveRequest?.RequestID == 1 && passedCorner && route is not null && route.ActivePathIndex == route.PathPoints.Count - 1)
            {
                firstFinalLegObserved = true;
            }

            samples.Add(State());
        }
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }

            nav = n;
            n.SessionFactory = () => new LifecycleSession(provider, false);
            n.WindowSubmitted += (ticket, context, target) =>
            {
                Transform3D[][] history = Field<RealisedPoseHistory>("_history").Context();
                bool actual = context.Zip(history).All(p => p.First.SequenceEqual(p.Second)) && context[3][0].Origin == n.ObservedRoot.Origin;
                contexts.Add(actual);
                windows.Add(new
                {
                    ticket,
                    actual,
                    state = State(),
                    roots = target.Select(p => Pack(SomaMotionData.ModelToWorld * p[0].Origin)).ToArray()
                });
            };
        }
        async Task TerminalAndSettle(int count)
        {
            for (int tick = 0; tick < 600 && (nav!.TerminalOutcomes.Count < count || nav.Phase is not ("settled-idle" or "failed")); tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
        }
        async Task Tail(int request)
        {
            double began = clock.Elapsed.TotalSeconds;
            events.Add(new
            {
                kind = "tail-start",
                request,
                state = State()
            });
            while (clock.Elapsed.TotalSeconds - began < 3)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            events.Add(new
            {
                kind = "tail-end",
                request,
                elapsed = clock.Elapsed.TotalSeconds - began,
                state = State()
            });
        }
        void Save()
        {
            File.WriteAllText(ProjectSettings.GlobalizePath(output + "latest.json"), JsonSerializer.Serialize(new
            {
                nativeCalls = 0,
                injectedRouteContextPosePhaseFailure = false,
                events,
                samples,
                windows,
                contexts,
                safety,
                passedCorner,
                firstFinalLegObserved,
                payloads,
                final = nav is null ? null : State()
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        }

        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            Assert.Same(nav, actor.Navigation);
            typeof(MotionBricksNavigation).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, new RefusalLogger(line =>
            {
                using var json = JsonDocument.Parse(line["Navigation refusal diagnostic: ".Length..]);
                payloads.Add(json.RootElement.Clone());
            }));
            skeleton.SkeletonUpdated += Observe;
            last = Flat(actor.GlobalPosition);
            events.Add(new
            {
                kind = "initial-actual",
                state = State()
            });
            int first = nav.SelectDestination(new(Basis.Identity, goal), []);
            double began = clock.Elapsed.TotalSeconds;
            ulong tickStart = Engine.GetPhysicsFrames();
            events.Add(new
            {
                kind = "first-selected",
                selection = first,
                state = State()
            });
            while (clock.Elapsed.TotalSeconds - began < 30)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            double fullWindow = clock.Elapsed.TotalSeconds - began;
            events.Add(new
            {
                kind = "full-window-end",
                elapsed = fullWindow,
                physicsTicks = Engine.GetPhysicsFrames() - tickStart,
                state = State()
            });
            await TerminalAndSettle(1);
            await Tail(1);
            int second = nav.SelectDestination(new(Basis.Identity, successor), []);
            events.Add(new
            {
                kind = "successor-selected",
                selection = second,
                state = State()
            });
            await TerminalAndSettle(2);
            await Tail(2);
            Save();
            Assert.True(fullWindow >= 30, "The complete real observation window must be retained.");
            Assert.Equal((int)NavigationDestinationResult.Accepted, first);
            Assert.Equal((int)NavigationDestinationResult.Accepted, second);
            Assert.False(SegmentOnMap(Flat(start), Flat(goal)));
            Assert.False(SegmentOnMap(Flat(start), Flat(goal)));
            Assert.Equal(2, nav.TerminalOutcomes.Count);
            Assert.Equal(NavigationTerminalOutcome.Completed, nav.TerminalOutcomes[0].Outcome);
            Assert.Equal(NavigationTerminalReason.ArrivalSequenceCompleted, nav.TerminalOutcomes[0].Reason);
            Assert.Equal(NavigationTerminalOutcome.Completed, nav.TerminalOutcomes[1].Outcome);
            Assert.Equal(NavigationTerminalReason.ArrivalSequenceCompleted, nav.TerminalOutcomes[1].Reason);
            Assert.True(passedCorner);
            Assert.True(firstFinalLegObserved);
            Assert.Empty(safety);
            Assert.All(contexts, Assert.True);
            Assert.NotEmpty(windows);
            Assert.Equal("settled-idle", nav.Phase);
            Assert.False(Field<bool>("_unavailable"));
            Assert.Equal(0, provider.Faults);
            Assert.False(nav.WorkerCompletion!.IsCompleted);
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
