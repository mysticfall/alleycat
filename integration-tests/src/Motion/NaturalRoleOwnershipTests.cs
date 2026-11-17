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
    /// <summary>Current semantic/source ownership rejects incompatible work; replacement selects anew from actual pose.</summary>
    [Headless]
    [Fact]
    public async Task NaturalRoleSnapshotRejectsStaleSourceAndReplacementDoesNotWaitForCycle()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "NaturalRoleOwnership" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Position = new(-2, 0, -2);
        MotionBricksNavigation? nav = null;
        var provider = new LifecycleProviderState();
        var snapshots = new List<MotionBricksNavigation.RoleSubmission>();
        var accepted = new List<long>();
        long stale = 0;
        bool discardObserved = false;
        T Field<T>(string name)
        {
            return (T)typeof(MotionBricksNavigation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
        }

        float Heading()
        {
            return (float)typeof(MotionBricksNavigation).GetMethod("AnatomicalHeading", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(nav, [Field<RealisedPoseHistory>("_history").Context()[3]])! + Mathf.Pi;
        }

        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }

            nav = n;
            n.SessionFactory = () => new LifecycleSession(provider, false);
            n.NativeHandoffObserved = h => { if (h.Accepted) { accepted.Add(h.Request); } };
            n.PoseObserved += () => { if (snapshots.Count == 1 && Field<bool>("_needRequest") && !Field<bool>("_pending")) { discardObserved = true; } };
            n.WindowSubmitted += (ticket, context, _) =>
            {
                MotionBricksNavigation.RoleSubmission snapshot = Assert.IsType<MotionBricksNavigation.RoleSubmission>(n.SubmittedRole);
                snapshots.Add(snapshot);
                Assert.Equal("feminine/normal/" + snapshot.SelectedRole, snapshot.Source.Key);
                Transform3D[][] actual = Field<RealisedPoseHistory>("_history").Context();
                Assert.True(context.Zip(actual).All(p => p.First.SequenceEqual(p.Second)));
                if (snapshots.Count == 1)
                {
                    stale = ticket;
                    object packet = Field<object>("_nativePending");
                    // Technical negative only. The frozen symptom regression never mutates ownership or target state.
                    packet.GetType().GetProperty("Role")!.SetValue(packet, snapshot with
                    {
                        SelectedRole = "right_noncross"
                    });
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
            float heading = Heading();
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(new(new Basis(Vector3.Up, heading - Mathf.Pi),
                actor.GlobalPosition + new Vector3(Mathf.Cos(heading), 0, -Mathf.Sin(heading))), []));
            for (int tick = 0; tick < 180 && accepted.Count == 0; tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            Assert.NotEmpty(accepted);
            Assert.DoesNotContain(stale, accepted);
            Assert.True(discardObserved);
            Assert.Equal("left_noncross", snapshots[0].SelectedRole);
            Assert.Equal(snapshots[0].Source.Revision, snapshots[1].Source.Revision);
            Assert.True(snapshots[1].Source.SourceFrame >= snapshots[0].Source.SourceFrame);
            Assert.True(nav.HasDestination);
            Assert.Empty(nav.TerminalOutcomes);
            int before = snapshots.Count;
            heading = Heading();
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(new(new Basis(Vector3.Up, heading - Mathf.Pi),
                actor.GlobalPosition - (.6f * new Vector3(Mathf.Sin(heading), 0, Mathf.Cos(heading)))), []));
            for (int tick = 0; tick < 90 && snapshots.Count == before; tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }

            Assert.True(snapshots.Count > before);
            Assert.Equal("backward", snapshots[before].SelectedRole);
            Assert.NotEqual(snapshots[1].Generation, snapshots[before].Generation);
            Assert.True(snapshots[before].Source.Revision > snapshots[1].Source.Revision);
            Assert.False(Field<bool>("_unavailable"));
            Assert.Equal(0, provider.Faults);
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
