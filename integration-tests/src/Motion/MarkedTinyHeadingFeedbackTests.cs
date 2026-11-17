using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>The marked tiny adjustment must remain local across the complete response, not loop and return.</summary>
    [Fact]
    public async Task MarkedTinyAdjustmentWithDifferentFacingRemainsLocalThroughRecordedWindow()
    {
        const string source = "res://temp/motionbricks/navigation-playtests/20261109T134436608Z-ae1fb836a5c54682ac8cb9b7acc485e1/";
        string output = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-heading-issue-01/");
        NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(ProjectSettings.GlobalizePath(source))];
        Assert.Equal(2378, entries.Length);
        NavigationSessionReader.Entry command = entries.Single(e => e.Event.GetProperty("sequence").GetInt64() == 851);
        JsonElement data = command.Event.GetProperty("data").GetProperty("data");
        double window = (entries[^1].Event.GetProperty("clock").GetInt64() - command.Event.GetProperty("clock").GetInt64()) / 1e9;
        Assert.Equal(10.858264787, window);
        Transform3D start = FeedbackTransform(data.GetProperty("actor").Deserialize<float[]>()!, 0);
        Transform3D target = FeedbackTransform(data.GetProperty("requested").Deserialize<float[]>()!, 0);
        float heading = data.GetProperty("anatomicalHeadingWorld").GetSingle();
        NavigationSessionReader.Entry[] seeds = [.. entries.Where(e => e.Event.GetProperty("kind").GetString() == "pose" &&
            e.Event.GetProperty("sequence").GetInt64() < 851).TakeLast(8)];
        int bones = seeds[^1].Event.GetProperty("data").GetProperty("generatedBones").GetInt32();
        Transform3D[] Pose(NavigationSessionReader.Entry seed, int at)
        {
            return [.. Enumerable.Range(0, bones).Select(i => FeedbackTransform(seed.Values, (at + i) * 12))];
        }

        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "MarkedTinyHeadingResponse" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Transform = start;
        MotionBricksNavigation? nav = null;
        var provider = new LocalTurnSession(true);
        var clock = new Stopwatch();
        var samples = new List<object>();
        var submissions = new List<object>();
        var decisions = new List<MotionBricksNavigation.RoleSubmission>();
        float maximumExcursion = 0, maximumRotation = 0, pathLength = 0;
        Vector3 last = start.Origin;
        void Sample()
        {
            if (nav is null || !clock.IsRunning)
            {
                return;
            }
            maximumExcursion = Math.Max(maximumExcursion, actor.GlobalPosition.DistanceTo(start.Origin));
            maximumRotation = Math.Max(maximumRotation, Math.Abs(Mathf.Wrap(nav.RecordingAnatomicalHeading!.Value - heading, -Mathf.Pi, Mathf.Pi)));
            pathLength += actor.GlobalPosition.DistanceTo(last);
            last = actor.GlobalPosition;
            samples.Add(new
            {
                wall = clock.Elapsed.TotalSeconds,
                nav.ObservationTime,
                nav.Phase,
                nav.Failure,
                heading = nav.RecordingAnatomicalHeading,
                root = new[] { last.X, last.Y, last.Z },
                nav.CurrentStatus
            });
        }
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation value)
            {
                return;
            }
            nav = value;
            value.Ready += () => value.SetPhysicsProcess(false);
            value.SessionFactory = () => provider;
            value.PoseObserved += Sample;
            value.WindowSubmitted += (ticket, context, targets) =>
            {
                if (value.CurrentStatus.HasActiveRequest && value.SubmittedRole is { } role)
                {
                    decisions.Add(role);
                }
                submissions.Add(new
                {
                    wall = clock.Elapsed.TotalSeconds,
                    ticket,
                    value.SubmittedRole,
                    value.SourceRateCurveIntent,
                    value.PositioningPlacementIntent,
                    value.CurrentStatus
                });
            };
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            Assert.Equal(start, actor.GlobalTransform);
            static void Field(object node, string name, object value)
            {
                node.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(node, value);
            }

            static void Property(object node, string name, object value)
            {
                node.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(node, value);
            }

            Transform3D[] input = Pose(seeds[^1], 3), feedback = Pose(seeds[^1], 3 + bones);
            var history = new RealisedPoseHistory(bones);
            foreach (NavigationSessionReader.Entry seed in seeds)
            {
                history.Record(seed.Event.GetProperty("data").GetProperty("time").GetDouble(), Pose(seed, 3 + bones));
            }
            Field(nav, "_pose", input);
            Field(nav, "_observed", feedback);
            Field(nav, "_history", history);
            Property(nav, "ObservationTime", seeds[^1].Event.GetProperty("data").GetProperty("time").GetDouble());
            Field(nav, "_observedAt", nav.ObservationTime);
            Field(nav, "_record", false);
            Field(nav, "_lastGround", new Vector3(feedback[0].Origin.X, 0, feedback[0].Origin.Z));
            Property(nav, "Phase", seeds[^1].Event.GetProperty("data").GetProperty("Phase").GetString()!);
            ((CalibratedSomaRig)typeof(MotionBricksNavigation).GetField("_rig", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!).Apply(input);
            Assert.Equal(heading, nav.RecordingAnatomicalHeading!.Value);
            float initialDistance = start.Origin.DistanceTo(target.Origin);
            float tolerance = nav.DestinationReachedDistance;
            Assert.True(initialDistance > tolerance); // Do not classify this as already position-reached.
            float permittedExcursion = initialDistance + tolerance;
            Assert.Equal(0, scene.Call("_submit_destination", target, Array.Empty<Vector3>()).AsInt32());
            _ = nav.Poll(nav.RecordingMeasuredTransform);
            var route = (NavigationRouteSnapshot)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
            bool ownsRoute = nav.OwnsRoute(route);
            clock.Start();
            nav.SetPhysicsProcess(true);
            while (clock.Elapsed.TotalSeconds < window)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            File.WriteAllText(Path.Combine(output, "baseline.json"), JsonSerializer.Serialize(new
            {
                source,
                records = entries.Length,
                window,
                elapsed = clock.Elapsed.TotalSeconds,
                seedSequences = seeds.Select(e => e.Event.GetProperty("sequence").GetInt64()).ToArray(),
                loadedAssembly = typeof(Game).Assembly.ManifestModule.ModuleVersionId,
                heading,
                initialDistance,
                tolerance,
                permittedExcursion,
                maximumExcursion,
                maximumRotation,
                pathLength,
                ownsRoute,
                route = route.PathPoints.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
                submissions,
                samples,
                outcomes = nav.TerminalOutcomes,
                nav.Phase,
                nav.Failure,
                generatedChunks = provider.Calls,
                nativeCalls = 0,
                scope = "Recorded canonical state seeded once; normal scene/physics/feedback; synthetic target connector, not bit-identical native replay"
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            Assert.True(ownsRoute);
            Assert.NotEmpty(decisions);
            Assert.DoesNotContain(nav.TerminalOutcomes, o => o.Reason == NavigationTerminalReason.MotionUnavailable);
            Assert.True(maximumExcursion <= permittedExcursion,
                $"Tiny adjustment became an outward/returning journey: excursion {maximumExcursion}m exceeds original displacement plus existing position tolerance {permittedExcursion}m.");
            Assert.DoesNotContain(decisions, d => d.SelectedRole == "forward" && !d.Decision.Positioning);
            Assert.True(maximumRotation > Mathf.DegToRad(nav.FacingToleranceDegrees));
        }
        finally
        {
            clock.Stop();
            tree.NodeAdded -= Configure;
            if (nav is not null)
            {
                nav.PoseObserved -= Sample;
            }
            Task? completion = nav?.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion;
            }
        }
    }
}
