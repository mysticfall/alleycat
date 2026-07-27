using AlleyCat.Control.Locomotion;
using AlleyCat.Core;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Focused Godot integration coverage for the NAV-001 locomotive navigation consumer.
/// </summary>
public sealed partial class LocomotiveNavigationIntegrationTests
{
    private const string NavigationTestNPCScenePath = "res://assets/testing/navigation/navigation_test_npc.tscn";
    private const string PredictiveNavigationPhotoboothPath =
        "res://tests/navigation/predictive_navigation_photobooth.tscn";

    /// <summary>
    /// Verifies the authored navigation playtest NPC completes role installation with one interface-backed locomotive navigator.
    /// </summary>
    [Headless]
    [Fact]
    public async Task NavigationTestNPCScene_InstallsOneExplicitlyBoundLocomotiveNavigation()
    {
        SceneTree sceneTree = GetSceneTree();
        PackedScene scene = GD.Load<PackedScene>(NavigationTestNPCScenePath);
        Node3D npc = scene.Instantiate<Node3D>();
        sceneTree.Root.AddChild(npc);

        try
        {
            await WaitForNextFrameAsync(sceneTree);
            await WaitForNextFrameAsync(sceneTree);

            LocomotiveNavigation navigation = npc.GetNode<LocomotiveNavigation>("Navigation");
            INavigation facade = navigation;
            Assert.Same(npc, navigation.Actor);
            Assert.Equal(StandingLocomotionCharacter.ReferenceFemale, navigation.ResponseProfileCharacter);
            _ = Assert.IsAssignableFrom<ILocomotive>(navigation.Actor);
            _ = Assert.Single(npc.GetChildren().OfType<NavigationBase>());
            Assert.False(facade.HasDestination);

        }
        finally
        {
            npc.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Guards the top-view camera frame and marker/route semantics used by predictive-navigation visual evidence.
    /// </summary>
    [Headless]
    [Fact]
    public void PredictiveNavigationPhotobooth_ProvidesUnambiguousTopTrajectoryFrameAndSemanticOverlays()
    {
        PackedScene scene = Assert.IsType<PackedScene>(
            ResourceLoader.Load(PredictiveNavigationPhotoboothPath),
            exactMatch: false);
        Node3D root = Assert.IsType<Node3D>(scene.Instantiate(), exactMatch: false);
        try
        {
            Camera3D camera = Assert.IsType<Camera3D>(root.GetNode("VerificationCamera"), exactMatch: false);
            Vector3 cameraForward = -camera.Basis.Z.Normalized();
            Vector3 screenRight = camera.Basis.X.Normalized();
            Vector3 screenUp = camera.Basis.Y.Normalized();
            Assert.Equal(Camera3D.ProjectionType.Orthogonal, camera.Projection);
            Assert.True(camera.Current);
            Assert.True(camera.Position.Y >= 10.0f);
            Assert.True(camera.Size >= 12.0f);
            Assert.True(cameraForward.Dot(Vector3.Down) > 0.999f);
            Assert.True(screenRight.Dot(Vector3.Right) > 0.999f);
            Assert.True(screenUp.Dot(Vector3.Forward) > 0.999f);

            Assert.NotNull(root.GetNodeOrNull<Node3D>("Markers"));
            Assert.NotNull(root.GetNodeOrNull<NavigationRegion3D>("NavigationRuntime/NavigationRegion3D"));
            Assert.NotNull(root.GetNodeOrNull<Node3D>("NavigationRuntime/NavigationTestNpc"));
            Assert.NotSame(camera, root.GetNode<Camera3D>("NavigationRuntime/Camera3D"));
            Assert.True(root.HasMethod("_draw_axis_and_legend"));
            Assert.True(root.HasMethod("_draw_route_geometry"));
            Assert.True(root.HasMethod("_draw_deviation_marker"));
            Assert.True(root.HasMethod("_verify_required_images"));
            Assert.True(root.HasMethod("_run_sharp_turn_diagnostic"));
            Assert.True(root.HasMethod("_run_sharp_turn_case"));
            Assert.True(root.HasMethod("_run_exact_sharp_turn_diagnostic"));
            Assert.True(root.HasMethod("_sample_exact_sharp_turn"));
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Verifies explicit binding validation and deterministic publish-before-locomotion priority.
    /// </summary>
    [Headless]
    [Fact]
    public void BindingAndPriority_RequireExplicitLocomotiveNodeAndPublishBeforeDefaultConsumers()
    {
        LocomotiveNavigation navigation = new();
        Node3D invalidActor = new();
        RecordingLocomotiveActor validActor = new();
        AnimationTree animationTree = new();
        CharacterLocomotion locomotion = new();
        try
        {
            _ = Assert.Single(navigation._GetConfigurationWarnings());

            navigation.Actor = invalidActor;
            _ = Assert.Single(navigation._GetConfigurationWarnings());

            navigation.Actor = validActor;
            navigation.ResponseProfileCharacter = StandingLocomotionCharacter.ReferenceFemale;
            Assert.Empty(navigation._GetConfigurationWarnings());
            Assert.True(navigation.ProcessPhysicsPriority < animationTree.ProcessPhysicsPriority);
            Assert.True(animationTree.ProcessPhysicsPriority < locomotion.ProcessPhysicsPriority);
        }
        finally
        {
            navigation.Free();
            invalidActor.Free();
            validActor.Free();
            animationTree.Free();
            locomotion.Free();
        }
    }

    /// <summary>
    /// Verifies one coherent actor sample publishes local commands without moving the actor and rejection is atomic.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PhysicsTick_PublishesFromActorWorldTransformWithoutMutationAndRejectedReplacementPreservesCommand()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotiveNavigationRig rig = await CreateRigAsync(sceneTree);
        try
        {
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(1.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            _ = rig.Navigation.Poll(rig.Actor.GlobalTransform);
            Transform3D before = rig.Actor.GlobalTransform;
            rig.Navigation.ResetSampleCount();
            rig.Actor.ResetCommandCounts();

            rig.Navigation._PhysicsProcess(1.0 / 60.0);

            Assert.Equal(1, rig.Navigation.SampleCount);
            Assert.Equal(before, rig.Actor.GlobalTransform);
            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.InRange(Mathf.Abs(rig.Actor.RotationInput.X), 0.01f, 0.3f);
            Assert.True(rig.Actor.RotationInput.X > 0.0f, "A destination to world +X must publish semantic RIGHT as positive.");
            Assert.Equal(1, rig.Actor.MoveCount);
            Assert.Equal(1, rig.Actor.RotateCount);
            Vector2 activeMovement = rig.Actor.MovementInput;
            Vector2 activeRotation = rig.Actor.RotationInput;

            var invalidDestination = new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Invalid, rig.Navigation.SetDestination(invalidDestination));
            Assert.Equal(activeMovement, rig.Actor.MovementInput);
            Assert.Equal(activeRotation, rig.Actor.RotationInput);
            Assert.Equal(1, rig.Actor.MoveCount);
            Assert.Equal(1, rig.Actor.RotateCount);
            Assert.Equal(destination, rig.Navigation.Destination);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies invalid samples and clear immediately release both locomotion command channels.
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidSampleAndClear_NeutraliseMovementAndRotationImmediately()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotiveNavigationRig rig = await CreateRigAsync(sceneTree);
        try
        {
            Transform3D destination = FacingTransform(Vector3.Right, new Vector3(1.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            _ = rig.Navigation.Poll(rig.Actor.GlobalTransform);
            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.NotEqual(Vector2.Zero, rig.Actor.RotationInput);

            rig.Navigation.UseInvalidSample = true;
            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.Equal(Vector2.Zero, rig.Actor.RotationInput);

            rig.Navigation.UseInvalidSample = false;
            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.NotEqual(Vector2.Zero, rig.Actor.RotationInput);
            rig.Navigation.ClearDestination();
            Assert.NotEqual(Vector2.Zero, rig.Actor.RotationInput);
            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.Equal(Vector2.Zero, rig.Actor.RotationInput);

            Assert.Equal(
                NavigationDestinationResult.Accepted,
                rig.Navigation.SetDestination(rig.Actor.GlobalTransform));
            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.True(((INavigation)rig.Navigation).IsNavigationFinished);
            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.Equal(Vector2.Zero, rig.Actor.RotationInput);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies turn-in-place publication and lifecycle neutralisation without direct actor rotation.
    /// </summary>
    [Headless]
    [Fact]
    public async Task TurnInPlaceDisableAndExit_PublishBoundedRotationThenNeutralise()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotiveNavigationRig rig = await CreateRigAsync(sceneTree);
        try
        {
            Transform3D destination = FacingTransform(Vector3.Right, rig.Actor.GlobalPosition);
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            Transform3D before = rig.Actor.GlobalTransform;

            rig.Navigation._PhysicsProcess(1.0 / 60.0);

            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.InRange(Mathf.Abs(rig.Actor.RotationInput.X), 0.01f, 1.0f);
            Assert.Equal(before, rig.Actor.GlobalTransform);

            rig.Navigation.SetPhysicsProcess(false);
            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.Equal(Vector2.Zero, rig.Actor.RotationInput);

            rig.Navigation.SetPhysicsProcess(true);
            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.NotEqual(Vector2.Zero, rig.Actor.RotationInput);
            rig.Navigation._ExitTree();
            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.Equal(Vector2.Zero, rig.Actor.RotationInput);
            rig.Navigation.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies navigation completion remains neutral when planner-facing tolerance is narrower than navigation tolerance.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CompletedWithinNavigationFacingTolerance_NeutralisesOnceAndDoesNotRepublishPlannerTurn()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotiveNavigationRig rig = await CreateRigAsync(sceneTree);
        try
        {
            rig.Navigation.FacingToleranceDegrees = 3.0f;
            Transform3D destination = new(
                new Basis(Vector3.Up, Mathf.DegToRad(2.5f)),
                rig.Actor.GlobalPosition);
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));

            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.True(((INavigation)rig.Navigation).IsNavigationFinished);

            rig.Navigation.ResetSampleCount();
            for (int frame = 0; frame < 30; frame++)
            {
                rig.Navigation._PhysicsProcess(1.0 / 60.0);
                Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
                Assert.Equal(Vector2.Zero, rig.Actor.RotationInput);
            }

            Assert.Equal(30, rig.Navigation.SampleCount);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies completed navigation keeps polling authoritative state and resumes command publication only after a
    /// terminal-release position miss.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CompletedThenMeaningfullyDisplaced_PollsAndResumesRouteRecovery()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotiveNavigationRig rig = await CreateRigAsync(sceneTree);
        try
        {
            Transform3D destination = rig.Actor.GlobalTransform;
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            rig.Navigation.SetPathOverride([Vector3.Zero, destination.Origin], 1);

            rig.Navigation._PhysicsProcess(1.0 / 60.0);
            Assert.True(((INavigation)rig.Navigation).IsNavigationFinished);
            Assert.Equal(Vector2.Zero, rig.Actor.MovementInput);
            Assert.Equal(Vector2.Zero, rig.Actor.RotationInput);

            Transform3D displacedTransform = new(
                rig.Actor.GlobalTransform.Basis,
                new Vector3(0.10f, 0.0f, 0.0f));
            Transform3D actorBeforeRecovery = rig.Actor.GlobalTransform;
            rig.Navigation.ObservedTransformOverride = displacedTransform;
            rig.Navigation.ResetSampleCount();
            rig.Actor.ResetCommandCounts();

            rig.Navigation._PhysicsProcess(0.2);

            Assert.Equal(1, rig.Navigation.SampleCount);
            Assert.False(rig.Navigation.LastPositionComplete);
            Assert.Equal(actorBeforeRecovery, rig.Actor.GlobalTransform);
            Assert.True(rig.Actor.MovementInput.Length() > 0.01f);
            Assert.Equal(1, rig.Actor.MoveCount);
            Assert.Equal(1, rig.Actor.RotateCount);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies one poll publishes copied route state and deterministic request, geometry, index, and fallback revisions.
    /// </summary>
    [Headless]
    [Fact]
    public async Task RouteSnapshot_CapturesOneImmutablePollAndRevisesDeterministically()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotiveNavigationRig rig = await CreateRigAsync(sceneTree);
        try
        {
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(2.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            Vector3[] firstPath = [Vector3.Zero, destination.Origin];
            rig.Navigation.SetPathOverride(firstPath, 0);

            NavigationMotionIntent firstIntent = rig.Navigation.Poll(rig.Actor.GlobalTransform);
            NavigationRouteSnapshot first = Assert.IsType<NavigationRouteSnapshot>(rig.Navigation.CaptureRouteSnapshot());
            Assert.Equal(firstIntent.NextPathPosition, first.NextPathPoint);
            Assert.Equal(firstPath, first.PathPoints);
            Assert.Equal(1, first.ActivePathIndex);
            Assert.Equal(destination, first.Destination);
            Assert.Equal(1, first.DestinationRequestGeneration);
            Assert.Equal(1, first.RouteRevision);
            Assert.False(first.UsedAcceptedPathFallback);
            Assert.False(first.WasReplanned);

            firstPath[0] = new Vector3(99.0f, 0.0f, 99.0f);
            Assert.Equal(Vector3.Zero, first.PathPoints[0]);

            rig.Navigation.SetPathOverride([Vector3.Zero, destination.Origin], 1);
            _ = rig.Navigation.Poll(rig.Actor.GlobalTransform);
            NavigationRouteSnapshot advanced = Assert.IsType<NavigationRouteSnapshot>(rig.Navigation.CaptureRouteSnapshot());
            Assert.Equal(first.RouteRevision, advanced.RouteRevision);
            Assert.False(advanced.WasReplanned);
            Assert.Equal(1, advanced.ActivePathIndex);

            Vector3[] revisedPath = [Vector3.Zero, new Vector3(1.0f, 0.0f, 0.5f), destination.Origin];
            rig.Navigation.SetPathOverride(revisedPath, 1);
            _ = rig.Navigation.Poll(rig.Actor.GlobalTransform);
            NavigationRouteSnapshot revised = Assert.IsType<NavigationRouteSnapshot>(rig.Navigation.CaptureRouteSnapshot());
            Assert.Equal(advanced.RouteRevision + 1, revised.RouteRevision);
            Assert.True(revised.WasReplanned);
            Assert.Equal(1, revised.ActivePathIndex);

            rig.Navigation.UseAcceptedPathFallback = true;
            _ = rig.Navigation.Poll(rig.Actor.GlobalTransform);
            NavigationRouteSnapshot fallback = Assert.IsType<NavigationRouteSnapshot>(rig.Navigation.CaptureRouteSnapshot());
            Assert.True(fallback.UsedAcceptedPathFallback);
            Assert.True(fallback.RouteRevision > revised.RouteRevision);

            var invalidDestination = new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Invalid, rig.Navigation.SetDestination(invalidDestination));
            Assert.Same(fallback, rig.Navigation.CaptureRouteSnapshot());

            rig.Navigation.UseAcceptedPathFallback = false;
            Transform3D replacement = FacingTransform(Vector3.Forward, new Vector3(1.5f, 0.0f, 0.5f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(replacement));
            Assert.Null(rig.Navigation.CaptureRouteSnapshot());
            rig.Navigation.SetPathOverride([Vector3.Zero, replacement.Origin], 0);
            _ = rig.Navigation.Poll(rig.Actor.GlobalTransform);
            NavigationRouteSnapshot replaced = Assert.IsType<NavigationRouteSnapshot>(rig.Navigation.CaptureRouteSnapshot());
            Assert.Equal(first.DestinationRequestGeneration + 1, replaced.DestinationRequestGeneration);
            Assert.True(replaced.RouteRevision > fallback.RouteRevision);
            Assert.Equal(replacement, replaced.Destination);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a smoothed actor that passes an interior waypoint can still advance coherent path intent and complete.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PassedInteriorWaypoint_AdvancesEffectiveSampleAndAllowsTerminalCompletion()
    {
        SceneTree sceneTree = GetSceneTree();
        LocomotiveNavigationRig rig = await CreateRigAsync(sceneTree);
        try
        {
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(2.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            Vector3 waypoint = new(1.0f, 0.0f, 0.5f);
            rig.Navigation.SetPathOverride(
                [Vector3.Zero, waypoint, destination.Origin],
                1);
            Assert.Equal(destination, rig.Navigation.Destination);
            Assert.True(destination.Origin.DistanceTo(waypoint) > rig.Navigation.PathDesiredDistance);

            NavigationMotionIntent intent = rig.Navigation.Poll(destination);
            NavigationRouteSnapshot snapshot = Assert.IsType<NavigationRouteSnapshot>(rig.Navigation.CaptureRouteSnapshot());

            Assert.Equal(2, snapshot.ActivePathIndex);
            Assert.True(intent.PositionReached);
            Assert.True(intent.FacingReached);
            Assert.True(intent.IsComplete);
            Assert.InRange(intent.RemainingPathDistance, 0.0f, rig.Navigation.PathDesiredDistance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    private static async Task<LocomotiveNavigationRig> CreateRigAsync(SceneTree sceneTree)
    {
        Node3D root = new()
        {
            Name = "LocomotiveNavigationTestRoot",
            ProcessMode = Node.ProcessModeEnum.Always,
        };
        NavigationRegion3D region = new()
        {
            NavigationMesh = CreatePlaneNavigationMesh(),
        };
        RecordingLocomotiveActor actor = new()
        {
            Name = "Actor",
            Transform = FacingTransform(Vector3.Forward, Vector3.Zero),
        };
        ScriptedLocomotiveNavigation navigation = new()
        {
            Name = "Navigation",
            Actor = actor,
            PathDesiredDistance = 0.05f,
            DestinationReachedDistance = 0.05f,
            FacingToleranceDegrees = 1.0f,
            NavigationLayers = 1U,
            PathMetadataFlags = NavigationPathQueryParameters3D.PathMetadataFlags.None,
            PathPostprocessing = NavigationPathQueryParameters3D.PathPostProcessing.Edgecentered,
            ResponseProfileCharacter = StandingLocomotionCharacter.ReferenceFemale,
            ProcessMode = Node.ProcessModeEnum.Always,
        };
        Rid navigationMap = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(navigationMap, true);
        region.SetNavigationMap(navigationMap);
        navigation.SetNavigationMap(navigationMap);
        root.AddChild(region);
        root.AddChild(actor);
        root.AddChild(navigation);
        sceneTree.Root.AddChild(root);
        await WaitForNextFrameAsync(sceneTree);
        region.SetNavigationMap(navigationMap);
        navigation.SetNavigationMap(navigationMap);
        NavigationServer3D.AgentSetPosition(navigation.GetRid(), actor.GlobalPosition);

        for (int attempt = 0; attempt < 30; attempt++)
        {
            Vector3[] path = NavigationServer3D.MapGetPath(
                navigationMap,
                Vector3.Zero,
                new Vector3(0.5f, 0.0f, 0.0f),
                true);
            if (path.Length > 0)
            {
                break;
            }

            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        navigation.SetPhysicsProcess(false);
        actor.ResetCommandCounts();
        return new LocomotiveNavigationRig(root, navigationMap, actor, navigation);
    }

    private static async Task DestroyRigAsync(SceneTree sceneTree, LocomotiveNavigationRig rig)
    {
        if (GodotObject.IsInstanceValid(rig.Root) && rig.Root.IsInsideTree())
        {
            rig.Root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }

        NavigationServer3D.FreeRid(rig.NavigationMap);
    }

    private static NavigationMesh CreatePlaneNavigationMesh()
    {
        NavigationMesh mesh = new();
        mesh.SetVertices([
            new Vector3(-1.0f, 0.0f, -1.0f),
            new Vector3(4.0f, 0.0f, -1.0f),
            new Vector3(4.0f, 0.0f, 4.0f),
            new Vector3(-1.0f, 0.0f, 4.0f),
        ]);
        mesh.AddPolygon([0, 1, 2]);
        mesh.AddPolygon([0, 2, 3]);
        return mesh;
    }

    private static Transform3D FacingTransform(Vector3 facing, Vector3 origin)
    {
        Vector3 stableFacing = facing.Normalized();
        Vector3 right = stableFacing.Cross(Vector3.Up).Normalized();
        return new Transform3D(new Basis(right, Vector3.Up, -stableFacing), origin);
    }

    private sealed record LocomotiveNavigationRig(
        Node3D Root,
        Rid NavigationMap,
        RecordingLocomotiveActor Actor,
        ScriptedLocomotiveNavigation Navigation);

    private sealed partial class ScriptedLocomotiveNavigation : LocomotiveNavigation
    {
        public Transform3D? ObservedTransformOverride
        {
            get;
            set;
        }

        public int SampleCount
        {
            get; private set;
        }

        public bool UseInvalidSample
        {
            get; set;
        }

        public bool UseAcceptedPathFallback
        {
            get; set;
        }

        private Vector3[]? PathOverride
        {
            get; set;
        }

        private int PathIndexOverride
        {
            get; set;
        }

        public void ResetSampleCount() => SampleCount = 0;

        public NavigationRouteSnapshot? CaptureRouteSnapshot() => CurrentRouteSnapshot;

        protected override Transform3D GetAuthoritativeActorTransform(Node3D actor)
            => ObservedTransformOverride ?? base.GetAuthoritativeActorTransform(actor);

        public void SetPathOverride(Vector3[] path, int pathIndex)
        {
            PathOverride = path;
            PathIndexOverride = pathIndex;
        }

        protected override void AdjustPathSample(ref Vector3 nextPathPosition, ref Vector3[] path, ref int pathIndex)
        {
            SampleCount++;
            if (UseInvalidSample)
            {
                nextPathPosition = new Vector3(float.NaN, 0.0f, 0.0f);
                path = [nextPathPosition];
                pathIndex = 0;
                return;
            }

            if (UseAcceptedPathFallback)
            {
                path = [];
                pathIndex = 0;
                return;
            }

            if (PathOverride is { Length: > 0 } pathOverride)
            {
                path = pathOverride;
                pathIndex = Math.Clamp(PathIndexOverride, 0, path.Length - 1);
                nextPathPosition = path[pathIndex];
            }
        }
    }

    private sealed partial class RecordingLocomotiveActor : Node3D, ILocomotive
    {
        public IReadOnlyList<IComponent> Components { get; } = [];

        public Vector2 MovementInput
        {
            get; private set;
        }

        public Vector2 RotationInput
        {
            get; private set;
        }

        public int MoveCount
        {
            get; private set;
        }

        public int RotateCount
        {
            get; private set;
        }

        public void Move(Vector2 input)
        {
            MovementInput = input;
            MoveCount++;
        }

        public void Rotate(Vector2 input)
        {
            RotationInput = input;
            RotateCount++;
        }

        public void ResetCommandCounts()
        {
            MoveCount = 0;
            RotateCount = 0;
        }
    }
}
