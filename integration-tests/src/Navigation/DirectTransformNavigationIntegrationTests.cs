using AlleyCat.Navigation;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>Cheap direct-navigation boundary guards without replaying representative walks.</summary>
public sealed class DirectTransformNavigationIntegrationTests
{
    /// <summary>A request before map publication cannot commit intent or move its actor.</summary>
    [Fact]
    public void RequestBeforeMapSync_ReturnsNotReadyWithoutCommitOrActorMutation()
    {
        using var target = new Node3D();
        using var navigation = new DirectTransformNavigation { Target = target };
        Transform3D original = target.Transform;
        Assert.Equal(NavigationDestinationResult.NotReady, navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(2f, 0f, 0f))));
        navigation._PhysicsProcess(1d);
        Assert.False(navigation.HasDestination);
        Assert.Equal(original, target.Transform);
    }

    /// <summary>Invalid replacement preserves an accepted destination instead of partially committing it.</summary>
    [Fact]
    public Task SetDestination_InvalidTransformPreservesAcceptedDestinationAndIntent() => WithReadyRigAsync((target, navigation) =>
    {
        var destination = new Transform3D(Basis.Identity, new Vector3(2f, 0f, 0f));
        Assert.Equal(NavigationDestinationResult.Accepted, navigation.SetDestination(destination));
        NavigationMotionIntent before = navigation.Poll(target.GlobalTransform);
        Assert.Equal(NavigationDestinationResult.Invalid, navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(float.NaN, 0f, 0f))));
        Assert.Equal(destination, navigation.Destination);
        Assert.True(navigation.HasDestination);
        Assert.Equal(before, navigation.Poll(target.GlobalTransform));
    });

    /// <summary>Zero elapsed time cannot move an outstanding request.</summary>
    [Fact]
    public Task ZeroDelta_WithOutstandingIntent_DoesNotMutateLocalOrWorldTransform() => WithReadyRigAsync((target, navigation) =>
    {
        Assert.Equal(NavigationDestinationResult.Accepted, navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(2f, 0f, 0f))));
        Transform3D local = target.Transform;
        Transform3D world = target.GlobalTransform;
        navigation._PhysicsProcess(0d);
        Assert.Equal(local, target.Transform);
        Assert.Equal(world, target.GlobalTransform);
    });

    /// <summary>Already-complete requests cannot normalise or rewrite the actor transform.</summary>
    [Fact]
    public Task CompletedIntent_DoesNotMutateLocalOrWorldTransform() => WithReadyRigAsync((target, navigation) =>
    {
        Assert.Equal(NavigationDestinationResult.Accepted, navigation.SetDestination(target.GlobalTransform));
        Assert.True(navigation.Poll(target.GlobalTransform).IsComplete);
        Transform3D world = target.GlobalTransform;
        navigation._PhysicsProcess(1d);
        Assert.Equal(world, target.GlobalTransform);
    });

    private static async Task WithReadyRigAsync(Action<Node3D, DirectTransformNavigation> check)
    {
        SceneTree tree = GetSceneTree();
        var root = new Node3D();
        var mesh = new NavigationMesh
        {
            Vertices = [new Vector3(-5f, 0f, -5f), new Vector3(5f, 0f, -5f), new Vector3(5f, 0f, 5f), new Vector3(-5f, 0f, 5f)],
        };
        mesh.AddPolygon([0, 1, 2]);
        mesh.AddPolygon([0, 2, 3]);
        var region = new NavigationRegion3D { NavigationMesh = mesh };
        var target = new Node3D();
        var navigation = new DirectTransformNavigation { Target = target };
        Rid map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(map, true);
        region.SetNavigationMap(map);
        navigation.SetNavigationMap(map);
        root.AddChild(region);
        root.AddChild(target);
        target.AddChild(navigation);
        tree.Root.AddChild(root);
        navigation.SetPhysicsProcess(false);
        try
        {
            region.SetNavigationMap(map);
            navigation.SetNavigationMap(map);
            NavigationServer3D.AgentSetPosition(navigation.GetRid(), target.GlobalPosition);
            for (int tick = 0; tick < 30 && NavigationServer3D.MapGetPath(map, Vector3.Zero, new Vector3(2f, 0f, 0f), true).Length == 0; tick++)
            {
                await WaitForNextPhysicsFrameAsync(tree);
            }
            Assert.NotEmpty(NavigationServer3D.MapGetPath(map, Vector3.Zero, new Vector3(2f, 0f, 0f), true));
            check(target, navigation);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(tree, 1);
            NavigationServer3D.FreeRid(map);
        }
    }
}
