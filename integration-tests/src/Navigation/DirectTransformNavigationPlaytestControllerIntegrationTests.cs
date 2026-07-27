using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.Logging;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Preserved focused integration coverage for the direct-navigation playtest controller.
/// </summary>
public sealed partial class DirectTransformNavigationPlaytestControllerIntegrationTests
{
    private const float PositionTolerance = 0.08f;
    private const float BasisTolerance = 0.0001f;

    /// <summary>
    /// Verifies drag-to-release placement preserves the locked destination origin and selected facing.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DragRelease_SetsDestinationWithSelectedFacing()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            var lockedDestination = new Vector3(1.0f, 0.0f, 1.0f);
            var facingProbe = new Vector3(1.0f, 0.0f, -1.0f);
            Vector3 expectedFacing = facingProbe - lockedDestination;
            expectedFacing.Y = 0.0f;

            rig.Controller.BeginDestinationPlacementAt(lockedDestination);
            rig.Controller.UpdateDestinationFacingProbe(facingProbe);
            rig.Controller.CompleteDestinationPlacementAt(facingProbe);

            Assert.True(rig.Navigation.HasDestination);
            AssertVectorClose(lockedDestination, rig.Navigation.Destination.Origin);
            AssertBasisClose(Basis.LookingAt(expectedFacing.Normalized(), Vector3.Up), rig.Navigation.Destination.Basis);
            Assert.Same(rig.PreviewMaterial, rig.MarkerSurface.MaterialOverride);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies each acceptance attempt emits structured world-space reproduction coordinates and a monotonic request ID.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DragRelease_LogsStructuredDestinationAttemptWithMonotonicRequestID()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Vector3 firstDestination = new(1.0f, 0.0f, 1.0f);
            Vector3 secondDestination = new(2.0f, 0.0f, 1.5f);
            rig.Controller.BeginDestinationPlacementAt(firstDestination);
            rig.Controller.CompleteDestinationPlacementAt(firstDestination + Vector3.Forward);
            rig.Controller.BeginDestinationPlacementAt(secondDestination);
            rig.Controller.CompleteDestinationPlacementAt(secondDestination + Vector3.Right);

