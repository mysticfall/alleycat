using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Focused integration coverage for the NAV-001 direct-transform baseline implementation.
/// </summary>
public sealed partial class DirectTransformNavigationIntegrationTests
{
    private const float PositionTolerance = 0.05f;
    private const float BasisTolerance = 0.0001f;

    /// <summary>
    /// Verifies finite transform destinations are accepted and non-finite transforms are rejected without replacing the destination.
    /// </summary>
    [Headless]
    [Fact]
    public async Task SetDestination_FiniteAndNonFiniteTransforms_ReturnsExpectedResults()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useExplicitTarget: true);

        try
        {
            var finiteDestination = new Transform3D(new Basis(Vector3.Up, 0.25f), new Vector3(1f, 0f, 1f));
            NavigationDestinationResult accepted = rig.Navigation.SetDestination(finiteDestination);

            Assert.Equal(NavigationDestinationResult.Accepted, accepted);
            Assert.True(rig.Navigation.HasDestination);
            AssertTransformClose(finiteDestination, rig.Navigation.Destination);

            var invalidDestination = new Transform3D(Basis.Identity, new Vector3(float.NaN, 0f, 0f));
            NavigationDestinationResult invalid = rig.Navigation.SetDestination(invalidDestination);

            Assert.Equal(NavigationDestinationResult.Invalid, invalid);
            Assert.True(rig.Navigation.HasDestination);
            AssertTransformClose(finiteDestination, rig.Navigation.Destination);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies clearing an accepted destination resets destination and running state consistently.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ClearDestination_AfterAcceptedDestination_StopsNavigationState()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useExplicitTarget: true);

        try
        {
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(1f, 0f, 1f))));
            Assert.True(rig.Navigation.HasDestination);

            rig.Navigation.ClearDestination();

            Assert.False(rig.Navigation.HasDestination);
            Assert.False(rig.Navigation.IsNavigationRunning);
            Assert.True(((INavigation)rig.Navigation).IsNavigationFinished);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the direct implementation applies reached-destination updates to the explicitly configured ancestor target node.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DirectTransformNavigation_WithExplicitTarget_MovesConfiguredNode()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useExplicitTarget: true);

        try
        {
            Basis parentStart = rig.Parent.GlobalTransform.Basis;
            var finalBasis = new Basis(Vector3.Up, 0.4f);
            var target = new Transform3D(finalBasis, rig.MovedTarget.GlobalPosition);

            Assert.Same(rig.MovedTarget, rig.Navigation.Target);
            Assert.True(IsAncestorOf(rig.MovedTarget, rig.Navigation), "Explicit target should be an ancestor so the NavigationAgent3D transform follows it.");
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(target));
            AssertBasisClose(finalBasis, rig.Navigation.Destination.Basis);
            Assert.True(rig.Navigation.ApplyFinalOrientationIfDirectDestinationReached());
            rig.MovedTarget.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(target.Origin, rig.MovedTarget.GlobalPosition, PositionTolerance);
            AssertBasisClose(finalBasis, rig.MovedTarget.Basis);
            AssertBasisClose(parentStart, rig.Parent.GlobalTransform.Basis);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the direct implementation falls back to applying reached-destination updates to its closest Node3D ancestor when no target is configured.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DirectTransformNavigation_WithoutTarget_FallsBackToClosestNode3DAncestor()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useExplicitTarget: false, useIntermediateNode: true);

        try
        {
            var finalBasis = new Basis(Vector3.Up, -0.35f);
            var target = new Transform3D(finalBasis, rig.Parent.GlobalPosition);

            Assert.Null(rig.Navigation.Target);
            Assert.NotSame(rig.Parent, rig.Navigation.GetParent());
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(target));
            AssertBasisClose(finalBasis, rig.Navigation.Destination.Basis);
            Assert.True(rig.Navigation.ApplyFinalOrientationIfDirectDestinationReached());
            rig.Parent.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(target.Origin, rig.Parent.GlobalPosition, PositionTolerance);
            AssertBasisClose(finalBasis, rig.Parent.Basis);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies final facing intent is applied to the moved target once the requested destination is reached.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DirectTransformNavigation_WhenDestinationReached_AppliesFinalOrientation()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useExplicitTarget: true);

        try
        {
            var finalBasis = new Basis(Vector3.Up, 0.75f);
            var target = new Transform3D(finalBasis, rig.MovedTarget.GlobalPosition);

            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(target));
            AssertBasisClose(finalBasis, rig.Navigation.Destination.Basis);
            Assert.True(rig.Navigation.ApplyFinalOrientationIfDirectDestinationReached());
            rig.MovedTarget.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(target.Origin, rig.MovedTarget.GlobalPosition, PositionTolerance);
            AssertBasisClose(finalBasis, rig.MovedTarget.Basis);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies final facing intent remains world-space when the moved node has a transformed parent.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DirectTransformNavigation_WithExplicitTargetUnderTransformedParent_AppliesWorldFinalOrientation()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useExplicitTarget: true);

        try
        {
            Node3D targetParent = new()
            {
                Name = "TransformedTargetParent",
            };

            rig.Root.AddChild(targetParent);
            targetParent.Basis = Basis.FromScale(new Vector3(1.5f, 1.0f, 0.75f)) * new Basis(Vector3.Up, 0.65f);
            targetParent.Position = new Vector3(0.35f, 0.1f, -0.25f);
            targetParent.ForceUpdateTransform();
            Assert.False(IsBasisClose(Basis.Identity, targetParent.Basis), "Regression fixture parent must have a non-identity local basis.");
            rig.Parent.RemoveChild(rig.MovedTarget);
            targetParent.AddChild(rig.MovedTarget);
            targetParent.ForceUpdateTransform();
            rig.MovedTarget.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);
            Assert.Same(targetParent, rig.MovedTarget.GetParent());

            rig.MovedTarget.GlobalPosition = new Vector3(0.8f, 0f, 0.6f);
            rig.MovedTarget.ForceUpdateTransform();

            Basis finalBasis = Basis.Identity;
            var target = new Transform3D(finalBasis, rig.MovedTarget.GlobalPosition);

            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(target));
            Assert.True(rig.Navigation.ApplyFinalOrientationIfDirectDestinationReached());
            rig.MovedTarget.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(target.Origin, rig.MovedTarget.GlobalPosition, PositionTolerance);
            Assert.False(IsBasisClose(finalBasis, rig.MovedTarget.Basis), "Regression fixture must require a parent-space conversion.");
            AssertBasisClose(finalBasis, rig.MovedTarget.GlobalTransform.Basis);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    private static async Task<NavigationTestRig> CreateRigAsync(SceneTree sceneTree, bool useExplicitTarget, bool useIntermediateNode = false)
    {
        Node3D root = new()
        {
            Name = "NavigationTestRoot",
        };
        NavigationRegion3D region = CreateNavigationRegion();
        Node3D parent = new()
        {
            Name = "Parent",
            GlobalPosition = new Vector3(0.2f, 0f, 0.2f),
        };
        Node3D movedTarget = new()
        {
            Name = "ExplicitTarget",
            GlobalPosition = new Vector3(0.2f, 0f, 0.2f),
        };
        DirectTransformNavigation navigation = new()
        {
            Name = "Navigation",
            MovementSpeed = 100f,
            DestinationReachedDistance = 0.05f,
            PathDesiredDistance = 0.05f,
            Target = useExplicitTarget ? movedTarget : null,
        };

        root.AddChild(region);
        root.AddChild(parent);
        if (useExplicitTarget)
        {
            parent.AddChild(movedTarget);
            movedTarget.AddChild(navigation);
        }
        else if (useIntermediateNode)
        {
            Node intermediate = new()
            {
                Name = "IntermediateNode",
            };
            parent.AddChild(intermediate);
            intermediate.AddChild(navigation);
        }
        else
        {
            parent.AddChild(navigation);
            root.AddChild(movedTarget);
        }
        sceneTree.Root.AddChild(root);

        parent.GlobalPosition = new Vector3(0.2f, 0f, 0.2f);
        movedTarget.GlobalPosition = new Vector3(0.2f, 0f, 0.2f);

        await WaitForPhysicsFramesAsync(sceneTree, 5);

        return new NavigationTestRig(root, parent, movedTarget, navigation);
    }

    private static async Task DestroyRigAsync(SceneTree sceneTree, NavigationTestRig rig)
    {
        rig.Root.QueueFree();
        await WaitForNextFrameAsync(sceneTree);
    }

    private static NavigationRegion3D CreateNavigationRegion()
    {
        NavigationMesh mesh = new();
        mesh.SetVertices([
            new Vector3(-1f, 0f, -1f),
            new Vector3(4f, 0f, -1f),
            new Vector3(4f, 0f, 4f),
            new Vector3(-1f, 0f, 4f),
        ]);
        mesh.AddPolygon([0, 1, 2]);
        mesh.AddPolygon([0, 2, 3]);

        return new NavigationRegion3D
        {
            Name = "NavigationRegion3D",
            NavigationMesh = mesh,
        };
    }

    private static void AssertTransformClose(Transform3D expected, Transform3D actual)
    {
        AssertVectorClose(expected.Origin, actual.Origin, BasisTolerance);
        AssertBasisClose(expected.Basis, actual.Basis);
    }

    private static void AssertBasisClose(Basis expected, Basis actual)
    {
        AssertVectorClose(expected.Column0, actual.Column0, BasisTolerance);
        AssertVectorClose(expected.Column1, actual.Column1, BasisTolerance);
        AssertVectorClose(expected.Column2, actual.Column2, BasisTolerance);
    }

    private static bool IsBasisClose(Basis expected, Basis actual)
        => expected.Column0.DistanceTo(actual.Column0) <= BasisTolerance
            && expected.Column1.DistanceTo(actual.Column1) <= BasisTolerance
            && expected.Column2.DistanceTo(actual.Column2) <= BasisTolerance;

    private static bool IsAncestorOf(Node expectedAncestor, Node node)
    {
        Node? parent = node.GetParent();
        while (parent is not null)
        {
            if (ReferenceEquals(expectedAncestor, parent))
            {
                return true;
            }

            parent = parent.GetParent();
        }

        return false;
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(
            expected.DistanceTo(actual) <= tolerance,
            $"Expected {actual} to be within {tolerance} of {expected}.");
    }

    private sealed record NavigationTestRig(
        Node3D Root,
        Node3D Parent,
        Node3D MovedTarget,
        DirectTransformNavigation Navigation);
}
