using System.Reflection;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <inheritdoc/>
public sealed class NativePathSamplingTests
{
    /// <summary>Distance ahead walks ordered native edges, including a corner, rather than drawing a chord or arc.</summary>
    [Headless]
    [Fact]
    public void DistanceAheadTraversesNativeCornerAndKeepsStraightPacing()
    {
        Vector3[] path = [Vector3.Zero, new(1, 0, 0), new(1, 0, 2)];
        NativePathMotionTargets.Sample sample = NativePathMotionTargets.At(path, 1, new(.2f, 0, .05f), 1.3f);
        Assert.True(sample.Position.IsEqualApprox(new(1, 0, .5f)));
        Assert.Equal(Vector3.Back, sample.Direction);
        Assert.False(sample.AtEnd);
        const float speed = .5784675478935242f;
        sample = NativePathMotionTargets.At([Vector3.Zero, new(10, 0, 0)], 1, Vector3.Zero, speed * (49f / 30));
        Assert.Equal(speed * (49f / 30), sample.Position.X);
        Assert.Equal(0, sample.Position.Z);
    }

    /// <summary>The recorded near-corner actual root samples the native outgoing edge instead of an inward fillet.</summary>
    [Headless]
    [Fact]
    public void RecordedNearCornerSamplesNativeOutgoingEdge()
    {
        string source = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-playtests/20261110T033217788Z-166a727ca4bb4be09ec1709c71596d4f/");
        JsonElement r = NavigationSessionReader.ReadCheckpoint(source).Single(e => e.Event.GetProperty("sequence").GetInt64() == 1836).Event.GetProperty("data").GetProperty("data");
        Vector3[] path = [.. r.GetProperty("route").GetProperty("points").EnumerateArray().Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()))];
        JsonElement p = r.GetProperty("actorTransform");
        Vector3 actual = new(p[9].GetSingle(), 0, p[11].GetSingle());
        Vector3 edge = path[4] - path[3];
        Vector3 projected = path[3] + (edge * ((actual - path[3]).Dot(edge) / edge.LengthSquared()));
        float distance = r.GetProperty("intent").GetProperty("Speed").GetSingle() * (49f / 30);
        float remainingAfterCorner = distance - projected.DistanceTo(path[4]);
        Assert.True(remainingAfterCorner > 0);
        NativePathMotionTargets.Sample sample = NativePathMotionTargets.At(path, 4, actual, distance);
        Vector3 expected = path[4] + ((path[5] - path[4]).Normalized() * remainingAfterCorner);
        Assert.True(expected.IsEqualApprox(sample.Position));
        Assert.True(sample.Position.X >= path[4].X);
    }

    /// <summary>A minimal agent fixture supplies synthetic parent positions solely to observe native via completion.</summary>
    [Headless]
    [Fact]
    public async Task NativeAgentViaProgressUsesNativeCompletion()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        NavigationRegion3D region = scene.GetNode<NavigationRegion3D>("NavigationRegion3D");
        scene.RemoveChild(region);
        scene.Free();
        var root = new Node3D();
        root.AddChild(region);
        var parent = new Node3D { Position = new(-2, 0, -2) };
        var nav = new ReachabilityOnlyNavigation { NavigationLayers = 1, PathDesiredDistance = .1f, TargetDesiredDistance = .1f };
        parent.AddChild(nav);
        root.AddChild(parent);
        tree.Root.AddChild(root);
        Rid map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(map, true);
        region.SetNavigationMap(map);
        nav.SetNavigationMap(map);
        Vector3 via = new(-1, 0, -2), goal = new(0, 0, -2);
        try
        {
            for (int i = 0; i < 60 && NavigationServer3D.MapGetClosestPointOwner(map, parent.Position) != region.GetRid(); i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal(NavigationDestinationResult.Accepted, nav.SetDestination(new(Basis.Identity, goal), [via]));
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            _ = nav.Poll(parent.GlobalTransform);
            Assert.Equal(via, nav.TargetPosition);
            Assert.False((bool)typeof(NavigationBase).GetProperty("NativeDestinationLeg", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!);
            Assert.Equal(nav.GetCurrentNavigationPathIndex(), nav.LastSampledPathIndex);
            parent.Position = via; // Minimal native API input, not main-actor/generated-feedback advancement.
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            _ = nav.Poll(parent.GlobalTransform);
            Assert.True(nav.IsTargetReached());
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            _ = nav.Poll(parent.GlobalTransform);
            Assert.Equal(goal, nav.TargetPosition);
            Assert.True((bool)typeof(NavigationBase).GetProperty("NativeDestinationLeg", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!);
            Assert.True(nav.ViaPointsPassed[0]);
            Assert.Equal(nav.GetCurrentNavigationPathIndex(), nav.LastSampledPathIndex);
            Assert.Equal(0, nav.Requests);
            Assert.Null(nav.WorkerCompletion);
        }
        finally { root.QueueFree(); await TestUtils.WaitForPhysicsFramesAsync(tree, 2); NavigationServer3D.FreeRid(map); }
    }
}