            Assert.Collection(
                rig.Controller.Logger.Entries,
                first => AssertDestinationLog(first, 1, firstDestination, Mathf.Sqrt(2.0f)),
                second => AssertDestinationLog(second, 2, secondDestination, 2.5f));
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies input only queues click placement and that the physics callback performs the ground projection and placement.
    /// </summary>
    [Headless]
    [Fact]
    public async Task UnhandledInput_ClickPlacementIsQueuedUntilPhysicsProcess()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            var destinationMousePosition = new Vector2(1.0f, 1.0f);
            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = destinationMousePosition,
            });
            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = false,
                Position = destinationMousePosition,
            });

            Assert.True(rig.Controller.HasPendingDestinationPlacementRequest);
            Assert.False(rig.Navigation.HasDestination);
            Assert.Equal(0, rig.Controller.ProjectionAttemptCount);

            rig.Controller._PhysicsProcess(0.0);

            Assert.False(rig.Controller.HasPendingDestinationPlacementRequest);
            Assert.True(rig.Navigation.HasDestination);
            Assert.Equal(3, rig.Controller.ProjectionAttemptCount);
            AssertVectorClose(new Vector3(1.0f, 0.0f, 1.0f), rig.Navigation.Destination.Origin);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies camera orbit remains anchored to its initial focus when the navigation target moves.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CameraOrbitOrigin_RemainsFixedWhenTargetMoves()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            Vector3 initialCameraPosition = rig.Controller.LastCameraOrbitPosition;

            rig.Target.Position = new Vector3(2.0f, 0.0f, 1.5f);
            rig.Controller._Process(0.016);
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(initialOrigin, rig.Controller.CameraOrbitOrigin);
            AssertVectorClose(initialCameraPosition, rig.Controller.LastCameraOrbitPosition);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies pan movement uses the controller's inverted camera-relative axes.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PanCameraOrbitOrigin_MovesOriginAndCameraConsistently()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            Vector3 initialCameraPosition = rig.Controller.LastCameraOrbitPosition;
            Vector3 cameraRight = GetHorizontalOrFallback(rig.Camera.GlobalTransform.Basis.X, Vector3.Right);
            Vector3 cameraForward = GetHorizontalOrFallback(-rig.Camera.GlobalTransform.Basis.Z, Vector3.Forward);
            var relativeMotion = new Vector2(10.0f, -5.0f);
            float initialDistance = initialCameraPosition.DistanceTo(initialOrigin);
            float panScale = rig.Controller.CameraPanSensitivity
                * Mathf.Clamp(initialDistance, rig.Controller.MinCameraDistance, rig.Controller.MaxCameraDistance);
            Vector3 expectedDelta = ((-cameraRight * relativeMotion.X) + (cameraForward * relativeMotion.Y)) * panScale;

            rig.Controller.PanCameraOrbitOrigin(relativeMotion);
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(initialOrigin + expectedDelta, rig.Controller.CameraOrbitOrigin);
            AssertVectorClose(initialCameraPosition + expectedDelta, rig.Controller.LastCameraOrbitPosition);
            Assert.InRange(Mathf.Abs(rig.Controller.CameraOrbitOrigin.Y - initialOrigin.Y), 0.0f, BasisTolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies middle-button drag routes to camera panning.
    /// </summary>
    [Headless]
    [Fact]
    public async Task UnhandledInput_MiddleDragPansCameraOrbitOrigin()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            Vector3 initialCameraPosition = rig.Controller.LastCameraOrbitPosition;

            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Middle,
                Pressed = true,
                Position = new Vector2(100.0f, 100.0f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseMotion
            {
                Position = new Vector2(112.0f, 92.0f),
                Relative = new Vector2(12.0f, -8.0f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Middle,
                Pressed = false,
                Position = new Vector2(112.0f, 92.0f),
            });
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(rig.Controller.CameraOrbitOrigin.DistanceTo(initialOrigin) > PositionTolerance);
            Assert.True(rig.Controller.LastCameraOrbitPosition.DistanceTo(initialCameraPosition) > PositionTolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies right-button drag orbits without panning the focus point.
    /// </summary>
    [Headless]
    [Fact]
    public async Task UnhandledInput_RightDragOrbitsCameraWithoutPanningOrigin()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Vector3 initialOrigin = rig.Controller.CameraOrbitOrigin;
            float initialCameraHeight = rig.Controller.LastCameraOrbitPosition.Y;

            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
                Position = new Vector2(100.0f, 100.0f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseMotion
            {
                Position = new Vector2(100.0f, 115.0f),
                Relative = new Vector2(0.0f, 15.0f),
            });
            rig.Controller._UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = false,
                Position = new Vector2(100.0f, 115.0f),
            });
            await WaitForNextFrameAsync(sceneTree);

            AssertVectorClose(initialOrigin, rig.Controller.CameraOrbitOrigin);
            Assert.True(rig.Controller.LastCameraOrbitPosition.Y > initialCameraHeight);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies positive vertical orbit input raises pitch with the configured inverted sign.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OrbitCamera_VerticalMotionRaisesPitchWithInvertedSign()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            float initialCameraHeight = rig.Controller.LastCameraOrbitPosition.Y;

            rig.Controller.OrbitCamera(new Vector2(0.0f, 10.0f));
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(rig.Controller.LastCameraOrbitPosition.Y > initialCameraHeight);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the destination marker hides during navigation and returns after navigation is cleared.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DestinationMarker_HidesWhileNavigationRunsAndReturnsWhenStopped()
    {
        SceneTree sceneTree = GetSceneTree();
        PlaytestControllerRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Navigation.MovementSpeed = 0.1f;
            Assert.Equal(
                NavigationDestinationResult.Accepted,
                rig.Navigation.SetDestination(new Transform3D(Basis.Identity, new Vector3(3.0f, 0.0f, 3.0f))));
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            Assert.True(rig.Navigation.IsNavigationRunning);

            rig.Controller.PreviewDestinationAt(new Vector3(0.5f, 0.0f, 0.5f));
            Assert.False(rig.Marker.Visible);

            rig.Navigation.ClearDestination();
            rig.Controller.PreviewDestinationAt(new Vector3(0.75f, 0.0f, 0.75f));
            Assert.True(rig.Marker.Visible);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    private static async Task<PlaytestControllerRig> CreateRigAsync(SceneTree sceneTree)
    {
        Node3D root = new()
        {
            Name = "PlaytestControllerRoot"
        };
        Node3D target = new()
        {
            Name = "Target"
        };
        DirectTransformNavigation navigation = new()
        {
            Name = "Navigation",
            Target = target,
        };
        NavigationMesh navigationMesh = new();
        navigationMesh.SetVertices([
            new Vector3(-1.0f, 0.0f, -1.0f),
            new Vector3(4.0f, 0.0f, -1.0f),
            new Vector3(4.0f, 0.0f, 4.0f),
            new Vector3(-1.0f, 0.0f, 4.0f),
        ]);
        navigationMesh.AddPolygon([0, 1, 2]);
        navigationMesh.AddPolygon([0, 2, 3]);
        NavigationRegion3D navigationRegion = new()
        {
            Name = "NavigationRegion3D",
            NavigationMesh = navigationMesh,
        };
        Rid navigationMap = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(navigationMap, true);
        navigationRegion.SetNavigationMap(navigationMap);
        navigation.SetNavigationMap(navigationMap);
        Camera3D camera = new()
        {
            Name = "Camera3D",
            Current = true,
            Position = new Vector3(0.0f, 4.0f, -6.0f),
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
            Shape = new BoxShape3D { Size = new Vector3(20.0f, 0.1f, 20.0f) },
            Position = new Vector3(0.0f, -0.05f, 0.0f),
        };
        Node3D marker = new()
        {
            Name = "DestinationMarker"
        };
        MeshInstance3D markerSurface = new()
        {
            Name = "MarkerSurface"
        };
        StandardMaterial3D previewMaterial = new()
        {
            ResourceName = "PreviewMaterial"
        };
        StandardMaterial3D pressedMaterial = new()
        {
            ResourceName = "PressedMaterial"
        };
        ProjectionTrackingPlaytestController controller = new()
        {
            Name = "Controller",
            Camera = camera,
            Navigation = navigation,
            Actor = target,
            DestinationMarker = marker,
            DestinationMarkerSurface = markerSurface,
            DestinationMarkerPreviewMaterial = previewMaterial,
            DestinationMarkerPressedMaterial = pressedMaterial,
            RayLength = 100.0f,
            GroundCollisionMask = 1U,
            MinCameraDistance = 2.0f,
            MaxCameraDistance = 10.0f,
            CameraZoomStep = 0.75f,
            CameraOrbitSensitivity = 0.006f,
            CameraPanSensitivity = 0.01f,
        };

        root.AddChild(navigationRegion);
        root.AddChild(ground);
        ground.AddChild(groundCollision);
        root.AddChild(target);
        target.AddChild(navigation);
        root.AddChild(camera);
        root.AddChild(marker);
        marker.AddChild(markerSurface);
        sceneTree.Root.AddChild(root);
        navigationRegion.SetNavigationMap(navigationMap);
        navigation.SetNavigationMap(navigationMap);

        camera.Position = new Vector3(0.0f, 4.0f, -6.0f);
        camera.LookAt(target.Position, Vector3.Up);
        root.AddChild(controller);
        await WaitForNavigationMapAsync(sceneTree, navigationMap);
        camera.Position = new Vector3(0.0f, 4.0f, -6.0f);
        camera.LookAt(target.Position, Vector3.Up);
        controller.ReinitialiseCameraOrbit();

        return new PlaytestControllerRig(
            root,
            target,
            navigation,
            camera,
            controller,
            marker,
            markerSurface,
            previewMaterial,
            navigationMap);
    }

    private static async Task WaitForNavigationMapAsync(SceneTree sceneTree, Rid navigationMap)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (NavigationServer3D.MapGetPath(
                    navigationMap,
                    Vector3.Zero,
                    new Vector3(1.0f, 0.0f, 1.0f),
                    true).Length > 0)
            {
                return;
            }

            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }
    }

    private static async Task DestroyRigAsync(SceneTree sceneTree, PlaytestControllerRig rig)
    {
        rig.Root.QueueFree();
        await WaitForNextFrameAsync(sceneTree);
        NavigationServer3D.FreeRid(rig.NavigationMap);
    }

    private static Vector3 GetHorizontalOrFallback(Vector3 direction, Vector3 fallback)
    {
        direction.Y = 0.0f;
        return direction.LengthSquared() > Mathf.Epsilon ? direction.Normalized() : fallback;
    }

    private static void AssertBasisClose(Basis expected, Basis actual)
    {
        AssertVectorClose(expected.X, actual.X, BasisTolerance);
        AssertVectorClose(expected.Y, actual.Y, BasisTolerance);
        AssertVectorClose(expected.Z, actual.Z, BasisTolerance);
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual, float tolerance = PositionTolerance)
        => Assert.True(expected.DistanceTo(actual) <= tolerance, $"Expected {actual} to be within {tolerance} of {expected}.");

    private static void AssertDestinationLog(
        CapturedLogEntry entry,
        int expectedRequestID,
        Vector3 expectedDestination,
        float expectedDistance)
    {
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(expectedRequestID, entry.Properties["RequestID"]);
        Assert.Equal(NavigationDestinationResult.Accepted, entry.Properties["Result"]);
        AssertVectorClose(expectedDestination, Assert.IsType<Vector3>(entry.Properties["DestinationWorldPosition"]));
        AssertVectorClose(Vector3.Zero, Assert.IsType<Vector3>(entry.Properties["ActorWorldPosition"]));
        AssertVectorClose(Vector3.Forward, Assert.IsType<Vector3>(entry.Properties["ActorWorldForward"]));
        Assert.InRange(Assert.IsType<float>(entry.Properties["ActorToDestinationDistance"]), expectedDistance - BasisTolerance, expectedDistance + BasisTolerance);
        Assert.True(entry.Properties.ContainsKey("DestinationWorldYaw"));
        Assert.True(entry.Properties.ContainsKey("DestinationWorldForward"));
        Assert.True(entry.Properties.ContainsKey("ActorWorldYaw"));
    }

    private sealed record PlaytestControllerRig(
        Node3D Root,
        Node3D Target,
        DirectTransformNavigation Navigation,
        Camera3D Camera,
        ProjectionTrackingPlaytestController Controller,
        Node3D Marker,
        GeometryInstance3D MarkerSurface,
        Material PreviewMaterial,
        Rid NavigationMap);

    private sealed partial class ProjectionTrackingPlaytestController : DirectTransformNavigationPlaytestController
    {
        public CapturingLogger Logger { get; } = new();

        public int ProjectionAttemptCount
        {
            get; private set;
        }

        protected override bool TryProjectMouseToGround(Vector2 mousePosition, out Vector3 hitPosition)
        {
            ProjectionAttemptCount++;
            hitPosition = new Vector3(mousePosition.X, 0.0f, mousePosition.Y);
            return true;
        }

        protected override ILogger<DirectTransformNavigationPlaytestController> ResolveLogger() => Logger;
    }

    private sealed class CapturingLogger : ILogger<DirectTransformNavigationPlaytestController>
    {
        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
            {
                foreach ((string key, object? value) in structuredState)
                {
                    properties[key] = value;
                }
            }

            Entries.Add(new CapturedLogEntry(logLevel, properties));
        }
    }

    private sealed record CapturedLogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> Properties);
}
