using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>A superseded placement revision cannot install its result or rewind source time.</summary>
    [Headless]
    [Fact]
    public async Task StaleRoutePlacementIsDiscardedWithoutPoisoningCurrentRequest()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Position = new(-2.1635602f, 0, -.1687011f);
        MotionBricksNavigation? nav = null;
        var state = new LifecycleProviderState();
        var observations = new List<MotionBricksNavigation.NativeHandoffObservation>();
        var sourceFrames = new List<double>();
        long staleTicket = 0;
        bool staleTickObserved = false;
        T Field<T>(string name)
        {
            return (T)typeof(MotionBricksNavigation).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(nav)!;
        }

        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }

            nav = n;
            n.SessionFactory = () => new LifecycleSession(state, false);
            n.NativeHandoffObserved = observations.Add;
            n.PoseObserved += () =>
            {
                if (sourceFrames.Count == 1 && Field<bool>("_needRequest") && !Field<bool>("_pending"))
                {
                    staleTickObserved = true;
                }
            };
            n.WindowSubmitted += (ticket, context, _) =>
            {
                sourceFrames.Add(Field<ReviewedRoleSampler>("_reviewed").Selection.SourceFrame);
                Transform3D[][] actual = Field<RealisedPoseHistory>("_history").Context();
                Assert.True(context.Zip(actual).All(p => p.First.SequenceEqual(p.Second)));
                if (sourceFrames.Count == 1)
                {
                    staleTicket = ticket;
                    object packet = Field<object>("_nativePending");
                    Assert.True((bool)packet.GetType().GetProperty("RouteGuided")!.GetValue(packet)!);
                    // Focused synthetic ownership-negative: no route/pose injection in the parent outcome regression.
                    packet.GetType().GetProperty("RouteRevision")!.SetValue(packet, -1L);
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
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(new(Basis.Identity, new(1.9308183f, 4.7683716e-7f, 3.0290458f)), []));
            for (int tick = 0; tick < 180 && observations.Count == 0; tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            Assert.Contains(observations, h => h.Accepted && h.Request != staleTicket);
            Assert.DoesNotContain(observations, h => h.Request == staleTicket);
            Assert.True(staleTickObserved, "Discard must retain normal displayed-pose application and observation for that physics tick.");
            Assert.True(sourceFrames.Count >= 2);
            Assert.True(sourceFrames[1] >= sourceFrames[0]);
            Assert.True(nav.HasDestination);
            Assert.Empty(nav.TerminalOutcomes);
            Assert.Equal("", nav.Failure);
            Assert.False(Field<bool>("_unavailable"));
            Assert.Equal(0, state.Faults);
            Assert.False(nav.WorkerCompletion!.IsCompleted);
            nav.ClearDestination();
        }
        finally
        {
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
