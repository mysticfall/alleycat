using System.Reflection;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>An ownership-integrity fault remains latched across a successor request.</summary>
    [Headless]
    [Fact]
    public async Task IncomingPacketIntegrityFaultRemainsLatched()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        var session = new LinearHandoffSession(false);
        MotionBricksNavigation? nav = null;
        int submissions = 0;
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }
            nav = n;
            n.SessionFactory = () => session;
            n.WindowSubmitted += (_, context, _) =>
            {
                submissions++;
                var history = (RealisedPoseHistory)typeof(MotionBricksNavigation)
                    .GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(n)!;
                Assert.True(context.Zip(history.Context()).All(p => p.First.SequenceEqual(p.Second)));
                if (submissions == 2)
                {
                    FieldInfo revision = typeof(MotionBricksNavigation).GetField("_nativeRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    revision.SetValue(n, (long)revision.GetValue(n)! + 1);
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
            _ = nav.SelectDestination(new(Basis.Identity, actor.GlobalPosition + new Vector3(0, 0, -4)), []);
            for (int tick = 0; tick < 600 && nav.TerminalOutcomes.Count == 0; tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal(NavigationTerminalReason.MotionUnavailable, Assert.Single(nav.TerminalOutcomes).Reason);
            Assert.Contains("Invalidated native handoff packet escaped ownership", nav.Failure);
            _ = nav.SelectDestination(new(Basis.Identity, actor.GlobalPosition + new Vector3(0, 0, -.4f)), []);
            for (int tick = 0; tick < 120 && nav.TerminalOutcomes.Count < 2; tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal(2, nav.TerminalOutcomes.Count);
            Assert.Equal(NavigationTerminalReason.MotionUnavailable, nav.TerminalOutcomes[1].Reason);
            Assert.Equal("failed", nav.Phase);
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
