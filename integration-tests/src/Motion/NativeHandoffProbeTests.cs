using System.Collections.Concurrent;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class LinearHandoffSession(bool blockSecond) : SomaInferenceWorker.ISession
    {
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ConcurrentQueue<(Transform3D Start, Transform3D End)> Curves = new();
        private int _calls;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            int call = Interlocked.Increment(ref _calls);
            if (blockSecond && call == 2 && !Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Test-owned worker latch exceeded its bound.");
            }

            Curves.Enqueue((context[3][0], target[0][0]));
            var frames = new Transform3D[50][];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = (Transform3D[])context[3].Clone();
                // Synthetic constant root velocity; not a model-output or foot-quality claim.
                frames[i][0] = new Transform3D(context[3][0].Basis,
                    context[3][0].Origin.Lerp(target[0][0].Origin, i / 49f));
            }
            return frames;
        }
        public void Dispose() => Release.Dispose();
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task NativeHandoff_UsesActualContextAgeWithoutPosePlacement() => RunNativeHandoffFixture(false);

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task NativeHandoff_OverrunRefusesInsteadOfReplayingOrRetrying() => RunNativeHandoffFixture(true);

    private static async Task RunNativeHandoffFixture(bool overrun)
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "NativeHandoffProbeFixture" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character npc = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        var session = new LinearHandoffSession(overrun);
        MotionBricksNavigation? nav = null;
        var handoffs = new List<MotionBricksNavigation.NativeHandoffObservation>();
        var contexts = new List<Transform3D[][]>();
        var observed = new List<(double Time, Transform3D Root)>();
        int secondSubmittedTick = -1, ticks = 0;
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }

            nav = n;
            n.SessionFactory = () => session;
            n.NativeHandoffObserved = handoffs.Add;
            n.PoseObserved += () => observed.Add((n.ObservationTime, n.ObservedRoot));
            n.WindowSubmitted += (_, context, _) =>
            {
                contexts.Add(context);
                if (contexts.Count == 2)
                {
                    secondSubmittedTick = ticks;
                }

                if (observed.Count > 0)
                {
                    Assert.True(observed[^1].Root.Origin.IsEqualApprox(context[3][0].Origin));
                    Assert.True(observed[^1].Root.Basis.IsEqualApprox(context[3][0].Basis));
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
            _ = nav.SelectDestination(new Transform3D(new Basis(Vector3.Up, Mathf.Pi), npc.GlobalPosition + new Vector3(0, 0, 2)), []);
            for (; ticks < 240 && handoffs.Count < 2; ticks++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                if (overrun && secondSubmittedTick >= 0 && ticks - secondSubmittedTick >= 30)
                {
                    session.Release.Set();
                }
            }
            Assert.True(handoffs.Count >= 2);
            MotionBricksNavigation.NativeHandoffObservation h = handoffs[1];
            Assert.True(h.PublishedAt > 0 && h.PublishedAt <= h.ConsumedAt);
            Assert.True(h.Age > 0);
            Assert.NotEqual(h.PreviousRequest, h.Request);
            if (overrun)
            {
                Assert.False(h.Accepted);
                Assert.Equal("native-overrun-safe-hold", h.Reason);
                Assert.False(nav.HasDestination);
                Assert.Equal("native-overrun-safe-hold", nav.Failure);
                Assert.Equal(NavigationTerminalReason.ConvergenceFailed, Assert.Single(nav.TerminalOutcomes).Reason);
                Assert.Equal(2, nav.Requests);
            }
            else
            {
                Assert.True(h.Accepted, h.Reason);
                Assert.True(h.OldRemaining > 0);
                Assert.Equal("", nav.Failure);
                (Transform3D start, Transform3D end) = session.Curves.ToArray()[1];
                Vector3 expected = start.Origin + ((end.Origin - start.Origin) * (float)(h.Age / (49.0 / 30)));
                Assert.InRange(h.Incoming[0].Origin.DistanceTo(expected), 0, .00001f);
                for (int j = 1; j < h.Incoming.Length; j++)
                {
                    Assert.True(h.Incoming[j].Origin.IsEqualApprox(contexts[1][3][j].Origin));
                }

                Assert.True(h.Incoming[0].Origin.DistanceTo(start.Origin) > 0, "Not frame0 replay.");
            }
            nav.ClearDestination();
            Assert.False(nav.HasDestination);
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
    }
}
