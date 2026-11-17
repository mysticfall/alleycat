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
    private const float SavedTransportTolerance = .000004f;
    private sealed record SavedDifference(bool ShapeMatches, float Maximum, int Index, float Expected, float Actual)
    {
        public bool Matches => ShapeMatches && Maximum <= SavedTransportTolerance;
    }

    private static SavedDifference CompareSaved(float[] expected, float[] actual)
    {
        float maximum = 0;
        int index = 0;
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            float delta = float.IsFinite(actual[i]) ? Math.Abs(expected[i] - actual[i]) : float.MaxValue;
            if (delta > maximum)
            {
                maximum = delta;
                index = i;
            }
        }
        return new(expected.Length == actual.Length, maximum, index, expected[index], actual[index]);
    }

    private sealed class SavedFirstReturn(float[] expectedContext, float[] expectedTarget, Transform3D[][] response) : SomaInferenceWorker.ISession
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls, Ready, Returned;
        internal long ReadyAt;
        internal SavedDifference? Context, Target;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            if (Interlocked.Increment(ref Calls) != 1)
            {
                _ = _stop.Task.GetAwaiter().GetResult(); // No second saved response; released after scene shutdown.
                throw new OperationCanceledException("Bounded saved-return probe stopped before any second response.");
            }
            Context = CompareSaved(expectedContext, NavigationRecordingSink.Pack(context));
            Target = CompareSaved(expectedTarget, NavigationRecordingSink.Pack(target));
            ReadyAt = Stopwatch.GetTimestamp();
            Volatile.Write(ref Ready, 1);
            if (!_release.Task.GetAwaiter().GetResult() || !Context.Matches || !Target.Matches)
            {
                throw new OperationCanceledException("Saved response refused: binding or probe stop.");
            }
            _ = Interlocked.Increment(ref Returned);
            return [.. response.Select(p => (Transform3D[])p.Clone())];
        }
        internal void Release() => _release.TrySetResult(true);
        internal void Stop()
        {
            _ = _release.TrySetResult(false);
            _ = _stop.TrySetResult(false);
        }
        public void Dispose() => Stop();
    }

    /// <summary>One saved26-frame response only; compare the next natural submission, never script a replay chain.</summary>
    [Fact]
    public async Task SavedOppositeHeadingFirstBoundaryFeasibility()
    {
        const string source = "res://temp/motionbricks/navigation-playtests/20261109T141756532Z-ab25d9b5ade847c2841f6b13c7c47a84/";
        string output = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-heading-replay-01/");
        NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(ProjectSettings.GlobalizePath(source))];
        Assert.Equal(3727, entries.Length);
        NavigationSessionReader.Entry At(long sequence)
        {
            return entries.Single(e => e.Event.GetProperty("sequence").GetInt64() == sequence);
        }

        NavigationSessionReader.Entry command = At(866), first = At(868), second = At(945);
        NavigationSessionReader.Entry native = entries.Single(e => e.Event.GetProperty("kind").GetString() == "native-returned" && e.Event.GetProperty("data").GetProperty("ticket").GetInt64() == 2);
        JsonElement inputData = command.Event.GetProperty("data").GetProperty("data");
        int bones = first.Event.GetProperty("data").GetProperty("contextShape")[0].GetInt32();
        Transform3D[] Pose(float[] values, int at)
        {
            return [.. Enumerable.Range(0, bones).Select(i => FeedbackTransform(values, (at + i) * 12))];
        }

        var response = new Transform3D[native.Event.GetProperty("data").GetProperty("shape").GetArrayLength()][];
        for (int i = 0; i < response.Length; i++)
        {
            response[i] = Pose(native.Values, i * bones);
        }
        Assert.Equal(26, response.Length);
        var session = new SavedFirstReturn(first.Values[..(4 * bones * 12)], first.Values[(4 * bones * 12)..], response);
        double delay = (At(912).Event.GetProperty("data").GetProperty("data").GetProperty("PublishedAt").GetInt64() - first.Event.GetProperty("clock").GetInt64()) / 1e9;
        double fullWindow = (entries[^1].Event.GetProperty("clock").GetInt64() - command.Event.GetProperty("clock").GetInt64()) / 1e9;
        Transform3D start = FeedbackTransform(inputData.GetProperty("actor").Deserialize<float[]>()!, 0);
        Transform3D goal = FeedbackTransform(inputData.GetProperty("requested").Deserialize<float[]>()!, 0);
        NavigationSessionReader.Entry[] seeds = [.. entries.Where(e => e.Event.GetProperty("kind").GetString() == "pose" &&
            e.Event.GetProperty("sequence").GetInt64() < 866).TakeLast(8)];
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "FirstSavedHeadingBoundary" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Transform = start;
        MotionBricksNavigation? nav = null;
        var clock = new Stopwatch();
        var samples = new List<object>();
        var submissions = new List<object>();
        var handoffs = new List<object>();
        string? stop = null;
        bool metadataBound = false, released = false;
        double submittedAt = 0, releasedAt = 0;
        int count = 0;
        void Sample()
        {
            if (!clock.IsRunning || nav is null)
            {
                return;
            }
            Vector3 p = actor.GlobalPosition;
            samples.Add(new
            {
                wall = clock.Elapsed.TotalSeconds,
                tick = Engine.GetPhysicsFrames(),
                nav.ObservationTime,
                root = new[] { p.X, p.Y, p.Z },
                distance = p.DistanceTo(goal.Origin),
                nav.RecordingAnatomicalHeading,
                nav.Phase,
                nav.NativePhase,
                phaseTime = typeof(MotionBricksNavigation).GetField("_phaseTime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)
            });
        }
        bool Metadata(JsonElement expected, long ticket)
        {
            MotionBricksNavigation.RoleSubmission? role = nav!.SubmittedRole;
            JsonElement e = expected.GetProperty("role"), s = e.GetProperty("Source");
            JsonElement status = JsonSerializer.SerializeToElement(nav.CurrentStatus);
            JsonElement recordedStatus = expected.GetProperty("status");
            return role is not null && ticket == expected.GetProperty("ticket").GetInt64() &&
                status.GetProperty("ActiveRequest").GetRawText() == recordedStatus.GetProperty("ActiveRequest").GetRawText() &&
                status.GetProperty("Revision").GetInt64() == recordedStatus.GetProperty("Revision").GetInt64() &&
                role.Revision == e.GetProperty("Revision").GetInt64() && role.Generation == e.GetProperty("Generation").GetInt64() &&
                role.RouteRevision == e.GetProperty("RouteRevision").GetInt64() && role.SelectedRole == e.GetProperty("SelectedRole").GetString() &&
                role.Decision.Positioning == e.GetProperty("Decision").GetProperty("Positioning").GetBoolean() &&
                role.Decision.Reason == e.GetProperty("Decision").GetProperty("Reason").GetString() &&
                role.Source.Key == s.GetProperty("Key").GetString() && role.Source.Revision == s.GetProperty("Revision").GetInt64() &&
                role.Source.WindowStart == s.GetProperty("WindowStart").GetInt32() && role.Source.CycleIndex == s.GetProperty("CycleIndex").GetInt64() &&
                role.Source.Available == s.GetProperty("Available").GetBoolean() &&
                Math.Abs(role.Source.InCycleFrame - s.GetProperty("InCycleFrame").GetDouble()) <= SavedTransportTolerance &&
                Math.Abs(role.Source.SourceFrame - s.GetProperty("SourceFrame").GetDouble()) <= SavedTransportTolerance &&
                Math.Abs(role.Source.FutureFrame - s.GetProperty("FutureFrame").GetDouble()) <= SavedTransportTolerance;
        }
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation value)
            {
                return;
            }
            nav = value;
            value.Ready += () => value.SetPhysicsProcess(false);
            value.SessionFactory = () => session;
            value.PoseObserved += Sample;
            value.NativeHandoffObserved = h => handoffs.Add(new
            {
                wall = clock.Elapsed.TotalSeconds,
                h.Request,
                h.Accepted,
                h.Reason,
                h.GameTime,
                h.ContextAt,
                h.Age,
                h.OldRemaining,
                h.PublishedAt,
                h.ConsumedAt
            });
            value.WindowSubmitted += (ticket, context, target) =>
            {
                count++;
                NavigationSessionReader.Entry expected = count == 1 ? first : second;
                float[] current = NavigationRecordingSink.Pack([.. context, .. target]);
                SavedDifference difference = CompareSaved(expected.Values, current);
                bool metadata = Metadata(expected.Event.GetProperty("data"), ticket);
                submissions.Add(new
                {
                    wall = clock.Elapsed.TotalSeconds,
                    tick = Engine.GetPhysicsFrames(),
                    value.ObservationTime,
                    ticket,
                    metadata,
                    difference,
                    value.SubmittedRole,
                    value.CurrentStatus,
                    expected = expected.Values,
                    actual = current,
                    value.SourceRateCurveIntent
                });
                if (count == 1)
                {
                    submittedAt = clock.Elapsed.TotalSeconds;
                    metadataBound = metadata && difference.Matches;
                    if (!metadataBound)
                    {
                        stop = "initial-submission-binding-diverged";
                        value.SetPhysicsProcess(false);
                    }
                }
                else
                {
                    stop = metadata && difference.Matches ? "next-boundary-matched-no-second-response-authorised" : "next-submission-binding-diverged";
                    value.SetPhysicsProcess(false);
                }
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

            Transform3D[] input = Pose(seeds[^1].Values, 3), feedback = Pose(seeds[^1].Values, 3 + bones);
            var history = new RealisedPoseHistory(bones);
            foreach (NavigationSessionReader.Entry seed in seeds)
            {
                history.Record(seed.Event.GetProperty("data").GetProperty("time").GetDouble(), Pose(seed.Values, 3 + bones));
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
            Assert.Equal(inputData.GetProperty("anatomicalHeadingWorld").GetSingle(), nav.RecordingAnatomicalHeading!.Value);
            clock.Start();
            Assert.Equal(0, scene.Call("_submit_destination", goal, Array.Empty<Vector3>()).AsInt32());
            _ = nav.Poll(nav.RecordingMeasuredTransform);
            var route = (NavigationRouteSnapshot)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
            bool ownsRoute = nav.OwnsRoute(route);
            nav.SetPhysicsProcess(true);
            while (stop is null && clock.Elapsed.TotalSeconds < fullWindow)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                if (Volatile.Read(ref session.Ready) != 0 && !released && stop is null)
                {
                    if (session.Context?.Matches != true || session.Target?.Matches != true)
                    {
                        stop = "initial-worker-binding-diverged";
                        nav.SetPhysicsProcess(false);
                    }
                    else if (metadataBound && clock.Elapsed.TotalSeconds >= submittedAt + delay)
                    {
                        releasedAt = clock.Elapsed.TotalSeconds;
                        released = true;
                        session.Release();
                    }
                }
            }
            nav.SetPhysicsProcess(false);
            File.WriteAllText(Path.Combine(output, "feasibility.json"), JsonSerializer.Serialize(new
            {
                source,
                fullWindow,
                elapsed = clock.Elapsed.TotalSeconds,
                stop,
                ownsRoute,
                tolerance = SavedTransportTolerance,
                recordedSubmitToPublication = delay,
                submittedAt,
                releasedAt,
                releaseOvershoot = releasedAt - submittedAt - delay,
                clockFrequency = Stopwatch.Frequency,
                workerReadyAt = session.ReadyAt,
                metadataBound,
                workerContext = session.Context,
                workerTarget = session.Target,
                session.Calls,
                session.Returned,
                submissions,
                handoffs,
                samples,
                nav.Failure,
                nativeCalls = 0,
                scope = "First saved-return boundary only; not full parent reproduction; no second saved response"
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            Assert.True(session.Returned <= 1);
            Assert.NotNull(stop); // Feasibility stopping condition, not a parent behaviour assertion.
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
            session.Stop();
            if (completion is not null)
            {
                await completion;
            }
        }
    }
}
