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
    /// <summary>NAV's nearby pure lateral positioning selects non-crossing source intent before guide placement.</summary>
    [Headless]
    [Fact]
    public async Task NearbyLeftPositioningSelectsNonCrossingBeforeTargetConstruction()
    {
        const string output = "res://temp/motionbricks/source-pipeline-09/natural-navigation-roles-01/latest.json";
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "NearbyNaturalRole" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        Assert.False(actor.IsInsideTree());
        actor.Position = new(-2, 0, -2);
        // Capture the authored target before tree entry; the role reparents this same object on entry.
        Skeleton3D skeleton = actor.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        NavigationRegion3D region = scene.GetNode<NavigationRegion3D>("NavigationRegion3D");
        NavigationMesh mesh = region.NavigationMesh;
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

        static float[] Pack(Vector3 p)
        {
            return [p.X, p.Y, p.Z];
        }

        bool OnMap(Vector2 p)
        {
            return rectangles.Any(r => p.X >= r.Low.X && p.X <= r.High.X && p.Y >= r.Low.Y && p.Y <= r.High.Y);
        }

        bool SegmentOnMap(Vector2 a, Vector2 b)
        {
            var fractions = new SortedSet<double> { 0, 1 };
            Vector2 delta = b - a;
            foreach ((Vector2 Low, Vector2 High) in rectangles)
            {
                foreach (float x in new[] { Low.X, High.X })
                {
                    if (delta.X != 0 && (x - a.X) / delta.X is > 0 and < 1)
                    {
                        _ = fractions.Add((x - a.X) / delta.X);
                    }
                }

                foreach (float z in new[] { Low.Y, High.Y })
                {
                    if (delta.Y != 0 && (z - a.Y) / delta.Y is > 0 and < 1)
                    {
                        _ = fractions.Add((z - a.Y) / delta.Y);
                    }
                }
            }
            double[] cuts = [.. fractions];
            return OnMap(a) && OnMap(b) && cuts.Zip(cuts.Skip(1)).All(p => OnMap(a.Lerp(b, (float)((p.First + p.Second) / 2))));
        }
        var provider = new LifecycleProviderState();
        MotionBricksNavigation? nav = null;
        var clock = Stopwatch.StartNew();
        var samples = new List<object>();
        var events = new List<object>();
        var windows = new List<object>();
        var roles = new List<string>();
        var contextChecks = new List<bool>();
        var safety = new List<string>();
        bool ready = false;
        Vector2? last = null;
        Vector3 start = default, goal = default;
        float initialHeading = 0;
        double fullSeconds = 0, tailSeconds = 0;
        T Field<T>(string name)
        {
            return (T)typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
        }

        float Heading()
        {
            return (float)typeof(MotionBricksNavigation).GetMethod("AnatomicalHeading", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(nav, [Field<RealisedPoseHistory>("_history").Context()[3]])! + Mathf.Pi;
        }

        object State()
        {
            return new
            {
                wall = clock.Elapsed.TotalSeconds,
                tick = Engine.GetPhysicsFrames(),
                nav!.ObservationTime,
                actor = Pack(actor.GlobalPosition),
                heading = Heading(),
                nav.Phase,
                nav.Failure,
                nav.HasDestination,
                nav.CurrentStatus,
                nav.ReviewedRole,
                selection = Field<ReviewedRoleSampler>("_reviewed").Selection,
                route = typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav),
                nav.SourceRateCurveIntent,
                observedRoot = Pack(nav.ObservedRoot.Origin),
                outcomes = nav.TerminalOutcomes.ToArray(),
                provider.Calls,
                provider.Faults,
                unavailable = Field<bool>("_unavailable"),
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
                Transform3D[][] actual = Field<RealisedPoseHistory>("_history").Context();
                bool fromActual = context.Zip(actual).All(p => p.First.SequenceEqual(p.Second)) && context[3][0].Origin == n.ObservedRoot.Origin;
                contextChecks.Add(fromActual);
                if (n.ReviewedRole.Length != 0)
                {
                    roles.Add(n.ReviewedRole);
                }

                windows.Add(new
                {
                    ticket,
                    fromActual,
                    state = State(),
                    roots = target.Select(p => Pack(SomaMotionData.ModelToWorld * p[0].Origin)).ToArray()
                });
            };
        }
        void Save()
        {
            File.WriteAllText(ProjectSettings.GlobalizePath(output), JsonSerializer.Serialize(new
            {
                nativeCalls = 0,
                injectedRouteContextPosePhaseRoleTargetFailure = false,
                scenario = "One-metre pure-left positioning from actual Ready orientation, same requested facing",
                start = Pack(start),
                goal = Pack(goal),
                initialHeading,
                fullSeconds,
                tailSeconds,
                expectedFirstMovementRole = "left_noncross",
                observedMovementRoles = roles,
                events,
                windows,
                samples,
                contextChecks,
                safety,
                final = ready ? State() : null
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
            ready = true;
            start = actor.GlobalPosition;
            initialHeading = Heading();
            goal = start + new Vector3(Mathf.Cos(initialHeading), 0, -Mathf.Sin(initialHeading));
            Assert.True(SegmentOnMap(Flat(start), Flat(goal)));
            skeleton.SkeletonUpdated += Observe;
            last = Flat(start);
            events.Add(new
            {
                kind = "idle-actual",
                state = State()
            });
            int accepted = nav.SelectDestination(new(new Basis(Vector3.Up, initialHeading - Mathf.Pi), goal), []);
            events.Add(new
            {
                kind = "request-selected",
                accepted,
                state = State()
            });
            double began = clock.Elapsed.TotalSeconds;
            while (clock.Elapsed.TotalSeconds - began < 12)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            fullSeconds = clock.Elapsed.TotalSeconds - began;
            events.Add(new
            {
                kind = "full-window-end",
                elapsed = fullSeconds,
                state = State()
            });
            for (int tick = 0; tick < 600 && (nav.TerminalOutcomes.Count == 0 || nav.Phase is not ("settled-idle" or "failed")); tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            began = clock.Elapsed.TotalSeconds;
            events.Add(new
            {
                kind = "tail-start",
                state = State()
            });
            while (clock.Elapsed.TotalSeconds - began < 3)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            tailSeconds = clock.Elapsed.TotalSeconds - began;
            events.Add(new
            {
                kind = "tail-end",
                elapsed = tailSeconds,
                state = State()
            });
            Save();
            Assert.Equal((int)NavigationDestinationResult.Accepted, accepted);
            Assert.True(fullSeconds >= 12 && tailSeconds >= 3);
            Assert.NotEmpty(windows);
            Assert.All(contextChecks, Assert.True);
            Assert.Equal(0, provider.Faults);
            Assert.False(Field<bool>("_unavailable"));
            Assert.Empty(safety);
            Assert.NotEmpty(roles);
            // NAV user policy supplies this oracle, not the existing forward-only geometry or classifier output.
            Assert.Equal("left_noncross", roles[0]);
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
