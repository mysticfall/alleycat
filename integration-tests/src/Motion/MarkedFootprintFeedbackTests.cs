using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
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
    private sealed class LocalTurnSession(bool followsTurn) : SomaInferenceWorker.ISession
    {
        private readonly TargetEndpointSession _inner = new(followsTurn);
        internal int Calls;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            _ = Interlocked.Increment(ref Calls);
            return _inner.Generate(context, target);
        }
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Reopened marked attempt3: retain the complete wall-stop response window, not an idle-only refusal.</summary>
    [Fact]
    public async Task MarkedWallStopRequestHasOwnedResponseThroughRecordedWindow()
    {
        const string source = "res://temp/motionbricks/navigation-playtests/20261109T123251832Z-3f644f3aa66e44c7b958af4e7effebc4/";
        string output = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-local-turn-02/");
        NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(ProjectSettings.GlobalizePath(source))];
        Assert.Equal(4370, entries.Length);
        NavigationSessionReader.Entry command = entries.Single(e => e.Event.GetProperty("sequence").GetInt64() == 3412);
        JsonElement data = command.Event.GetProperty("data").GetProperty("data");
        double window = (entries[^1].Event.GetProperty("clock").GetInt64() - command.Event.GetProperty("clock").GetInt64()) / 1e9;
        Assert.Equal(4.845948319, window);
        Transform3D start = FeedbackTransform(data.GetProperty("actor").Deserialize<float[]>()!, 0);
        Transform3D target = FeedbackTransform(data.GetProperty("requested").Deserialize<float[]>()!, 0);
        float heading = data.GetProperty("anatomicalHeadingWorld").GetSingle();
        NavigationSessionReader.Entry[] seeds = [.. entries.Where(e => e.Event.GetProperty("kind").GetString() == "pose" &&
            e.Event.GetProperty("sequence").GetInt64() < 3412).TakeLast(8)];
        int count = seeds[^1].Event.GetProperty("data").GetProperty("generatedBones").GetInt32();
        Transform3D[] Pose(NavigationSessionReader.Entry seed, int at)
        {
            return [.. Enumerable.Range(0, count).Select(i => FeedbackTransform(seed.Values, (at + i) * 12))];
        }

        JsonElement identity = entries.Single(e => e.Event.GetProperty("kind").GetString() == "content-identity").Event.GetProperty("data");
        var identityChecks = new Dictionary<string, object>();
        foreach (JsonProperty file in identity.GetProperty("hashes").EnumerateObject())
        {
            using FileStream stream = File.OpenRead(ProjectSettings.GlobalizePath("res://" + file.Name));
            string current = Convert.ToHexString(SHA256.HashData(stream));
            identityChecks[file.Name] = new
            {
                recorded = file.Value.GetString(),
                current,
                matches = current == file.Value.GetString()
            };
        }
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "MarkedWallStopResponse" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Transform = start;
        MotionBricksNavigation? nav = null;
        var provider = new LocalTurnSession(true);
        var clock = new Stopwatch();
        var samples = new List<object>();
        var submissions = new List<object>();
        int ownedMotion = 0;
        var ownedRoles = new List<string>();
        float maximumRotation = 0, maximumTravel = 0;
        void Sample()
        {
            if (nav is null || !clock.IsRunning)
            {
                return;
            }
            maximumRotation = Math.Max(maximumRotation, Math.Abs(Mathf.Wrap(nav.RecordingAnatomicalHeading!.Value - heading, -Mathf.Pi, Mathf.Pi)));
            maximumTravel = Math.Max(maximumTravel, actor.GlobalPosition.DistanceTo(start.Origin));
            samples.Add(new
            {
                wall = clock.Elapsed.TotalSeconds,
                tick = Engine.GetPhysicsFrames(),
                nav.ObservationTime,
                nav.Phase,
                nav.Failure,
                nav.CurrentStatus,
                heading = nav.RecordingAnatomicalHeading,
                actor = new[] { actor.GlobalPosition.X, actor.GlobalPosition.Y, actor.GlobalPosition.Z }
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
                string role = value.SubmittedRole?.SelectedRole ?? "";
                if (value.CurrentStatus.HasActiveRequest && role.Length > 0)
                {
                    ownedMotion++;
                    ownedRoles.Add(role);
                }
                submissions.Add(new
                {
                    wall = clock.Elapsed.TotalSeconds,
                    ticket,
                    role,
                    value.CurrentStatus,
                    source = value.SubmittedRole,
                    intent = value.SourceRateCurveIntent
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

            Transform3D[] input = Pose(seeds[^1], 3), feedback = Pose(seeds[^1], 3 + count);
            var history = new RealisedPoseHistory(count);
            foreach (NavigationSessionReader.Entry seed in seeds)
            {
                history.Record(seed.Event.GetProperty("data").GetProperty("time").GetDouble(), Pose(seed, 3 + count));
            }
            Field(nav, "_pose", input);
            Field(nav, "_observed", feedback);
            Field(nav, "_history", history);
            Property(nav, "ObservationTime", seeds[^1].Event.GetProperty("data").GetProperty("time").GetDouble());
            Field(nav, "_observedAt", nav.ObservationTime);
            Field(nav, "_record", false);
            Field(nav, "_lastGround", new Vector3(feedback[0].Origin.X, 0, feedback[0].Origin.Z));
            Property(nav, "Phase", "settled-idle");
            ((NativeSomaRig)typeof(MotionBricksNavigation).GetField("_rig", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!).Apply(input);
            Assert.Equal(heading, nav.RecordingAnatomicalHeading!.Value);
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
                session = source,
                records = entries.Length,
                identityChecks,
                loadedAssembly = typeof(Game).Assembly.ManifestModule.ModuleVersionId,
                window,
                elapsed = clock.Elapsed.TotalSeconds,
                seedSequences = seeds.Select(e => e.Event.GetProperty("sequence").GetInt64()).ToArray(),
                originalHeading = heading,
                ownsRoute,
                queryPath = route.PathPoints.Select(v => new[] { v.X, v.Y, v.Z }).ToArray(),
                ownedMotion,
                ownedRoles,
                generatedChunks = provider.Calls,
                submissions,
                samples,
                maximumRotation,
                maximumTravel,
                nav.Phase,
                nav.Failure,
                outcomes = nav.TerminalOutcomes,
                targetIntent = nav.SourceRateCurveIntent,
                nativeCalls = 0,
                scope = "Recorded canonical snapshot, normal scene; synthetic provider, not exact prior-trip or native replay"
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            Assert.True(ownsRoute);
            Assert.True(ownedMotion > 0, "Accepted marked attempt has no owned non-idle conditioning; failure-to-idle is not a travel response.");
            Assert.DoesNotContain(nav.TerminalOutcomes, o => o.Reason == NavigationTerminalReason.MotionUnavailable);
            Assert.True(maximumRotation > Mathf.DegToRad(nav.FacingToleranceDegrees));
            Assert.True(maximumTravel > nav.DestinationReachedDistance);
            Assert.Contains("forward", ownedRoles);
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
