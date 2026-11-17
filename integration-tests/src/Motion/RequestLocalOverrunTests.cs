using System.Collections.Concurrent;
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
    private sealed class RequestLocalSession(bool providerError, ConcurrentQueue<long> returned) : SomaInferenceWorker.ISession
    {
        public readonly ManualResetEventSlim FirstRelease = new(false);
        private int _calls;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            if (Interlocked.Increment(ref _calls) == 1 && !FirstRelease.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Test-owned first-return gate exceeded its bound.");
            }
            returned.Enqueue(1);
            if (providerError)
            {
                throw new InvalidOperationException("request-local-fixture-provider-error");
            }
            // Each future is newly constructed from this request, never an archived future for a changed context.
            return [.. Enumerable.Range(0, 50).Select(i => context[3].Select((t, j) =>
                t.InterpolateWith(target[0][j], i / 49f)).ToArray())];
        }
        public void Dispose() => FirstRelease.Dispose();
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task TemporalOverrun_AllRecordedLaterCommandsRemainUsableThroughOriginalTail() => RunRequestLocalOverrunAsync(false);

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task TemporalOverrun_TrueProviderErrorStillLatchesAcrossRecordedCommands() => RunRequestLocalOverrunAsync(true);

    private static async Task RunRequestLocalOverrunAsync(bool providerError)
    {
        const string sourceFolder = "res://temp/motionbricks/nav-overrun-request-local-01/";
        string folder = $"res://temp/motionbricks/anim002-regenerated-source-adoption-01/overrun-{Guid.NewGuid():N}/";
        _ = Directory.CreateDirectory(ProjectSettings.GlobalizePath(folder));
        using var scenario = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(sourceFolder + "original-scenario.json")));
        JsonElement original = scenario.RootElement;
        double failureTime = original.GetProperty("failureNavTime").GetDouble();
        int failedTick = original.GetProperty("failedTick").GetInt32();
        int tailTicks = original.GetProperty("lastTick").GetInt32() - failedTick;
        JsonElement[] commands = [.. original.GetProperty("laterCommands").EnumerateArray()];
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "RequestLocalOverrun" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        MotionBricksNavigation? nav = null;
        var returned = new ConcurrentQueue<long>();
        var session = new RequestLocalSession(providerError, returned);
        var submissions = new List<long>();
        var consumed = new List<long>();
        var played = new HashSet<long>();
        var handoffs = new List<MotionBricksNavigation.NativeHandoffObservation>();
        var attempts = new List<object>();
        bool idleAccepted = false;
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }
            nav = n;
            n.SessionFactory = () => session;
            n.WindowSubmitted += (_, _, _) => submissions.Add(n.CurrentStatus.ActiveRequest?.RequestID ?? 0);
            n.PlaybackObserved = (ticket, _, _) => played.Add(ticket);
            n.NativeHandoffObserved = h =>
            {
                handoffs.Add(h);
                if (h.Accepted)
                {
                    consumed.Add(n.CurrentStatus.ActiveRequest?.RequestID ?? 0);
                    idleAccepted |= !h.Arrival && n.CurrentStatus.ActiveRequest is null;
                }
            };
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40); // Existing scene/nav-map and skin-history prerequisite.
            Assert.NotNull(nav);
            nav.SetPhysicsProcess(false); // One explicit production tick per observed engine physics boundary below.
            byte[] bytes = File.ReadAllBytes(ProjectSettings.GlobalizePath(sourceFolder + "original-boundary-context.f32"));
            float[] values = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            Transform3D[][] context = [.. Enumerable.Range(0, 4).Select(f => Enumerable.Range(0, 77)
                .Select(j => FeedbackTransform(values, ((f * 77) + j) * 12)).ToArray())];
            var history = new RealisedPoseHistory(77);
            for (int i = 0; i < 8; i++)
            {
                history.Record(failureTime - .3 + (i / 30.0), context[Math.Min(i, 3)]);
            }
            void Field(string name, object? value)
            {
                typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, value);
            }

            object? Get(string name)
            {
                return typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav);
            }

            void Time(double value)
            {
                typeof(MotionBricksNavigation).GetProperty(nameof(MotionBricksNavigation.ObservationTime), BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, value);
            }

            Field("_history", history);
            Field("_pose", (Transform3D[])context[3].Clone());
            Field("_observed", (Transform3D[])context[3].Clone());
            Field("_lastGround", new Vector3(context[3][0].Origin.X, 0, context[3][0].Origin.Z));
            actor.GlobalPosition = new Vector3(original.GetProperty("actorOrigin")[0].GetSingle(), 0,
                original.GetProperty("actorOrigin")[2].GetSingle());
            Time(failureTime - .1);
            float[] first = [.. original.GetProperty("initialCommand").GetProperty("requested").EnumerateArray().Select(e => e.GetSingle())];
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(FeedbackTransform(first, 0), []));
            nav.SetPhysicsProcess(false); // AcceptDestination enables processing; this fixture owns each explicit tick.
            nav._PhysicsProcess(1.0 / 60);
            _ = Assert.Single(submissions);
            object packet = Get("_nativePending")!;
            ConstructorInfo constructor = packet.GetType().GetConstructors().Single(c => c.GetParameters().Any(p => p.Name == "Overlap"));
            object?[] arguments = [.. constructor.GetParameters().Select(parameter => parameter.Name switch
            {
                "Overlap" => true,
                "ContextAt" => failureTime - original.GetProperty("turnBoundary").GetProperty("handoff").GetProperty("Age").GetDouble(),
                _ => packet.GetType().GetProperty(parameter.Name!)!.GetValue(packet)
            })];
            Field("_nativePending", constructor.Invoke(arguments));
            Field("_segment", null); // Recorded outgoing endpoint: OldRemaining=0, not a generated scheduling claim.
            Field("_endpoint", false);
            Field("_record", false); // Keep the recorded initial realised context until the imposed expiry is consumed.
            Time(failureTime - (1.0 / 60));
            session.FirstRelease.Set();
            var worker = (SomaInferenceWorker)Get("_worker")!;
            bool Published()
            {
                return typeof(SomaInferenceWorker).GetField("_result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker) is not null;
            }

            for (int i = 0; i < 120 && !Published(); i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1); // Worker publication only; no generated motion is advanced.
            }
            Assert.True(Published(), "The synthetic result must be published before imposing the recorded consumption boundary.");
            Time(failureTime - (1.0 / 60));
            nav._PhysicsProcess(1.0 / 60);
            long expiredTicket = (long)packet.GetType().GetProperty("Ticket")!.GetValue(packet)!;
            if (!providerError && handoffs.Count == 0)
            {
                File.WriteAllText(ProjectSettings.GlobalizePath(folder + "fixture-boundary-diagnostic.json"), JsonSerializer.Serialize(new
                {
                    packet,
                    nav.RoleDecision,
                    nav.SubmittedRole,
                    route = typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav),
                    roleRevision = Get("_roleRevision"),
                    pending = Get("_pending"),
                    packetNow = Get("_nativePending"),
                    nav.Failure,
                    nav.Phase,
                    returned = returned.Count
                }));
            }
            if (!providerError)
            {
                Assert.Equal(original.GetProperty("turnBoundary").GetProperty("handoff").GetProperty("Age").GetDouble(),
                    Assert.Single(handoffs).Age, 8);
            }
            for (int tick = 1; tick <= tailTicks; tick++)
            {
                foreach (JsonElement command in commands.Where(c => c.GetProperty("tick").GetInt32() - failedTick == tick))
                {
                    float[] transform = [.. command.GetProperty("requested").EnumerateArray().Select(e => e.GetSingle())];
                    int result = nav.SelectDestination(FeedbackTransform(transform, 0), []);
                    nav.SetPhysicsProcess(false);
                    attempts.Add(new
                    {
                        tick,
                        result,
                        active = nav.CurrentStatus.ActiveRequest
                    });
                }
                nav._PhysicsProcess(1.0 / 60);
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            bool unavailable = (bool)Get("_unavailable")!;
            File.WriteAllText(ProjectSettings.GlobalizePath(folder + (providerError ? "provider-result.json" : "component-result.json")),
                JsonSerializer.Serialize(new
                {
                    providerError,
                    tailTicks,
                    attempts,
                    submissions,
                    consumed,
                    played,
                    idleAccepted,
                    unavailable,
                    nav.Failure,
                    nav.Phase,
                    handoffs = handoffs.Select(h => new { h.Request, h.Accepted, h.Reason, h.Age, h.OldRemaining })
                }));
            Assert.Equal(3, attempts.Count);
            if (providerError)
            {
                Assert.True(unavailable);
                Assert.Contains("request-local-fixture-provider-error", nav.Failure);
                _ = Assert.Single(submissions);
                Assert.Empty(consumed);
                Assert.Equal(4, nav.TerminalOutcomes.Count);
            }
            else
            {
                Assert.Contains(handoffs, h => h.Request == expiredTicket && !h.Accepted && h.Reason == "native-overrun-safe-hold");
                Assert.DoesNotContain(expiredTicket, played);
                Assert.False(unavailable, "A valid temporal expiry must not latch lifetime MotionUnavailable across all later commands.");
                Assert.True(idleAccepted, "The existing generated-idle path must consume an owned fresh result.");
                foreach (long request in new long[] { 2, 3, 4 })
                {
                    Assert.Contains(request, submissions);
                    Assert.Contains(request, consumed);
                }
            }
        }
        finally
        {
            session.FirstRelease.Set();
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
