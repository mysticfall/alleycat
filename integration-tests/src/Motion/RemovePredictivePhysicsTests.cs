using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>An external obstacle absent from NAV is handled by actual engine movement, not prediction.</summary>
    [Headless]
    [Fact]
    public async Task NpcActualCollisionStillClampsAndAcceptsSuccessor()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        DeactivateNavigationRecording(scene);
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Position = new(-2, 0, -2);
        var wall = new StaticBody3D { Position = new(-2, .5f, -2.6f) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new(2, 1, .2f) } });
        scene.AddChild(wall);
        MotionBricksNavigation? nav = null;
        float minimumZ = -2;
        void Observe()
        {
            minimumZ = Math.Min(minimumZ, actor.GlobalPosition.Z);
        }

        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation value)
            {
                return;
            }
            nav = value;
            value.SessionFactory = () => new TargetEndpointSession(true);
            value.PoseObserved += Observe;
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            // Jolt contact and realised skeleton feedback are deliberately engine-ticked.
            for (int i = 0; i < 40 && (nav?.SamplesRecorded ?? 0) < 8; i++)
            {
                await TestUtils.WaitForNextPhysicsFrameAsync(tree);
            }
            Assert.NotNull(nav);
            Assert.True(nav.SamplesRecorded >= 8);
            Assert.Equal(0, scene.Call("_submit_destination", new Transform3D(Basis.Identity, new(-2, 0, -3.5f)), Array.Empty<Vector3>()).AsInt32());
            for (int i = 0; i < 360 && (nav.TerminalOutcomes.Count == 0 || nav.Phase != "settled-idle"); i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Contains("Generated root encountered a static obstruction", nav.Failure);
            Assert.Equal("settled-idle", nav.Phase);
            Assert.Equal(NavigationTerminalReason.ConvergenceFailed, Assert.Single(nav.TerminalOutcomes).Reason);
            var sphere = (SphereShape3D)actor.GetNode<CollisionShape3D>("CollisionShape3D").Shape;
            float slop = ProjectSettings.GetSetting("physics/jolt_physics_3d/simulation/penetration_slop").AsSingle();
            float front = wall.Position.Z + .1f;
            Assert.True(minimumZ < -2.1f);
            Assert.True(minimumZ - sphere.Radius >= front - slop);
            int requests = nav.Requests;
            Assert.Equal(0, scene.Call("_submit_destination", new Transform3D(Basis.Identity, actor.GlobalPosition + new Vector3(.3f, 0, .15f)), Array.Empty<Vector3>()).AsInt32());
            for (int i = 0; i < 60 && nav.Requests == requests; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.True(nav.Requests > requests);
            Assert.Equal("", nav.Failure);
        }
        finally
        {
            tree.NodeAdded -= Configure;
            if (nav is not null)
            {
                nav.SetPhysicsProcess(false);
                nav.PoseObserved -= Observe;
            }
            await root.DrainMotionAndDiagnosticsAsync();
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }

}
