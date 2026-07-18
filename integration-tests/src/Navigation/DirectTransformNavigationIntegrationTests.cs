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
    private const float ControllerPositionTolerance = 0.08f;

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

    /// <summary>
    /// Verifies drag-to-release placement preserves the locked destination origin and applies final facing from the drag vector.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlaytestController_DragRelease_SetsDestinationWithSelectedFacing()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreatePlaytestControllerRigAsync(sceneTree);

        try
        {
            var lockedDestination = new Vector3(1.0f, 0f, 1.0f);
            var facingProbe = new Vector3(1.0f, 0f, -1.0f);
            Vector3 expectedFacing = facingProbe - lockedDestination;
            expectedFacing.Y = 0f;

            rig.Controller.BeginDestinationPlacementAt(lockedDestination);
            rig.Controller.UpdateDestinationFacingProbe(facingProbe);
            rig.Controller.CompleteDestinationPlacementAt(facingProbe);

            Assert.True(rig.Navigation.HasDestination);
            AssertVectorClose(lockedDestination, rig.Navigation.Destination.Origin, ControllerPositionTolerance);
            AssertBasisClose(Basis.LookingAt(expectedFacing.Normalized(), Vector3.Up), rig.Navigation.Destination.Basis);
            Assert.Same(rig.PreviewMaterial, rig.MarkerSurface.MaterialOverride);
        }
        finally
        {
            await DestroyPlaytestControllerRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the playtest camera keeps orbiting the initial focus point instead of following the moving navigation target.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlaytestController_CameraOrbitOrigin_RemainsFixedWhenTargetMoves()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreatePlaytestControllerRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            Vector3 initialCameraPosition = rig.Controller.LastCameraOrbitPosition;

            rig.Target.GlobalPosition = new Vector3(2.0f, 0f, 1.5f);
            rig.Target.ForceUpdateTransform();
            rig.Controller._Process(0.016);
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(initialOrigin, rig.Controller.CameraOrbitOrigin, ControllerPositionTolerance);
            AssertVectorClose(initialCameraPosition, rig.Controller.LastCameraOrbitPosition, ControllerPositionTolerance);
        }
        finally
        {
            await DestroyPlaytestControllerRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pan movement uses the inverted axes expected by the playtest controls.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlaytestController_PanCameraOrbitOrigin_MovesOriginAndCameraConsistently()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreatePlaytestControllerRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            Vector3 initialCameraPosition = rig.Controller.LastCameraOrbitPosition;
            Vector3 cameraRight = GetHorizontalOrFallback(rig.Camera.GlobalTransform.Basis.X, Vector3.Right);
            Vector3 cameraForward = GetHorizontalOrFallback(-rig.Camera.GlobalTransform.Basis.Z, Vector3.Forward);
            var relativeMotion = new Vector2(10f, -5f);
            float initialCameraDistance = initialCameraPosition.DistanceTo(initialOrigin);
            float panScale = rig.Controller.CameraPanSensitivity * Mathf.Clamp(initialCameraDistance, rig.Controller.MinCameraDistance, rig.Controller.MaxCameraDistance);
            Vector3 expectedDelta = ((-cameraRight * relativeMotion.X) + (cameraForward * relativeMotion.Y)) * panScale;

            rig.Controller.PanCameraOrbitOrigin(relativeMotion);
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(initialOrigin + expectedDelta, rig.Controller.CameraOrbitOrigin, ControllerPositionTolerance);
            AssertVectorClose(initialCameraPosition + expectedDelta, rig.Controller.LastCameraOrbitPosition, ControllerPositionTolerance);
            Assert.InRange(Mathf.Abs(rig.Controller.CameraOrbitOrigin.Y - initialOrigin.Y), 0f, BasisTolerance);
        }
        finally
        {
            await DestroyPlaytestControllerRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies middle-button mouse drag routes to panning instead of orbiting.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlaytestController_UnhandledInput_MiddleDragPansCameraOrbitOrigin()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreatePlaytestControllerRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            Vector3 initialCameraPosition = rig.Controller.LastCameraOrbitPosition;

            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Middle,
                Pressed = true,
                Position = new Vector2(100f, 100f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseMotion
            {
                Position = new Vector2(112f, 92f),
                Relative = new Vector2(12f, -8f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Middle,
                Pressed = false,
                Position = new Vector2(112f, 92f),
            });
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(
                rig.Controller.CameraOrbitOrigin.DistanceTo(initialOrigin) > ControllerPositionTolerance,
                "Expected middle-button drag to pan the camera orbit origin.");
            Assert.True(
                rig.Controller.LastCameraOrbitPosition.DistanceTo(initialCameraPosition) > ControllerPositionTolerance,
                "Expected middle-button drag to move the camera with the panned origin.");
        }
        finally
        {
            await DestroyPlaytestControllerRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies right-button mouse drag routes to orbiting instead of panning.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlaytestController_UnhandledInput_RightDragOrbitsCameraWithoutPanningOrigin()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreatePlaytestControllerRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            float initialCameraHeight = rig.Controller.LastCameraOrbitPosition.Y;

            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
                Position = new Vector2(100f, 100f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseMotion
            {
                Position = new Vector2(100f, 115f),
                Relative = new Vector2(0f, 15f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = false,
                Position = new Vector2(100f, 115f),
            });
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(initialOrigin, rig.Controller.CameraOrbitOrigin, ControllerPositionTolerance);
            Assert.True(
                rig.Controller.LastCameraOrbitPosition.Y > initialCameraHeight,
                $"Expected right-button vertical drag to orbit camera above {initialCameraHeight}, but camera Y was {rig.Controller.LastCameraOrbitPosition.Y}.");
        }
        finally
        {
            await DestroyPlaytestControllerRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies vertical orbit drag uses the inverted pitch sign expected by the playtest controls.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlaytestController_OrbitCamera_VerticalMotionRaisesPitchWithInvertedSign()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreatePlaytestControllerRigAsync(sceneTree);

        try
        {
            float initialCameraHeight = rig.Controller.LastCameraOrbitPosition.Y;

            rig.Controller.OrbitCamera(new Vector2(0f, 10f));
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(
                rig.Controller.LastCameraOrbitPosition.Y > initialCameraHeight,
                $"Expected positive vertical mouse motion to raise camera pitch above {initialCameraHeight}, but camera Y was {rig.Controller.LastCameraOrbitPosition.Y}.");
        }
        finally
        {
            await DestroyPlaytestControllerRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the destination marker is suppressed while navigation is active and returns once navigation stops.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlaytestController_DestinationMarker_HidesWhileNavigationRunsAndReturnsWhenStopped()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreatePlaytestControllerRigAsync(sceneTree);

        try
        {
            rig.Navigation.MovementSpeed = 0.1f;
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(3f, 0f, 3f))));
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            Assert.True(rig.Navigation.IsNavigationRunning, "Fixture destination should leave navigation running for marker visibility coverage.");

            rig.Controller.PreviewDestinationAt(new Vector3(0.5f, 0f, 0.5f));

            Assert.False(rig.Marker.Visible);

            rig.Navigation.ClearDestination();
            rig.Controller.PreviewDestinationAt(new Vector3(0.75f, 0f, 0.75f));

            Assert.True(rig.Marker.Visible);
        }
        finally
        {
            await DestroyPlaytestControllerRigAsync(sceneTree, rig);
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

    private static async Task<PlaytestControllerRig> CreatePlaytestControllerRigAsync(SceneTree sceneTree)
    {
        Node3D root = new()
        {
            Name = "PlaytestControllerRoot",
        };
        Node3D target = new()
        {
            Name = "Target",
        };
        DirectTransformNavigation navigation = new()
        {
            Name = "Navigation",
            Target = target,
        };
        Camera3D camera = new()
        {
            Name = "Camera3D",
            Current = true,
            Position = new Vector3(0f, 4f, -6f),
        };
        StaticBody3D ground = new()
        {
            Name = "Ground",
            CollisionLayer = 1U,
            CollisionMask = 0U,
        };
        CollisionShape3D groundCollision = new()
        {
            Name = "GroundCollision",
            Shape = new BoxShape3D { Size = new Vector3(20f, 0.1f, 20f) },
            Position = new Vector3(0f, -0.05f, 0f),
        };
        Node3D marker = new()
        {
            Name = "DestinationMarker",
        };
        MeshInstance3D markerSurface = new()
        {
            Name = "MarkerSurface",
        };
        StandardMaterial3D previewMaterial = new()
        {
            ResourceName = "PreviewMaterial",
        };
        StandardMaterial3D pressedMaterial = new()
        {
            ResourceName = "PressedMaterial",
        };
        DirectTransformNavigationPlaytestController controller = new()
        {
            Name = "Controller",
            Camera = camera,
            Navigation = navigation,
            DestinationMarker = marker,
            DestinationMarkerSurface = markerSurface,
            DestinationMarkerPreviewMaterial = previewMaterial,
            DestinationMarkerPressedMaterial = pressedMaterial,
            RayLength = 100f,
            GroundCollisionMask = 1U,
            MinCameraDistance = 2f,
            MaxCameraDistance = 10f,
            CameraZoomStep = 0.75f,
            CameraOrbitSensitivity = 0.006f,
            CameraPanSensitivity = 0.01f,
        };

        root.AddChild(CreateNavigationRegion());
        root.AddChild(ground);
        ground.AddChild(groundCollision);
        root.AddChild(target);
        target.AddChild(navigation);
        root.AddChild(camera);
        root.AddChild(marker);
        marker.AddChild(markerSurface);
        sceneTree.Root.AddChild(root);

        camera.Position = new Vector3(0f, 4f, -6f);
        camera.ForceUpdateTransform();
        camera.LookAt(target.GlobalPosition, Vector3.Up);
        root.AddChild(controller);
        await WaitForPhysicsFramesAsync(sceneTree, 2);
        camera.GlobalPosition = new Vector3(0f, 4f, -6f);
        camera.ForceUpdateTransform();
        camera.LookAt(target.GlobalPosition, Vector3.Up);
        controller.Camera = camera;
        controller.Navigation = navigation;
        controller.DestinationMarker = marker;
        controller.DestinationMarkerSurface = markerSurface;
        controller.DestinationMarkerPreviewMaterial = previewMaterial;
        controller.DestinationMarkerPressedMaterial = pressedMaterial;
        controller.ReinitialiseCameraOrbit();

        return new PlaytestControllerRig(root, target, navigation, camera, controller, marker, markerSurface, previewMaterial, pressedMaterial);
    }

    private static async Task DestroyPlaytestControllerRigAsync(SceneTree sceneTree, PlaytestControllerRig rig)
    {
        rig.Root.QueueFree();
        await WaitForNextFrameAsync(sceneTree);
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

    private static Vector3 GetHorizontalOrFallback(Vector3 direction, Vector3 fallback)
    {
        direction.Y = 0f;
        return direction.LengthSquared() > Mathf.Epsilon ? direction.Normalized() : fallback;
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

    private sealed record PlaytestControllerRig(
        Node3D Root,
        Node3D Target,
        DirectTransformNavigation Navigation,
        Camera3D Camera,
        DirectTransformNavigationPlaytestController Controller,
        Node3D Marker,
        GeometryInstance3D MarkerSurface,
        Material PreviewMaterial,
        Material PressedMaterial);
}
