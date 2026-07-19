using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation;

/// <summary>
/// Focused Godot integration coverage for NAV-001 polling and direct-transform consumption.
/// </summary>
public sealed partial class DirectTransformNavigationIntegrationTests
{
    private const float PositionTolerance = 0.06f;
    private const float DirectionTolerance = 0.02f;

    /// <summary>
    /// Verifies precision defaults apply to code-created and scene-instanced direct consumers and remain configurable.
    /// </summary>
    [Headless]
    [Fact]
    public void PrecisionDistanceDefaults_ApplyToFreshAndAuthoredSceneInstancesAndRemainConfigurable()
    {
        DirectTransformNavigation fresh = new();
        Assert.Equal(0.05f, fresh.PathDesiredDistance);
        Assert.Equal(0.05f, fresh.DestinationReachedDistance);
        Assert.Equal(fresh.DestinationReachedDistance, fresh.TargetDesiredDistance);
        Assert.True(fresh.DestinationReachedDistance < fresh.ShortMoveDistance);

        fresh.PathDesiredDistance = 0.02f;
        fresh.DestinationReachedDistance = 0.03f;
        Assert.Equal(0.02f, fresh.PathDesiredDistance);
        Assert.Equal(0.03f, fresh.DestinationReachedDistance);
        Assert.Equal(0.03f, fresh.TargetDesiredDistance);
        fresh.Free();

        string[] scenePaths = [
            "res://assets/testing/navigation/navigation_test_npc.tscn",
            "res://assets/characters/templates/reference_female/reference_female_base.tscn",
            "res://assets/characters/templates/reference_male/reference_male_base.tscn",
        ];
        foreach (string scenePath in scenePaths)
        {
            PackedScene scene = GD.Load<PackedScene>(scenePath);
            Node instance = scene.Instantiate();
            try
            {
                DirectTransformNavigation navigation = instance.GetNode<DirectTransformNavigation>("Navigation");
                Assert.Equal(0.05f, navigation.PathDesiredDistance);
                Assert.Equal(0.05f, navigation.DestinationReachedDistance);
                Assert.Equal(navigation.DestinationReachedDistance, navigation.TargetDesiredDistance);
            }
            finally
            {
                instance.Free();
            }
        }
    }

