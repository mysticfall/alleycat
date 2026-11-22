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
    private sealed class FiniteTurnSession(bool gateSuccessor) : SomaInferenceWorker.ISession
    {
        internal readonly ManualResetEventSlim Release = new(false);
        internal int Calls;
        internal bool Disposed;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            int count = Interlocked.Increment(ref Calls) % 2 == 1 ? 30 : 50;
            if (gateSuccessor && Calls == 2 && !Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Finite successor gate was not released.");
            }
            Transform3D[][] frames = [.. Enumerable.Range(0, count).Select(f => context[3].Select((t, j) => SomaMotionData.Blend(t, target[0][j], f / (float)(count - 1))).ToArray())];
            if (Calls == 1)
            {
                foreach (Transform3D[] frame in frames)
                {
                    frame[0].Basis = new Basis(Vector3.Up, -.8f);
                }
            }
            return frames;
        }
        public void Dispose()
        {
            Release.Dispose();
            Disposed = true;
        }
    }

    /// <summary>Approved component alternative, not generated gait: all four ordered cues must actually finish.</summary>
    [Headless]
    [Fact]
    public Task FiniteNAVTurn_RecordedContextServicesAllCuesBeforeFiniteArrival() => CheckFiniteRunAsync(false);

    /// <summary>Cancellation during a successor hold must report acquired actor facing, not contrary hips.</summary>
    [Headless]
    [Fact]
    public Task FiniteNAVTurn_CancelPendingSuccessorReportsHeldActor() => CheckFiniteRunAsync(true);

    private static async Task CheckFiniteRunAsync(bool cancelSuccessor)
    {
        const string folder = "res://temp/motionbricks/nav-finite-turn-plan-01/";
        var session = new FiniteTurnSession(cancelSuccessor);
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        MotionBricksNavigation? nav = null;
        var submitted = new List<(long Ticket, Transform3D[][] Target)>();
        var endpoints = new List<long>();
        double? firstProgress = null;
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }
            nav = n;
            n.SessionFactory = () => session;
            n.WindowSubmitted += (ticket, _, target) => submitted.Add((ticket, [.. target.Select(p => (Transform3D[])p.Clone())]));
            n.PlaybackObserved = (ticket, poses, frame) =>
            {
                firstProgress = poses is not null && submitted.Count == 1 ? frame / (poses.Length - 1) : null;
                if (poses is not null && frame >= poses.Length - 1 && !endpoints.Contains(ticket))
                {
                    endpoints.Add(ticket);
                }
            };
        }
        tree.NodeAdded += Configure;
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            nav.SetPhysicsProcess(false);
            byte[] bytes = File.ReadAllBytes(ProjectSettings.GlobalizePath(folder + "context.f32"));
            float[] values = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            Transform3D[][] context = [.. Enumerable.Range(0, 4).Select(f => Enumerable.Range(0, 77).Select(j => FeedbackTransform(values, ((f * 77) + j) * 12)).ToArray())];
            void Field(string name, object value)
            {
                typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, value);
            }

            var history = new RealisedPoseHistory(77);
            for (int f = 0; f < 8; f++)
            {
                history.Record(f / 30d, context[Math.Max(0, f - 4)]);
            }
            Field("_history", history);
            Field("_observed", (Transform3D[])context[3].Clone());
            Field("_pose", (Transform3D[])context[3].Clone());
            Field("_lastGround", new Vector3(context[3][0].Origin.X, 0, context[3][0].Origin.Z));
            actor.GlobalPosition = SomaMotionData.ModelToWorld * new Vector3(context[3][0].Origin.X, 0, context[3][0].Origin.Z);
            using var scenario = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(folder + "scenario.json")));
            float[] command = [.. scenario.RootElement.GetProperty("command").GetProperty("data").GetProperty("data").GetProperty("requested").EnumerateArray().Select(v => v.GetSingle())];
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(FeedbackTransform(command, 0), []));
            nav.SetPhysicsProcess(false);
            var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
            var world = new Transform3D[77];
            data.WorldPose(context[3], world);
            double begin = SomaInference.Heading(world[72].Origin, world[67].Origin) + Math.PI;
            int checkedFrames = 0;
            for (int tick = 0; tick <= 866; tick++)
            {
                nav._PhysicsProcess(1d / 60);
                if (cancelSuccessor && submitted.Count == 2)
                {
                    _ = Assert.Single(endpoints);
                    // Poll once with B pending after A's endpoint; ClearDestination consumes that cached observation.
                    nav._PhysicsProcess(1d / 60);
                    double actual = actor.GlobalRotation.Y;
                    double held = nav.RecordingMeasuredTransform.Basis.GetEuler().Y;
                    nav.ClearDestination();
                    double reported = Assert.Single(nav.TerminalOutcomes).FinalTransform.YawRadians;
                    Assert.InRange(Math.Abs(Math.IEEERemainder(reported - actual, Math.Tau)), 0, .00001);
                    Assert.InRange(Math.Abs(Math.IEEERemainder(held - actual, Math.Tau)), 0, .00001);
                    return;
                }
                if (firstProgress is { } fraction && submitted.Count == 1)
                {
                    double expected = begin - Math.PI + (fraction * 8.026955 * Math.PI / 180);
                    Assert.InRange(Math.Abs(Math.IEEERemainder(actor.GlobalRotation.Y - expected, Math.Tau)), 0, .00001);
                    checkedFrames++;
                }
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.True(checkedFrames >= 50);
            Assert.False(cancelSuccessor, "Did not observe a pending successor.");
            Assert.InRange(Math.Abs(Math.IEEERemainder(actor.GlobalRotation.Y - FeedbackTransform(command, 0).Basis.GetEuler().Y, Math.Tau)), 0, .00001);
            File.WriteAllText(ProjectSettings.GlobalizePath("res://temp/motionbricks/anim002-regenerated-source-adoption-01/finite-result-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new
            {
                session.Calls,
                endpoints,
                submissions = submitted.Select(s => new { s.Ticket, source = nav.ReviewedRole, leftFoot = s.Target[0][69].Origin }),
                outcomes = nav.TerminalOutcomes.Count,
                nav.Phase
            }));
            Assert.Equal(5, submitted.Count); // Four primary cues then one existing external-idle connection.
            Assert.Equal(submitted.Select(s => s.Ticket), endpoints);
            using var source = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/landing-ablation-01.json")));
            JsonElement samples = source.RootElement.GetProperty("samples");
            double goal = Math.Atan2(-command[6], -command[8]);
            double angle = Math.IEEERemainder(goal - begin, Math.Tau);
            double span = samples[1005].GetProperty("heading_radians").GetDouble() - samples[945].GetProperty("heading_radians").GetDouble();
            for (int cue = 0; cue < 4; cue++)
            {
                int first = cue % 2 == 0 ? 947 : 961;
                double allocation = cue < 2 ? Math.PI * 2 / 3 : angle - (Math.PI * 2 / 3);
                double anchor = begin - Math.PI + (cue < 2 ? 0 : Math.PI * 2 / 3);
                for (int f = 0; f < 4; f++)
                {
                    JsonElement raw = samples[first + f - 1];
                    double progress = raw.GetProperty("heading_radians").GetDouble() - samples[945].GetProperty("heading_radians").GetDouble();
                    Transform3D[] target = submitted[cue].Target[f];
                    double actualYaw = Math.Atan2(target[0].Basis.Z.X, target[0].Basis.Z.Z);
                    Assert.InRange(Math.Abs(Math.IEEERemainder(actualYaw - anchor - (allocation * progress / span), Math.Tau)), 0, 1e-5);
                    for (int j = 1; j < 77; j++)
                    {
                        float[] row = [.. raw.GetProperty("pose")[j].EnumerateArray().Select(v => v.GetSingle())];
                        var expected = new Transform3D(new Basis(new Quaternion(row[3], row[4], row[5], row[6])), new Vector3(row[0], row[1], row[2]));
                        Assert.True(expected.IsEqualApprox(target[j]));
                    }
                }
            }
            _ = Assert.Single(nav.TerminalOutcomes);
            Assert.Equal("settled-idle", nav.Phase);
        }
        finally
        {
            session.Release.Set();
            tree.NodeAdded -= Configure;
            Task? completion = nav?.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        Assert.True(session.Disposed);
    }
}
