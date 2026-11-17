using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Nearby rear positioning retains the selected source, rather than becoming routine forward routing.</summary>
    [Headless]
    [Fact]
    public async Task SourceRateCurve_NearRearGoalSelectsBackwardPositioning()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        MotionBricksNavigation? nav = null;
        Transform3D[][]? submitted = null;
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }
            nav = n;
            n.SessionFactory = () => new TargetEndpointSession(true);
            n.WindowSubmitted += (_, _, target) => { submitted = target; n.SetPhysicsProcess(false); };
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            Vector3 start = actor.GlobalPosition, goal = start + new Vector3(0, 0, .4f);
            Assert.Equal(0, nav.SelectDestination(new(new Basis(Vector3.Up, .022989562f), goal), []));
            for (int i = 0; i < 90 && submitted is null; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal("", nav.Failure);
            Assert.NotNull(submitted);
            Assert.Equal("backward", nav.ReviewedRole);
            Assert.True(((SomaMotionData.ModelToWorld * submitted[0][0].Origin) - start).Dot(goal - start) > 0);
            var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
            var sampler = new ReviewedRoleSampler(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/set.json"), data);
            _ = sampler.SetIntent("backward", nav.ObservationTime);
            Transform3D[][] source = sampler.Targets(nav.ObservationTime, 49.0 / 30, Basis.Identity, Vector3.Zero);
            for (int i = 0; i < 4; i++)
            {
                for (int j = 1; j < data.Names.Length; j++)
                {
                    Assert.Equal(source[i][j], submitted[i][j]);
                }
            }
        }
        finally
        {
            tree.NodeAdded -= Configure;
            nav?.SetPhysicsProcess(false);
            await root.DrainMotionAndDiagnosticsAsync();
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }
}