    /// <summary>
    /// Verifies representative sub-metre lateral routes translate and settle without introducing unnecessary yaw.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PrecisionLateralRoutes_TranslateAndSettleAtRepresentativeSubMetreDistancesWithoutYaw()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach (float distance in new[] { 0.10f, 0.25f, 0.50f, 0.90f })
        {
            NavigationTestRig rig = await CreateRigAsync(sceneTree);
            try
            {
                rig.Navigation.MovementSpeed = 0.1f;
                rig.Navigation.ShortMoveDistance = 1.0f;
                Basis initialBasis = GetWorldTransform(rig.Target).Basis;
                Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(distance, 0.0f, 0.0f));
                Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));

                NavigationMotionIntent initial = await PollUntilValidAsync(sceneTree, rig);
                Assert.False(initial.PositionReached);
                Assert.True(initial.FacingReached);

                await StepNavigationAsync(sceneTree, rig, 0.1);
                Assert.True(GetWorldTransform(rig.Target).Origin.X > 0.0f);

                for (int step = 0; step < 120 && !((INavigation)rig.Navigation).IsNavigationFinished; step++)
                {
                    await StepNavigationAsync(sceneTree, rig, 0.1);
                }

                NavigationMotionIntent completed = rig.Navigation.Poll(GetWorldTransform(rig.Target));
                Assert.True(completed.IsComplete, $"Expected {distance:F2} m route to complete; position={GetWorldTransform(rig.Target).Origin}, remaining={completed.RemainingPathDistance}.");
                Assert.InRange(GetWorldTransform(rig.Target).Origin.DistanceTo(destination.Origin), 0.0f, rig.Navigation.DestinationReachedDistance);
                AssertBasisClose(initialBasis, GetWorldTransform(rig.Target).Basis);
            }
            finally
            {
                await DestroyRigAsync(sceneTree, rig);
            }
        }
    }

    /// <summary>
    /// Verifies destination proximity across an interior corner cannot complete before terminal path proximity.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DestinationCloseAcrossCorner_ContinuesThroughWaypointUntilTerminalPrecision()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useScriptedPath: true);

        try
        {
            rig.Navigation.MovementSpeed = 0.5f;
            var destinationPosition = new Vector3(0.04f, 0.0f, 0.0f);
            Transform3D destination = FacingTransform(Vector3.Forward, destinationPosition);
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            ScriptedDirectTransformNavigation scripted = Assert.IsType<ScriptedDirectTransformNavigation>(rig.Navigation);
            var corner = new Vector3(0.0f, 0.0f, -0.5f);
            Vector3[] cornerPath = [corner, destinationPosition];
            scripted.SetPath(cornerPath, 0);

            NavigationMotionIntent closeAcrossCorner = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.True(GetWorldTransform(rig.Target).Origin.DistanceTo(destinationPosition) <= rig.Navigation.DestinationReachedDistance);
            Assert.True(closeAcrossCorner.RemainingPathDistance > rig.Navigation.PathDesiredDistance);
            Assert.False(closeAcrossCorner.PositionReached);
            Assert.False(closeAcrossCorner.IsComplete);

            await StepNavigationAsync(sceneTree, rig, 1.0);
            AssertVectorClose(corner, GetWorldTransform(rig.Target).Origin, PositionTolerance);

            scripted.SetPath(cornerPath, 1);
            for (int step = 0; step < 10 && !((INavigation)rig.Navigation).IsNavigationFinished; step++)
            {
                await StepNavigationAsync(sceneTree, rig, 0.25);
            }

            NavigationMotionIntent completed = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.True(completed.PositionReached);
            Assert.True(completed.IsComplete);
            Assert.InRange(GetWorldTransform(rig.Target).Origin.DistanceTo(destinationPosition), 0.0f, rig.Navigation.DestinationReachedDistance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies invalid replacement requests preserve an existing accepted destination and cached intent.
    /// </summary>
    [Headless]
    [Fact]
    public async Task SetDestination_InvalidTransformPreservesAcceptedDestinationAndIntent()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Transform3D acceptedDestination = FacingTransform(Vector3.Forward, new Vector3(1.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(acceptedDestination));
            NavigationMotionIntent acceptedIntent = await PollUntilValidAsync(sceneTree, rig);

            var invalidDestination = new Transform3D(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Invalid, rig.Navigation.SetDestination(invalidDestination));

            Assert.True(rig.Navigation.HasDestination);
            AssertTransformClose(acceptedDestination, rig.Navigation.Destination);
            NavigationMotionIntent preserved = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.Equal(acceptedIntent.NextPathPosition, preserved.NextPathPosition);
            Assert.Equal(acceptedIntent.IsComplete, preserved.IsComplete);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the closest Node3D ancestor is moved and gradually reaches terminal facing when no target is configured.
    /// </summary>
    [Headless]
    [Fact]
    public async Task WithoutExplicitTarget_ClosestNode3DAncestorTurnsToCompletion()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useExplicitTarget: false, useIntermediateNode: true);

        try
        {
            rig.Navigation.AngularSpeedDegrees = 45.0f;
            rig.Navigation.FacingToleranceDegrees = 0.5f;
            Transform3D destination = FacingTransform(Vector3.Right, GetWorldTransform(rig.Target).Origin);

            Assert.Null(rig.Navigation.Target);
            Assert.NotSame(rig.Target, rig.Navigation.GetParent());
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent initial = await PollUntilValidAsync(sceneTree, rig);
            Assert.False(initial.FacingReached);

            for (int index = 0; index < 40 && !((INavigation)rig.Navigation).IsNavigationFinished; index++)
            {
                await StepNavigationAsync(sceneTree, rig, 0.1);
            }

            NavigationMotionIntent completed = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.True(completed.IsComplete);
            AssertDirectionClose(Vector3.Right, GetHorizontalFacing(GetWorldTransform(rig.Target).Basis));
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a long lateral route captures standing facing before gradually turning towards path travel.
    /// </summary>
    [Headless]
    [Fact]
    public async Task LongLateralDestination_FirstSampleRetainsFacingThenTurnsGraduallyTowardsPath()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Navigation.InitialFacingRampDistance = 0.5f;
            rig.Navigation.FacingRampDistance = 0.25f;
            rig.Navigation.ShortMoveDistance = 0.1f;
            rig.Navigation.MovementSpeed = 1.0f;
            rig.Navigation.AngularSpeedDegrees = 45.0f;
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(2.5f, 0.0f, 0.0f));

            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent first = await PollUntilValidAsync(sceneTree, rig);

            Assert.True(
                first.HasValidSample,
                $"Expected a valid sample; map iteration={NavigationServer3D.MapGetIterationId(rig.Navigation.GetNavigationMap())}, path points={rig.Navigation.CurrentPath.Length}.");
            AssertDirectionClose(Vector3.Forward, first.DesiredFacingDirection);
            Vector3 pathBearing = GetHorizontalDirection(first.TravelDirection);
            Assert.True(pathBearing.X > 0.5f);
            Basis initialBasis = GetWorldTransform(rig.Target).Basis;

            for (int index = 0; index < 8; index++)
            {
                await StepNavigationAsync(sceneTree, rig, 0.1);
            }

            Vector3 progressedFacing = GetHorizontalFacing(GetWorldTransform(rig.Target).Basis);
            Assert.True(progressedFacing.Dot(pathBearing) > Vector3.Forward.Dot(pathBearing) + 0.1f);
            Assert.False(IsDirectionClose(pathBearing, progressedFacing), "Angular speed must prevent an instant path-facing snap.");
            Assert.False(IsBasisClose(initialBasis, GetWorldTransform(rig.Target).Basis));
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies world-up yaw preserves authored local/world scale and rotates the existing pitch and roll as a unit.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ScaledPitchedAndRolledActor_GradualWorldYawPreservesCompleteBasisAndScale()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Basis rotation = new Basis(Vector3.Up, 0.25f)
                * new Basis(Vector3.Right, 0.3f)
                * new Basis(Vector3.Forward, -0.2f);
            var authoredScale = new Vector3(1.4f, 0.8f, 1.7f);
            rig.Target.Transform = new Transform3D(rotation * Basis.FromScale(authoredScale), Vector3.Zero);
            rig.Target.ForceUpdateTransform();
            rig.Navigation.AngularSpeedDegrees = 60.0f;
            rig.Navigation.FacingToleranceDegrees = 0.25f;
            Transform3D initialLocal = rig.Target.Transform;
            Transform3D initialWorld = GetWorldTransform(rig.Target);
            Transform3D destination = FacingTransform(Vector3.Right, initialWorld.Origin);
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent initialIntent = await PollUntilValidAsync(sceneTree, rig);
            Assert.False(initialIntent.IsComplete);

            float yawStep = Mathf.Clamp(
                initialIntent.SignedYawError,
                -Mathf.DegToRad(6.0f),
                Mathf.DegToRad(6.0f));
            Basis expectedWorldBasis = new Basis(Vector3.Up, yawStep) * initialWorld.Basis;

            rig.Navigation._PhysicsProcess(0.1);

            Transform3D afterLocal = rig.Target.Transform;
            Transform3D afterWorld = GetWorldTransform(rig.Target);
            AssertBasisClose(expectedWorldBasis, afterWorld.Basis);
            AssertBasisMetricPreserved(initialLocal.Basis, afterLocal.Basis);
            AssertBasisMetricPreserved(initialWorld.Basis, afterWorld.Basis);
            Assert.False(IsBasisClose(initialWorld.Basis, afterWorld.Basis), "Yaw must remain gradual but non-zero.");

            for (int index = 0; index < 80 && !((INavigation)rig.Navigation).IsNavigationFinished; index++)
            {
                rig.Navigation._PhysicsProcess(0.1);
            }

            NavigationMotionIntent completed = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.True(completed.IsComplete);
            AssertDirectionClose(Vector3.Right, GetHorizontalFacing(GetWorldTransform(rig.Target).Basis));
            AssertBasisMetricPreserved(initialLocal.Basis, rig.Target.Transform.Basis);
            AssertBasisMetricPreserved(initialWorld.Basis, GetWorldTransform(rig.Target).Basis);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies zero elapsed time is an exact actor-transform no-op even when valid movement and yaw remain.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ZeroDelta_WithOutstandingIntent_DoesNotMutateLocalOrWorldTransform()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Target.Transform = new Transform3D(
                new Basis(Vector3.Right, 0.2f) * Basis.FromScale(new Vector3(1.2f, 0.9f, 1.5f)),
                Vector3.Zero);
            Transform3D destination = FacingTransform(Vector3.Right, new Vector3(2.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent intent = await PollUntilValidAsync(sceneTree, rig);
            Assert.True(intent.HasValidSample);
            Assert.False(intent.IsComplete);
            Transform3D localBefore = rig.Target.Transform;
            Transform3D worldBefore = GetWorldTransform(rig.Target);

            rig.Navigation._PhysicsProcess(0.0);

            Assert.Equal(localBefore, rig.Target.Transform);
            Assert.Equal(worldBefore, GetWorldTransform(rig.Target));
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies an already-complete valid intent does not rewrite or normalise the actor transform.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CompletedIntent_DoesNotMutateLocalOrWorldTransform()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Basis basis = new Basis(Vector3.Up, 0.35f)
                * new Basis(Vector3.Right, 0.2f)
                * Basis.FromScale(new Vector3(1.3f, 0.75f, 1.6f));
            rig.Target.Transform = new Transform3D(basis, Vector3.Zero);
            Transform3D localBefore = rig.Target.Transform;
            Transform3D worldBefore = GetWorldTransform(rig.Target);
            Transform3D destination = FacingTransform(GetHorizontalFacing(worldBefore.Basis), worldBefore.Origin);
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent intent = await PollUntilValidAsync(sceneTree, rig);
            Assert.True(intent.IsComplete);

            rig.Navigation._PhysicsProcess(1.0);

            Assert.Equal(localBefore, rig.Target.Transform);
            Assert.Equal(worldBefore, GetWorldTransform(rig.Target));
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies fallback publication and longer or shorter agent replans cannot rewind captured ramp progress.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PathPublicationAndReplans_KeepInitialFacingProgressFiniteAndMonotonic()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useScriptedPath: true);

        try
        {
            rig.Navigation.InitialFacingRampDistance = 4.0f;
            rig.Navigation.FacingRampDistance = 0.0f;
            rig.Navigation.ShortMoveDistance = 0.1f;
            Transform3D destination = FacingTransform(Vector3.Right, new Vector3(2.5f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            Vector3[] inspectedFallback = rig.Navigation.CurrentPath;
            Vector3 acceptedTerminal = inspectedFallback[^1];
            inspectedFallback[^1] = new Vector3(99.0f, 0.0f, 99.0f);
            Assert.Equal(acceptedTerminal, rig.Navigation.CurrentPath[^1]);

            NavigationMotionIntent fallback = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.True(fallback.HasValidSample);
            Assert.True(rig.Navigation.UsedAcceptedPathFallbackForLastPoll);
            Assert.Equal(0.0f, fallback.TravelledPathDistance);
            AssertDirectionClose(Vector3.Forward, fallback.DesiredFacingDirection);

            ScriptedDirectTransformNavigation scripted = Assert.IsType<ScriptedDirectTransformNavigation>(rig.Navigation);
            scripted.SetPath([Vector3.Zero, destination.Origin], 1);
            NavigationMotionIntent published = rig.Navigation.Poll(GetWorldTransform(rig.Target));

            Assert.False(rig.Navigation.UsedAcceptedPathFallbackForLastPoll);
            Assert.Equal(fallback.TravelledPathDistance, published.TravelledPathDistance);
            AssertDirectionClose(fallback.DesiredFacingDirection, published.DesiredFacingDirection);

            SetActorPosition(rig, new Vector3(0.4f, 0.0f, 0.0f));
            NavigationMotionIntent progressed = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            float progressedWeight = InitialFacingWeight(progressed, rig.Navigation.InitialFacingRampDistance);

            SetActorPosition(rig, new Vector3(0.4f, 0.0f, 0.6f));
            NavigationMotionIntent perpendicular = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            float perpendicularWeight = RampWeight(perpendicular, rig.Navigation.InitialFacingRampDistance);
            Assert.Equal(progressed.TravelledPathDistance, perpendicular.TravelledPathDistance);
            Assert.Equal(progressedWeight, perpendicularWeight, 4);

            SetActorPosition(rig, Vector3.Zero);
            NavigationMotionIntent backward = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            float backwardWeight = RampWeight(backward, rig.Navigation.InitialFacingRampDistance);
            Assert.Equal(perpendicular.TravelledPathDistance, backward.TravelledPathDistance);
            Assert.Equal(perpendicularWeight, backwardWeight, 4);

            scripted.SetPath([
                Vector3.Zero,
                new Vector3(-2.0f, 0.0f, 0.0f),
                destination.Origin,
            ], 1);
            NavigationMotionIntent longer = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            float longerWeight = RampWeight(longer, rig.Navigation.InitialFacingRampDistance);

            Assert.True(longer.RemainingPathDistance > progressed.RemainingPathDistance);
            Assert.Equal(backward.TravelledPathDistance, longer.TravelledPathDistance);
            Assert.Equal(backwardWeight, longerWeight, 4);

            scripted.SetPath([Vector3.Zero, destination.Origin], 1);
            NavigationMotionIntent shorter = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            float shorterWeight = RampWeight(shorter, rig.Navigation.InitialFacingRampDistance);

            Assert.True(shorter.RemainingPathDistance < longer.RemainingPathDistance);
            Assert.Equal(longer.TravelledPathDistance, shorter.TravelledPathDistance);
            Assert.Equal(longerWeight, shorterWeight, 4);

            SetActorPosition(rig, new Vector3(1.2f, 0.0f, 0.0f));
            NavigationMotionIntent forward = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            float forwardWeight = InitialFacingWeight(forward, rig.Navigation.InitialFacingRampDistance);
            Assert.True(forward.TravelledPathDistance > shorter.TravelledPathDistance);
            Assert.True(forwardWeight > shorterWeight);

            NavigationMotionIntent duplicate = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.Equal(forward.TravelledPathDistance, duplicate.TravelledPathDistance);
            Assert.True(float.IsFinite(duplicate.TravelledPathDistance));
            Assert.True(duplicate.DesiredFacingDirection.IsFinite());

            scripted.SetPath([
                GetWorldTransform(rig.Target).Origin,
                new Vector3(float.NaN, 0.0f, 0.0f),
                GetWorldTransform(rig.Target).Origin,
            ], 0);
            NavigationMotionIntent degenerate = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.Equal(duplicate.TravelledPathDistance, degenerate.TravelledPathDistance);
            Assert.True(float.IsFinite(degenerate.RemainingPathDistance));
            Assert.True(degenerate.DesiredFacingDirection.IsFinite());
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies returning from a published path to the accepted fallback cannot select a waypoint already passed.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PublishedPathToAcceptedFallback_AdvancesPastAlreadyPassedWaypoint()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useCornerMesh: true, useScriptedPath: true);

        try
        {
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(3.5f, 0.0f, 3.5f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            Vector3[] acceptedPath = rig.Navigation.CurrentPath;
            Assert.True(acceptedPath.Length >= 3);
            ScriptedDirectTransformNavigation scripted = Assert.IsType<ScriptedDirectTransformNavigation>(rig.Navigation);

            scripted.SetPath(acceptedPath, 1);
            _ = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Vector3 passedPosition = acceptedPath[1].MoveToward(acceptedPath[2], 0.2f);
            SetActorPosition(rig, passedPosition);

            scripted.SetPath([], 0);
            NavigationMotionIntent fallback = rig.Navigation.Poll(GetWorldTransform(rig.Target));

            Assert.True(rig.Navigation.UsedAcceptedPathFallbackForLastPoll);
            Assert.True(rig.Navigation.LastSampledPathIndex >= 2);
            Assert.Equal(acceptedPath[2], fallback.NextPathPosition);
            Assert.True(fallback.TravelDirection.Dot((acceptedPath[2] - passedPosition).Normalized()) > 0.99f);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies advancing the Godot path index preserves monotonic progress and uses the new downstream segment.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CurrentPathIndexTransition_PreservesMonotonicProgressAndRecomputesIntent()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useScriptedPath: true);

        try
        {
            rig.Navigation.InitialFacingRampDistance = 10.0f;
            rig.Navigation.FacingRampDistance = 0.0f;
            rig.Navigation.ShortMoveDistance = 0.1f;
            Transform3D destination = FacingTransform(Vector3.Back, new Vector3(3.5f, 0.0f, 3.5f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            ScriptedDirectTransformNavigation scripted = Assert.IsType<ScriptedDirectTransformNavigation>(rig.Navigation);
            Vector3 waypoint = new(2.0f, 0.0f, 0.0f);
            Vector3[] path = [Vector3.Zero, waypoint, destination.Origin];
            scripted.SetPath(path, 1);
            NavigationMotionIntent before = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            int beforeIndex = rig.Navigation.LastSampledPathIndex;
            Vector3 beforeFacing = before.DesiredFacingDirection;

            SetActorPosition(rig, before.NextPathPosition);
            scripted.SetPath(path, 2);
            NavigationMotionIntent after = rig.Navigation.Poll(GetWorldTransform(rig.Target));

            Assert.True(rig.Navigation.LastSampledPathIndex > beforeIndex);
            Assert.True(after.TravelledPathDistance >= before.TravelledPathDistance);
            Assert.Equal(2.0f, after.TravelledPathDistance, 4);
            Assert.True(float.IsFinite(after.TravelledPathDistance));
            Assert.True(Mathf.Abs(NavigationSteering.SignedYaw(beforeFacing, after.DesiredFacingDirection)) > 0.1f);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a short lateral move retains matching terminal facing while travel remains independent.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ShortLateralDestination_TravelsSidewaysWithoutForcedYaw()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Navigation.ShortMoveDistance = 1.0f;
            rig.Navigation.MovementSpeed = 1.0f;
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(0.9f, 0.0f, 0.0f));

            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent intent = await PollUntilValidAsync(sceneTree, rig);
            Basis initialBasis = GetWorldTransform(rig.Target).Basis;

            Assert.True(
                intent.HasValidSample,
                $"Expected valid agent sample; path={rig.Navigation.CurrentPath.Length}, agentTarget={rig.Navigation.TargetPosition}, targetDistance={rig.Navigation.TargetDesiredDistance}, finished={rig.Navigation.IsNavigationFinished()}, map={NavigationServer3D.MapGetIterationId(rig.Navigation.GetNavigationMap())}, regions={NavigationServer3D.MapGetRegions(rig.NavigationMap).Count}, agentPosition={NavigationServer3D.AgentGetPosition(rig.Navigation.GetRid())}, sameAgentMap={NavigationServer3D.AgentGetMap(rig.Navigation.GetRid()) == rig.NavigationMap}.");
            Assert.True(
                intent.TravelDirection.Dot(Vector3.Right) > 0.99f,
                $"Expected lateral travel, got travel={intent.TravelDirection}, next={intent.NextPathPosition}, actor={GetWorldTransform(rig.Target).Origin}.");
            AssertDirectionClose(Vector3.Forward, intent.DesiredFacingDirection);
            Assert.True(intent.FacingReached);
            Assert.Same(rig.Target, rig.Navigation.Target);

            await StepNavigationAsync(sceneTree, rig, 0.1);

            NavigationMotionIntent afterStep = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.True(
                GetWorldTransform(rig.Target).Origin.X > 0.05f,
                $"Expected translation; position={GetWorldTransform(rig.Target).Origin}, speed={rig.Navigation.MovementSpeed}, travel={afterStep.TravelDirection}, next={afterStep.NextPathPosition}, valid={afterStep.HasValidSample}, reached={afterStep.PositionReached}.");
            AssertBasisClose(initialBasis, GetWorldTransform(rig.Target).Basis);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies positional arrival exposes turn-in-place intent and combined completion waits for yaw tolerance.
    /// </summary>
    [Headless]
    [Fact]
    public async Task FinalTurn_PositionReachedKeepsTurningUntilCombinedToleranceCompletes()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            rig.Navigation.AngularSpeedDegrees = 30.0f;
            rig.Navigation.FacingToleranceDegrees = 1.0f;
            Transform3D destination = FacingTransform(Vector3.Right, new Vector3(1.2f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            _ = await PollUntilValidAsync(sceneTree, rig);

            SetWorldTransform(rig.Target, new Transform3D(GetWorldTransform(rig.Target).Basis, destination.Origin));
            NavigationMotionIntent arrived = rig.Navigation.Poll(GetWorldTransform(rig.Target));

            Assert.True(arrived.HasValidSample);
            Assert.True(arrived.PositionReached);
            Assert.False(arrived.FacingReached);
            Assert.False(arrived.IsComplete);
            AssertVectorClose(Vector3.Zero, arrived.TravelDirection, DirectionTolerance);
            Assert.True(rig.Navigation.IsNavigationRunning);
            Assert.False(((INavigation)rig.Navigation).IsNavigationFinished);

            Vector3 fixedPosition = GetWorldTransform(rig.Target).Origin;
            int steps = 0;
            while (!((INavigation)rig.Navigation).IsNavigationFinished && steps++ < 100)
            {
                await StepNavigationAsync(sceneTree, rig, 0.1);
            }

            NavigationMotionIntent completed = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.True(completed.PositionReached);
            Assert.True(completed.FacingReached);
            Assert.True(completed.IsComplete);
            Assert.False(rig.Navigation.IsNavigationRunning);
            Assert.True(((INavigation)rig.Navigation).IsNavigationFinished);
            AssertVectorClose(fixedPosition, GetWorldTransform(rig.Target).Origin, PositionTolerance);
            Assert.InRange(Mathf.Abs(Mathf.RadToDeg(completed.SignedYawError)), 0.0f, rig.Navigation.FacingToleranceDegrees);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a same-position destination produces only terminal turn-in-place intent.
    /// </summary>
    [Headless]
    [Fact]
    public async Task SamePositionDestination_PollsTerminalTurnInPlaceIntent()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Transform3D destination = FacingTransform(Vector3.Left, GetWorldTransform(rig.Target).Origin);
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));

            NavigationMotionIntent intent = await PollUntilValidAsync(sceneTree, rig);

            Assert.True(intent.HasValidSample);
            Assert.True(intent.PositionReached);
            Assert.False(intent.FacingReached);
            Assert.False(intent.IsComplete);
            AssertVectorClose(Vector3.Zero, intent.TravelDirection, DirectionTolerance);
            AssertDirectionClose(Vector3.Left, intent.DesiredFacingDirection);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a real authored L-shaped navigation mesh changes path-facing intent around its interior corner.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AuthoredCornerPath_ChangesDesiredFacingBeforeOrAtInteriorWaypoint()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useCornerMesh: true);

        try
        {
            rig.Navigation.InitialFacingRampDistance = 0.0f;
            rig.Navigation.FacingRampDistance = 1.0f;
            rig.Navigation.ShortMoveDistance = 0.1f;
            Transform3D destination = FacingTransform(Vector3.Back, new Vector3(3.5f, 0.0f, 3.5f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));

            NavigationMotionIntent first = await PollUntilValidAsync(sceneTree, rig);
            Assert.True(first.HasValidSample);
            Assert.True(rig.Navigation.CurrentPath.Length >= 3, "Authored L mesh must produce an interior corner path.");
            Vector3 firstFacing = first.DesiredFacingDirection;
            bool changedFacing = false;

            for (int index = 0; index < 80 && !first.PositionReached; index++)
            {
                Transform3D actor = GetWorldTransform(rig.Target);
                SetWorldTransform(rig.Target, new Transform3D(actor.Basis, actor.Origin.MoveToward(first.NextPathPosition, 0.12f)));
                first = rig.Navigation.Poll(GetWorldTransform(rig.Target));
                changedFacing |= NavigationSteering.SignedYaw(firstFacing, first.DesiredFacingDirection) is > 0.2f or < -0.2f;
                if (changedFacing)
                {
                    break;
                }
            }

            Assert.True(changedFacing, "Expected real path-facing intent to change around the authored interior corner.");
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies world-yaw turning preserves the complete world basis beneath a rotated, non-uniformly scaled parent.
    /// </summary>
    [Headless]
    [Fact]
    public async Task NonUniformTransformedParent_GradualWorldYawPreservesWorldBasisAndCompletes()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Node3D transformedParent = new()
            {
                Name = "TransformedParent",
                Transform = new Transform3D(
                    new Basis(Vector3.Up, 0.65f) * Basis.FromScale(new Vector3(1.5f, 1.0f, 0.75f)),
                    new Vector3(0.1f, 0.0f, 0.1f)),
            };
            rig.Root.AddChild(transformedParent);
            rig.Target.Reparent(transformedParent, keepGlobalTransform: true);
            rig.Target.Transform = new Transform3D(
                new Basis(Vector3.Right, 0.25f)
                    * new Basis(Vector3.Forward, -0.15f)
                    * Basis.FromScale(new Vector3(1.2f, 0.85f, 1.4f)),
                transformedParent.ToLocal(Vector3.Zero));
            transformedParent.ForceUpdateTransform();
            rig.Target.ForceUpdateTransform();
            await WaitForNextFrameAsync(sceneTree);

            rig.Navigation.AngularSpeedDegrees = 90.0f;
            rig.Navigation.FacingToleranceDegrees = 0.5f;
            Transform3D initialWorld = GetWorldTransform(rig.Target);
            Transform3D destination = FacingTransform(Vector3.Right, initialWorld.Origin);
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent initialIntent = await PollUntilValidAsync(sceneTree, rig);
            Assert.False(initialIntent.IsComplete);

            float yawStep = Mathf.Clamp(
                initialIntent.SignedYawError,
                -Mathf.DegToRad(9.0f),
                Mathf.DegToRad(9.0f));
            Basis expectedAfterFirstStep = new Basis(Vector3.Up, yawStep) * initialWorld.Basis;
            rig.Navigation._PhysicsProcess(0.1);
            AssertBasisClose(expectedAfterFirstStep, GetWorldTransform(rig.Target).Basis);
            AssertBasisMetricPreserved(initialWorld.Basis, GetWorldTransform(rig.Target).Basis);

            for (int index = 0; index < 40 && !((INavigation)rig.Navigation).IsNavigationFinished; index++)
            {
                rig.Navigation._PhysicsProcess(0.1);
            }

            NavigationMotionIntent completed = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Basis worldBasis = GetWorldTransform(rig.Target).Basis;
            Assert.True(completed.IsComplete);
            AssertDirectionClose(Vector3.Right, GetHorizontalFacing(worldBasis));
            AssertBasisMetricPreserved(initialWorld.Basis, worldBasis);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies replacement and clearing reset sample state without publishing stale completion.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ClearAndReplacement_ResetCachedIntentAndCompletionState()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Transform3D firstDestination = FacingTransform(Vector3.Forward, new Vector3(1.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(firstDestination));
            NavigationMotionIntent first = await PollUntilValidAsync(sceneTree, rig);
            Assert.True(first.HasValidSample);

            Transform3D replacement = FacingTransform(Vector3.Left, new Vector3(2.0f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(replacement));
            AssertTransformClose(replacement, rig.Navigation.Destination);
            Assert.True(rig.Navigation.IsNavigationRunning);
            Assert.False(((INavigation)rig.Navigation).IsNavigationFinished);

            NavigationMotionIntent replacementIntent = await PollUntilValidAsync(sceneTree, rig);
            Assert.True(replacementIntent.HasValidSample);
            Assert.False(replacementIntent.IsComplete);

            rig.Navigation.ClearDestination();
            NavigationMotionIntent cleared = rig.Navigation.Poll(GetWorldTransform(rig.Target));
            Assert.False(rig.Navigation.HasDestination);
            Assert.False(cleared.HasValidSample);
            Assert.True(cleared.IsComplete);
            Assert.True(((INavigation)rig.Navigation).IsNavigationFinished);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a synced-map unreachable request is rejected atomically after a valid accepted request.
    /// </summary>
    [Headless]
    [Fact]
    public async Task SyncedMapInitiallyUnreachableRequest_PreservesAcceptedDestinationAndIntentAtomically()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);

        try
        {
            Transform3D acceptedDestination = FacingTransform(Vector3.Forward, new Vector3(1.5f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(acceptedDestination));
            NavigationMotionIntent acceptedIntent = await PollUntilValidAsync(sceneTree, rig);
            Assert.True(acceptedIntent.HasValidSample);

            Transform3D unreachable = FacingTransform(Vector3.Left, new Vector3(20.0f, 0.0f, 20.0f));
            NavigationDestinationResult result = rig.Navigation.SetDestination(unreachable);

            Assert.Equal(NavigationDestinationResult.Unreachable, result);
            AssertTransformClose(acceptedDestination, rig.Navigation.Destination);
            Assert.Equal(acceptedIntent.NextPathPosition, rig.Navigation.Poll(GetWorldTransform(rig.Target)).NextPathPosition);
            Assert.True(rig.Navigation.HasDestination);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a map-not-ready replacement preserves every part of an active accepted request atomically.
    /// </summary>
    [Headless]
    [Fact]
    public async Task MapNotReadyReplacement_PreservesActiveRequestTargetPathAndIntent()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree);
        Rid unavailableMap = NavigationServer3D.MapCreate();

        try
        {
            Transform3D acceptedDestination = FacingTransform(Vector3.Forward, new Vector3(1.5f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(acceptedDestination));
            NavigationMotionIntent acceptedIntent = await PollUntilValidAsync(sceneTree, rig);
            Vector3 acceptedTargetPosition = rig.Navigation.TargetPosition;
            Vector3[] acceptedPath = [.. rig.Navigation.CurrentPath];
            int acceptedPathIndex = rig.Navigation.CurrentPathIndex;

            rig.Navigation.SetNavigationMap(unavailableMap);
            Assert.Equal(0U, NavigationServer3D.MapGetIterationId(unavailableMap));
            Transform3D replacement = FacingTransform(Vector3.Left, new Vector3(2.0f, 0.0f, 0.0f));

            NavigationDestinationResult result = rig.Navigation.SetDestination(replacement);

            Assert.Equal(NavigationDestinationResult.NotReady, result);
            AssertTransformClose(acceptedDestination, rig.Navigation.Destination);
            Assert.Equal(acceptedTargetPosition, rig.Navigation.TargetPosition);
            Assert.Equal(acceptedPath, rig.Navigation.CurrentPath);
            Assert.Equal(acceptedPathIndex, rig.Navigation.CurrentPathIndex);
            AssertIntentEqual(acceptedIntent, rig.Navigation.Poll(GetWorldTransform(rig.Target)));
            Assert.True(rig.Navigation.HasDestination);
        }
        finally
        {
            rig.Navigation.SetNavigationMap(rig.NavigationMap);
            NavigationServer3D.FreeRid(unavailableMap);
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies synchronous reachability starts at the direct target despite stale server-agent position on another island.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReachabilityValidation_UsesAuthoritativeActorStartInsteadOfServerAgentPosition()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useDisconnectedMesh: true);

        try
        {
            var actorPosition = new Vector3(10.0f, 0.0f, 0.0f);
            SetWorldTransform(rig.Target, FacingTransform(Vector3.Forward, actorPosition));
            Vector3 staleServerPosition = NavigationServer3D.AgentGetPosition(rig.Navigation.GetRid());
            Assert.True(staleServerPosition.DistanceTo(actorPosition) > 5.0f);
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(10.5f, 0.0f, 0.0f));
            Vector3[] stalePath = NavigationServer3D.MapGetPath(
                rig.NavigationMap,
                NavigationServer3D.AgentGetPosition(rig.Navigation.GetRid()),
                destination.Origin,
                true,
                rig.Navigation.NavigationLayers);
            Assert.NotEmpty(stalePath);
            Assert.True(stalePath[^1].DistanceTo(destination.Origin) > rig.Navigation.DestinationReachedDistance);
            AssertVectorClose(actorPosition, GetWorldTransform(rig.Target).Origin, PositionTolerance);

            NavigationDestinationResult result = rig.Navigation.SetDestination(destination);

            Assert.Equal(NavigationDestinationResult.Accepted, result);
            AssertTransformClose(destination, rig.Navigation.Destination);
            Assert.NotEmpty(rig.Navigation.CurrentPath);
            AssertVectorClose(destination.Origin, rig.Navigation.CurrentPath[^1], PositionTolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies the implementation-agnostic base resolves its Node3D ancestor world position without server-agent state.
    /// </summary>
    [Headless]
    [Fact]
    public async Task NavigationBase_DefaultStartUsesNodeWorldPositionInsteadOfServerAgentPosition()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "MinimalNavigationRoot"
        };
        NavigationRegion3D region = new()
        {
            Name = "NavigationRegion3D",
            NavigationMesh = CreateDisconnectedNavigationMesh(),
        };
        Node3D actor = new()
        {
            Name = "Actor"
        };
        MinimalNavigation navigation = new()
        {
            Name = "Navigation",
            DestinationReachedDistance = 0.05f,
        };
        Rid navigationMap = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(navigationMap, true);
        region.SetNavigationMap(navigationMap);
        navigation.SetNavigationMap(navigationMap);
        root.AddChild(region);
        root.AddChild(actor);
        actor.AddChild(navigation);
        sceneTree.Root.AddChild(root);

        try
        {
            region.SetNavigationMap(navigationMap);
            navigation.SetNavigationMap(navigationMap);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (NavigationServer3D.MapGetPath(
                        navigationMap,
                        Vector3.Zero,
                        new Vector3(0.5f, 0.0f, 0.0f),
                        true).Length > 0)
                {
                    break;
                }

                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            navigation.SetPhysicsProcess(false);
            actor.Transform = new Transform3D(
                new Basis(Vector3.Right, 0.2f) * Basis.FromScale(new Vector3(1.2f, 0.9f, 1.4f)),
                new Vector3(10.0f, 0.0f, 0.0f));
            Vector3 serverPosition = NavigationServer3D.AgentGetPosition(navigation.GetRid());

            Assert.True(serverPosition.DistanceTo(actor.Position) > 5.0f);
            AssertVectorClose(actor.Position, navigation.ExposedNavigationStartPosition, PositionTolerance);
            Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(10.5f, 0.0f, 0.0f));
            Assert.Equal(NavigationDestinationResult.Accepted, navigation.SetDestination(destination));
            Transform3D actorBeforePoll = actor.Transform;

            NavigationMotionIntent intent = navigation.Poll(actor.Transform);

            Assert.True(intent.HasValidSample);
            Assert.Equal(actorBeforePoll, actor.Transform);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
            NavigationServer3D.FreeRid(navigationMap);
        }
    }

    /// <summary>
    /// Verifies a large direct-transform step stops at the current path point on a route with downstream distance.
    /// </summary>
    [Headless]
    [Fact]
    public async Task LargeDelta_StopsAtCurrentNextPathPositionWithoutOvershoot()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = await CreateRigAsync(sceneTree, useCornerMesh: true);

        try
        {
            rig.Navigation.MovementSpeed = 100.0f;
            Transform3D destination = FacingTransform(Vector3.Back, new Vector3(3.5f, 0.0f, 3.5f));
            Assert.Equal(NavigationDestinationResult.Accepted, rig.Navigation.SetDestination(destination));
            NavigationMotionIntent before = await PollUntilValidAsync(sceneTree, rig);
            float segmentDistance = GetWorldTransform(rig.Target).Origin.DistanceTo(before.NextPathPosition);
            Assert.True(before.RemainingPathDistance > segmentDistance + 0.1f);

            await StepNavigationAsync(sceneTree, rig, 1.0);

            AssertVectorClose(before.NextPathPosition, GetWorldTransform(rig.Target).Origin, PositionTolerance);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    /// <summary>
    /// Verifies a pre-synchronisation request is not committed and cannot translate or rotate its actor.
    /// </summary>
    [Headless]
    [Fact]
    public async Task RequestBeforeMapSync_ReturnsNotReadyWithoutCommitOrActorMutation()
    {
        SceneTree sceneTree = GetSceneTree();
        NavigationTestRig rig = CreateUnattachedRig();

        try
        {
            Transform3D initial = GetWorldTransform(rig.Target);
            Transform3D destination = FacingTransform(Vector3.Right, new Vector3(2.0f, 0.0f, 0.0f));
            Transform3D initialDestination = rig.Navigation.Destination;
            Vector3 initialTargetPosition = rig.Navigation.TargetPosition;
            NavigationMotionIntent initialIntent = rig.Navigation.Poll(initial);

            Assert.Equal(NavigationDestinationResult.NotReady, rig.Navigation.SetDestination(destination));

            rig.Navigation._PhysicsProcess(1.0);

            Assert.False(rig.Navigation.HasDestination);
            Assert.Equal(initialDestination, rig.Navigation.Destination);
            Assert.Equal(initialTargetPosition, rig.Navigation.TargetPosition);
            Assert.Empty(rig.Navigation.CurrentPath);
            AssertIntentEqual(initialIntent, rig.Navigation.Poll(initial));
            AssertTransformClose(initial, GetWorldTransform(rig.Target));

            sceneTree.Root.AddChild(rig.Host);
            await WaitForNextFrameAsync(sceneTree);
            BindCustomNavigationMap(rig);
            await WaitForNavigationMapAsync(sceneTree, rig);
            Assert.False(rig.Navigation.HasDestination);
            Assert.True(rig.Navigation.Poll(GetWorldTransform(rig.Target)).IsComplete);
        }
        finally
        {
            await DestroyRigAsync(sceneTree, rig);
        }
    }

    private static async Task<NavigationTestRig> CreateRigAsync(
        SceneTree sceneTree,
        bool useCornerMesh = false,
        bool useExplicitTarget = true,
        bool useIntermediateNode = false,
        bool useDisconnectedMesh = false,
        bool useScriptedPath = false)
    {
        NavigationTestRig rig = CreateUnattachedRig(
            useCornerMesh,
            useExplicitTarget,
            useIntermediateNode,
            useDisconnectedMesh,
            useScriptedPath);
        sceneTree.Root.AddChild(rig.Host);
        await WaitForNextFrameAsync(sceneTree);
        BindCustomNavigationMap(rig);
        await WaitForNavigationMapAsync(sceneTree, rig);
        return rig;
    }

    private static async Task WaitForNavigationMapAsync(SceneTree sceneTree, NavigationTestRig rig)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Vector3[] path = NavigationServer3D.MapGetPath(
                rig.NavigationMap,
                Vector3.Zero,
                new Vector3(0.5f, 0.0f, 0.0f),
                true);
            if (path.Length > 0)
            {
                return;
            }

            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }
    }

    private static async Task<NavigationMotionIntent> PollUntilValidAsync(SceneTree sceneTree, NavigationTestRig rig)
    {
        float movementSpeed = rig.Navigation.MovementSpeed;
        float angularSpeed = rig.Navigation.AngularSpeedDegrees;
        rig.Navigation.MovementSpeed = 0.0f;
        rig.Navigation.AngularSpeedDegrees = 0.0f;
        rig.Navigation.SetPhysicsProcess(true);
        rig.Navigation.TargetPosition = rig.Navigation.Destination.Origin;
        try
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                NavigationMotionIntent intent = rig.Navigation.Poll(GetWorldTransform(rig.Target));
                if (intent.HasValidSample)
                {
                    return intent;
                }

                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            return rig.Navigation.Poll(GetWorldTransform(rig.Target));
        }
        finally
        {
            rig.Navigation.SetPhysicsProcess(false);
            rig.Navigation.MovementSpeed = movementSpeed;
            rig.Navigation.AngularSpeedDegrees = angularSpeed;
        }
    }

    private static async Task StepNavigationAsync(SceneTree sceneTree, NavigationTestRig rig, double delta)
    {
        rig.Navigation._PhysicsProcess(delta);
        await WaitForPhysicsFramesAsync(sceneTree, 1);
    }

    private static NavigationTestRig CreateUnattachedRig(
        bool useCornerMesh = false,
        bool useExplicitTarget = true,
        bool useIntermediateNode = false,
        bool useDisconnectedMesh = false,
        bool useScriptedPath = false)
    {
        Node3D root = new()
        {
            Name = "NavigationTestRoot",
            ProcessMode = Node.ProcessModeEnum.Always,
        };
        NavigationRegion3D region = new()
        {
            Name = "NavigationRegion3D",
            NavigationMesh = useDisconnectedMesh
                ? CreateDisconnectedNavigationMesh()
                : useCornerMesh
                    ? CreateCornerNavigationMesh()
                    : CreatePlaneNavigationMesh(),
        };
        Node3D target = new()
        {
            Name = "Target",
            Transform = FacingTransform(Vector3.Forward, Vector3.Zero),
        };
        DirectTransformNavigation navigation = useScriptedPath
            ? new ScriptedDirectTransformNavigation()
            : new DirectTransformNavigation();
        navigation.Name = "Navigation";
        navigation.Target = useExplicitTarget ? target : null;
        navigation.ProcessMode = Node.ProcessModeEnum.Always;
        navigation.MovementSpeed = 2.0f;
        navigation.AngularSpeedDegrees = 90.0f;
        navigation.FacingToleranceDegrees = 1.0f;
        navigation.NavigationLayers = 1U;
        navigation.PathMetadataFlags = NavigationPathQueryParameters3D.PathMetadataFlags.None;
        navigation.PathPostprocessing = NavigationPathQueryParameters3D.PathPostProcessing.Edgecentered;
        Rid navigationMap = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(navigationMap, true);
        region.SetNavigationMap(navigationMap);
        navigation.SetNavigationMap(navigationMap);
        root.AddChild(region);
        root.AddChild(target);
        if (useIntermediateNode)
        {
            Node intermediate = new()
            {
                Name = "Intermediate"
            };
            target.AddChild(intermediate);
            intermediate.AddChild(navigation);
        }
        else
        {
            target.AddChild(navigation);
        }
        return new NavigationTestRig(root, root, navigationMap, target, navigation);
    }

    private static void BindCustomNavigationMap(NavigationTestRig rig)
    {
        NavigationRegion3D region = rig.Root.GetNode<NavigationRegion3D>("NavigationRegion3D");
        region.SetNavigationMap(rig.NavigationMap);
        rig.Navigation.SetNavigationMap(rig.NavigationMap);
        rig.Target.Transform = FacingTransform(Vector3.Forward, Vector3.Zero);
        NavigationServer3D.AgentSetPosition(rig.Navigation.GetRid(), GetWorldTransform(rig.Target).Origin);
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

    private static NavigationMesh CreateCornerNavigationMesh()
    {
        NavigationMesh mesh = new();
        mesh.SetVertices([
            new Vector3(-1.0f, 0.0f, -1.0f),
            new Vector3(3.0f, 0.0f, -1.0f),
            new Vector3(3.0f, 0.0f, 1.0f),
            new Vector3(-1.0f, 0.0f, 1.0f),
            new Vector3(4.0f, 0.0f, -1.0f),
            new Vector3(4.0f, 0.0f, 1.0f),
            new Vector3(4.0f, 0.0f, 4.0f),
            new Vector3(3.0f, 0.0f, 4.0f),
        ]);
        mesh.AddPolygon([0, 1, 2, 3]);
        mesh.AddPolygon([1, 4, 5, 2]);
        mesh.AddPolygon([2, 5, 6, 7]);
        return mesh;
    }

    private static NavigationMesh CreateDisconnectedNavigationMesh()
    {
        NavigationMesh mesh = CreatePlaneNavigationMesh();
        Vector3[] mainVertices = mesh.GetVertices();
        mesh.SetVertices([
            .. mainVertices,
            new Vector3(9.0f, 0.0f, -1.0f),
            new Vector3(11.0f, 0.0f, -1.0f),
            new Vector3(11.0f, 0.0f, 1.0f),
            new Vector3(9.0f, 0.0f, 1.0f),
        ]);
        mesh.AddPolygon([4, 5, 6]);
        mesh.AddPolygon([4, 6, 7]);
        return mesh;
    }

    private static async Task DestroyRigAsync(SceneTree sceneTree, NavigationTestRig rig)
    {
        if (GodotObject.IsInstanceValid(rig.Host) && rig.Host.IsInsideTree())
        {
            rig.Host.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
        else if (GodotObject.IsInstanceValid(rig.Host))
        {
            rig.Host.Free();
        }

        NavigationServer3D.FreeRid(rig.NavigationMap);
    }

    private static Transform3D FacingTransform(Vector3 facing, Vector3 origin)
    {
        Vector3 stableFacing = facing.Normalized();
        Vector3 right = stableFacing.Cross(Vector3.Up).Normalized();
        return new Transform3D(new Basis(right, Vector3.Up, -stableFacing), origin);
    }

    private static Transform3D GetWorldTransform(Node3D node)
    {
        Transform3D transform = node.Transform;
        Node3D? parent = node.GetParentOrNull<Node3D>();
        while (parent is not null && !node.TopLevel)
        {
            transform = parent.Transform * transform;
            node = parent;
            parent = parent.GetParentOrNull<Node3D>();
        }

        return transform;
    }

    private static void SetWorldTransform(Node3D node, Transform3D worldTransform)
    {
        Node3D? parent = node.GetParentOrNull<Node3D>();
        node.Transform = parent is null || node.TopLevel
            ? worldTransform
            : GetWorldTransform(parent).AffineInverse() * worldTransform;
    }

    private static void SetActorPosition(NavigationTestRig rig, Vector3 position)
    {
        Transform3D actor = GetWorldTransform(rig.Target);
        SetWorldTransform(rig.Target, new Transform3D(actor.Basis, position));
        NavigationServer3D.AgentSetPosition(rig.Navigation.GetRid(), position);
    }

    private static float InitialFacingWeight(NavigationMotionIntent intent, float rampDistance)
    {
        float expected = NavigationSteering.SmoothstepRatio(intent.TravelledPathDistance, rampDistance);
        float actual = Mathf.Abs(NavigationSteering.SignedYaw(Vector3.Forward, intent.DesiredFacingDirection))
            / (Mathf.Pi * 0.5f);
        Assert.Equal(expected, actual, 3);
        return actual;
    }

    private static float RampWeight(NavigationMotionIntent intent, float rampDistance)
        => NavigationSteering.SmoothstepRatio(intent.TravelledPathDistance, rampDistance);

    private static Vector3 GetHorizontalFacing(Basis basis)
    {
        Vector3 facing = -basis.Z;
        facing.Y = 0.0f;
        return facing.Normalized();
    }

    private static Vector3 GetHorizontalDirection(Vector3 direction)
    {
        direction.Y = 0.0f;
        return direction.Normalized();
    }

    private static void AssertBasisMetricPreserved(Basis expected, Basis actual)
    {
        const float tolerance = 0.002f;
        Assert.InRange(Mathf.Abs(expected.X.Length() - actual.X.Length()), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(expected.Y.Length() - actual.Y.Length()), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(expected.Z.Length() - actual.Z.Length()), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(expected.X.Dot(expected.Y) - actual.X.Dot(actual.Y)), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(expected.X.Dot(expected.Z) - actual.X.Dot(actual.Z)), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(expected.Y.Dot(expected.Z) - actual.Y.Dot(actual.Z)), 0.0f, tolerance);
    }

    private static void AssertTransformClose(Transform3D expected, Transform3D actual)
    {
        AssertVectorClose(expected.Origin, actual.Origin, PositionTolerance);
        AssertBasisClose(expected.Basis, actual.Basis);
    }

    private static void AssertIntentEqual(NavigationMotionIntent expected, NavigationMotionIntent actual)
    {
        Assert.Equal(expected.NextPathPosition, actual.NextPathPosition);
        Assert.Equal(expected.TravelDirection, actual.TravelDirection);
        Assert.Equal(expected.DesiredFacingDirection, actual.DesiredFacingDirection);
        Assert.Equal(expected.SignedYawError, actual.SignedYawError);
        Assert.Equal(expected.RemainingPathDistance, actual.RemainingPathDistance);
        Assert.Equal(expected.TravelledPathDistance, actual.TravelledPathDistance);
        Assert.Equal(expected.PositionReached, actual.PositionReached);
        Assert.Equal(expected.FacingReached, actual.FacingReached);
        Assert.Equal(expected.IsComplete, actual.IsComplete);
        Assert.Equal(expected.HasValidSample, actual.HasValidSample);
    }

    private static void AssertBasisClose(Basis expected, Basis actual)
    {
        AssertVectorClose(expected.X, actual.X, DirectionTolerance);
        AssertVectorClose(expected.Y, actual.Y, DirectionTolerance);
        AssertVectorClose(expected.Z, actual.Z, DirectionTolerance);
    }

    private static bool IsBasisClose(Basis expected, Basis actual)
        => expected.X.DistanceTo(actual.X) <= DirectionTolerance
            && expected.Y.DistanceTo(actual.Y) <= DirectionTolerance
            && expected.Z.DistanceTo(actual.Z) <= DirectionTolerance;

    private static void AssertDirectionClose(Vector3 expected, Vector3 actual)
        => AssertVectorClose(expected.Normalized(), actual.Normalized(), DirectionTolerance);

    private static bool IsDirectionClose(Vector3 expected, Vector3 actual)
        => expected.Normalized().DistanceTo(actual.Normalized()) <= DirectionTolerance;

    private static void AssertVectorClose(Vector3 expected, Vector3 actual, float tolerance)
        => Assert.True(expected.DistanceTo(actual) <= tolerance, $"Expected {actual} to be within {tolerance} of {expected}.");

    private sealed class NavigationTestRig(
        Node3D host,
        Node3D root,
        Rid navigationMap,
        Node3D target,
        DirectTransformNavigation navigation)
    {
        public Node3D Host => host;

        public Node3D Root => root;

        public Rid NavigationMap => navigationMap;

        public Node3D Target => target;

        public DirectTransformNavigation Navigation => navigation;
    }

    private sealed partial class MinimalNavigation : NavigationBase
    {
        public Vector3 ExposedNavigationStartPosition => GetNavigationStartPosition();
    }

    private sealed partial class ScriptedDirectTransformNavigation : DirectTransformNavigation
    {
        private Vector3[]? _path;
        private int _pathIndex;

        public void SetPath(Vector3[] path, int pathIndex)
        {
            _path = path;
            _pathIndex = pathIndex;
        }

        protected override void AdjustPathSample(
            ref Vector3 nextPathPosition,
            ref Vector3[] path,
            ref int pathIndex)
        {
            if (_path is null)
            {
                return;
            }

            path = _path;
            pathIndex = Math.Clamp(_pathIndex, 0, path.Length);
            nextPathPosition = path.Length == 0
                ? nextPathPosition
                : path[Math.Clamp(pathIndex, 0, path.Length - 1)];
        }
    }
}
