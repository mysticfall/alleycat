using System.Reflection;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>A clear rearward arc keeps combined turning/walking; snapshots cannot outlive their request.</summary>
    [Fact]
    public async Task ClearRearwardArcKeepsForwardAndRouteQueryOwnership()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "ClearRearwardOwnership" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Position = new(-2, 0, -2);
        MotionBricksNavigation? nav = null;
        var roles = new List<string>();
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation value)
            {
                return;
            }
            nav = value;
            value.SessionFactory = () => new RecordingSession();
            value.WindowSubmitted += (ticket, context, targets) => roles.Add(value.SubmittedRole?.SelectedRole ?? "");
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            float heading = nav.RecordingAnatomicalHeading!.Value;
            Vector3 goal = actor.GlobalPosition - (new Vector3(Mathf.Sin(heading), 0, Mathf.Cos(heading)) * 2);
            Assert.Equal(0, nav.SelectDestination(new Transform3D(Basis.Identity, goal), []));
            for (int i = 0; i < 60 && roles.Count == 0; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.NotEmpty(roles);
            Assert.Equal("forward", roles[0]);
            var old = (NavigationRouteSnapshot)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
            Assert.True(nav.OwnsRoute(old));
            Assert.Equal(0, nav.SelectDestination(new Transform3D(Basis.Identity, goal + Vector3.Right), []));
            Assert.False(nav.OwnsRoute(old));
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            var current = (NavigationRouteSnapshot)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
            Assert.True(nav.OwnsRoute(current));
            var forged = new NavigationRouteSnapshot(current.PathPoints.ToArray(), current.ActivePathIndex, current.NextPathPoint,
                current.Destination, current.DestinationRequestGeneration, current.RouteRevision, false, false);
            PropertyInfo snapshot = typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            snapshot.SetValue(nav, forged);
            Assert.False(nav.OwnsRoute(forged)); // Even current numeric geometry cannot invent the query witness.
            var wrongQuery = new NavigationRouteSnapshot(current.PathPoints.ToArray(), current.ActivePathIndex, current.NextPathPoint,
                current.Destination, current.DestinationRequestGeneration, current.RouteRevision, false, false, old.Query);
            snapshot.SetValue(nav, wrongQuery);
            Assert.False(nav.OwnsRoute(wrongQuery));
            snapshot.SetValue(nav, current);
            Assert.True(nav.OwnsRoute(current));
            // An off-mesh coordinate can still have a native path to its nearest mesh point.
            // Query an unused layer to establish a real native-unreachable request, not a custom bounds oracle.
            uint layers = nav.NavigationLayers;
            nav.NavigationLayers = 1u << 31;
            Assert.Empty(NavigationServer3D.MapGetPath(nav.GetNavigationMap(), actor.GlobalPosition, new(50, 0, 50),
                true, nav.NavigationLayers));
            Assert.Equal((int)NavigationDestinationResult.Unreachable, nav.SelectDestination(new Transform3D(Basis.Identity, new(50, 0, 50)), []));
            nav.NavigationLayers = layers;
            Assert.True(nav.OwnsRoute(current));
        }
        finally
        {
            tree.NodeAdded -= Configure;
            Task? completion = nav?.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion;
            }
        }
    }
}
