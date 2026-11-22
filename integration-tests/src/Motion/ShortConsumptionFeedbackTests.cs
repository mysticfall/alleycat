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
    /// <summary>Conditional first-return mechanism only; all subsequent native responses remain unproved.</summary>
    [Fact]
    public async Task ShortPositioningConsumesSavedProgressBeforeRequestingFacing()
    {
        const string source = "res://temp/motionbricks/navigation-playtests/20261109T141756532Z-ab25d9b5ade847c2841f6b13c7c47a84/";
        string output = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-heading-consumption-fix-01/");
        NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(ProjectSettings.GlobalizePath(source))];
        Assert.Equal(3727, entries.Length);
        NavigationSessionReader.Entry At(long n)
        {
            return entries.Single(e => e.Event.GetProperty("sequence").GetInt64() == n);
        }

        NavigationSessionReader.Entry first = At(868), native = At(911);
        JsonElement data = At(866).Event.GetProperty("data").GetProperty("data");
        int bones = first.Event.GetProperty("data").GetProperty("contextShape")[0].GetInt32();
        Transform3D[] Pose(float[] v, int at)
        {
            return [.. Enumerable.Range(0, bones).Select(i => FeedbackTransform(v, (at + i) * 12))];
        }

        Transform3D[][] response = [.. Enumerable.Range(0, 26).Select(i => Pose(native.Values, i * bones))];
        var session = new SavedFirstReturn(first.Values[..(4 * bones * 12)], first.Values[(4 * bones * 12)..], response);
        double delay = (At(912).Event.GetProperty("data").GetProperty("data").GetProperty("PublishedAt").GetInt64() - first.Event.GetProperty("clock").GetInt64()) / 1e9;
        double parentWindow = (entries[^1].Event.GetProperty("clock").GetInt64() - At(866).Event.GetProperty("clock").GetInt64()) / 1e9;
        Transform3D start = FeedbackTransform(data.GetProperty("actor").Deserialize<float[]>()!, 0), goal = FeedbackTransform(data.GetProperty("requested").Deserialize<float[]>()!, 0);
        NavigationSessionReader.Entry[] seeds = [.. entries.Where(e => e.Event.GetProperty("kind").GetString() == "pose" && e.Event.GetProperty("sequence").GetInt64() < 866).TakeLast(8)];
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "ConditionalShortConsumption" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Transform = start;
        MotionBricksNavigation? nav = null;
        var clock = new Stopwatch();
        var samples = new List<object>();
        var submissions = new List<object>();
        var handoffs = new List<object>();
        int count = 0;
        bool firstBound = false, released = false, crossedBeforeNext = false;
        double firstAt = 0;
        string? stop = null;
        MotionBricksNavigation.RoleSubmission? next = null;
        void Sample()
        {
            if (nav is null || !clock.IsRunning)
            {
                return;
            }
            Vector3 p = actor.GlobalPosition;
            if (count == 1 && p.DistanceTo(goal.Origin) <= nav.DestinationReachedDistance)
            {
                crossedBeforeNext = true;
            }
            samples.Add(new
            {
                wall = clock.Elapsed.TotalSeconds,
                nav.ObservationTime,
                root = new[] { p.X, p.Y, p.Z },
                distance = p.DistanceTo(goal.Origin),
                nav.RecordingAnatomicalHeading,
                nav.Phase,
                phaseTime = typeof(MotionBricksNavigation).GetField("_phaseTime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)
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
            value.SessionFactory = () => session;
            value.PoseObserved += Sample;
            value.NativeHandoffObserved = h => handoffs.Add(new { wall = clock.Elapsed.TotalSeconds, h.Request, h.Accepted, h.Reason, h.GameTime, h.ContextAt, h.Age, h.PublishedAt, h.ConsumedAt });
            value.WindowSubmitted += (ticket, context, target) =>
            {
                count++;
                float[] current = NavigationRecordingSink.Pack([.. context, .. target]);
                SavedDifference difference = CompareSaved(first.Values, current);
                var route = (NavigationRouteSnapshot?)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
                bool owns = route is not null && value.OwnsRoute(route);
                if (count == 1)
                {
                    firstAt = clock.Elapsed.TotalSeconds;
                    JsonElement expected = first.Event.GetProperty("data");
                    JsonElement actualRole = JsonSerializer.SerializeToElement(value.SubmittedRole);
                    JsonElement expectedRole = expected.GetProperty("role");
                    JsonElement actualStatus = JsonSerializer.SerializeToElement(value.CurrentStatus);
                    bool identity = ticket == 2 && actualStatus.GetProperty("ActiveRequest").GetRawText() == expected.GetProperty("status").GetProperty("ActiveRequest").GetRawText() &&
                        actualStatus.GetProperty("Revision").GetInt64() == expected.GetProperty("status").GetProperty("Revision").GetInt64();
                    firstBound = owns && identity && actualRole.GetRawText() == expectedRole.GetRawText() && difference.Matches;
                    if (!firstBound)
                    {
                        stop = "initial-binding-diverged";
                        value.SetPhysicsProcess(false);
                    }
                }
                else
                {
                    next = value.SubmittedRole;
                    stop = "new-future-request-no-saved-response";
                    value.SetPhysicsProcess(false);
                }
                submissions.Add(new
                {
                    wall = clock.Elapsed.TotalSeconds,
                    ticket,
                    owns,
                    value.SubmittedRole,
                    value.CurrentStatus,
                    firstBound,
                    expectedFirst = first.Values,
                    actual = current,
                    differenceToFirst = difference,
                    actualDistance = actor.GlobalPosition.DistanceTo(goal.Origin),
                    value.SourceRateCurveIntent
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
            ((NativeSomaRig)typeof(MotionBricksNavigation).GetField("_rig", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!).Apply(input);
            Assert.Equal(data.GetProperty("anatomicalHeadingWorld").GetSingle(), nav.RecordingAnatomicalHeading!.Value);
            clock.Start();
            Assert.Equal(0, scene.Call("_submit_destination", goal, Array.Empty<Vector3>()).AsInt32());
            _ = nav.Poll(nav.RecordingMeasuredTransform);
            nav.SetPhysicsProcess(true);
            while (stop is null && clock.Elapsed.TotalSeconds < parentWindow)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                if (Volatile.Read(ref session.Ready) != 0 && !released && stop is null)
                {
                    if (session.Context?.Matches != true || session.Target?.Matches != true)
                    {
                        stop = "worker-binding-diverged";
                        nav.SetPhysicsProcess(false);
                    }
                    else if (firstBound && clock.Elapsed.TotalSeconds >= firstAt + delay)
                    {
                        released = true;
                        session.Release();
                    }
                }
            }
            nav.SetPhysicsProcess(false);
            File.WriteAllText(Path.Combine(output, "component.json"), JsonSerializer.Serialize(new
            {
                source,
                parentWindow,
                elapsed = clock.Elapsed.TotalSeconds,
                stop,
                firstBound,
                crossedBeforeNext,
                next,
                session.Calls,
                session.Returned,
                workerContext = session.Context,
                workerTarget = session.Target,
                samples,
                submissions,
                handoffs,
                nav.Failure,
                nativeCalls = 0,
                scope = "CONDITIONAL unchanged first-return mechanism; future outputs and full25.096799003s request require new live/manual trial after review"
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            Assert.True(firstBound && session.Returned == 1, "First-response prerequisite failed; not component symptom-red.");
            Assert.True(crossedBeforeNext, "Saved positioning suffix progress was discarded before actual position reached tolerance.");
            Assert.NotNull(next);
            Assert.True(next.Decision.Inputs.PositionReached);
            Assert.EndsWith("_turn", next.SelectedRole);
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
