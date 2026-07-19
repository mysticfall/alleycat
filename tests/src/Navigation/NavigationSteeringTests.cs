using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Pure deterministic coverage for NAV-001 steering policy.
/// </summary>
public sealed class NavigationSteeringTests
{
    private const float Tolerance = 0.0001f;

    /// <inheritdoc/>
    [Fact]
    public void Calculate_InitialRamp_UsesSmoothstepTravelledPathDistance()
    {
        Transform3D actor = FacingTransform(Vector3.Right, new Vector3(0.0f, 0.0f, -5.0f));
        Vector3[] path = [new(0.0f, 0.0f, -10.0f)];

        NavigationMotionIntent intent = Calculate(
            actor,
            FacingTransform(Vector3.Forward, path[0]),
            path[0],
            path,
            0,
            Vector3.Right,
            10.0f,
            10.0f,
            travelledDistance: 5.0f);

        AssertVectorClose(new Vector3(Mathf.Sqrt(0.5f), 0.0f, -Mathf.Sqrt(0.5f)), intent.DesiredFacingDirection);
        Assert.Equal(5.0f, intent.TravelledPathDistance);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_AcceptedFallbackToPublishedPath_PreservesInitialProgressAndFacingWeight()
    {
        Vector3[] acceptedPath = [new(2.0f, 0.0f, 0.0f), new(4.0f, 0.0f, 0.0f)];
        Vector3[] publishedPath = [new(1.0f, 0.0f, 0.0f), new(4.0f, 0.0f, 0.0f)];

        NavigationMotionIntent fallback = CalculateRoute(acceptedPath, 0, 0.75f);
        NavigationMotionIntent published = CalculateRoute(publishedPath, 0, 0.75f);

        Assert.Equal(fallback.TravelledPathDistance, published.TravelledPathDistance);
        AssertVectorClose(fallback.DesiredFacingDirection, published.DesiredFacingDirection);
        AssertInitialWeight(0.75f, published);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_LongerReplan_DoesNotRewindInitialProgressOrFacingWeight()
    {
        NavigationMotionIntent before = CalculateRoute([new(2.0f, 0.0f, 0.0f)], 0, 0.8f);
        NavigationMotionIntent longer = CalculateRoute([new(2.0f, 0.0f, 0.0f), new(8.0f, 0.0f, 0.0f)], 0, 0.8f);

        Assert.True(longer.RemainingPathDistance > before.RemainingPathDistance);
        Assert.Equal(before.TravelledPathDistance, longer.TravelledPathDistance);
        AssertVectorClose(before.DesiredFacingDirection, longer.DesiredFacingDirection);
        AssertInitialWeight(0.8f, longer);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_ShorterReplan_DoesNotJumpOrRewindInitialProgress()
    {
        NavigationMotionIntent before = CalculateRoute([new(2.0f, 0.0f, 0.0f), new(8.0f, 0.0f, 0.0f)], 0, 0.8f);
        NavigationMotionIntent shorter = CalculateRoute([new(2.0f, 0.0f, 0.0f)], 0, 0.8f);

        Assert.True(shorter.RemainingPathDistance < before.RemainingPathDistance);
        Assert.Equal(before.TravelledPathDistance, shorter.TravelledPathDistance);
        AssertVectorClose(before.DesiredFacingDirection, shorter.DesiredFacingDirection);
        AssertInitialWeight(0.8f, shorter);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_CurrentPathIndexTransition_UsesMonotonicProgressAndCurrentDownstreamPath()
    {
        Vector3[] path = [new(1.0f, 0.0f, 0.0f), new(3.0f, 0.0f, 0.0f)];
        NavigationMotionIntent first = CalculateRoute(path, 0, 0.6f);
        NavigationMotionIntent advanced = CalculateRoute(path, 1, 0.9f);

        Assert.True(advanced.TravelledPathDistance > first.TravelledPathDistance);
        AssertInitialWeight(0.9f, advanced);
        Assert.True(advanced.RemainingPathDistance <= first.RemainingPathDistance);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_EmptySinglePointAndDegeneratePaths_KeepProgressFiniteAndMonotonic()
    {
        NavigationMotionIntent empty = CalculateRoute([], 0, 0.4f);
        NavigationMotionIntent single = CalculateRoute([Vector3.Zero], 0, 0.4f);
        NavigationMotionIntent degenerate = CalculateRoute(
            [Vector3.Zero, new Vector3(float.NaN, 0.0f, 0.0f), Vector3.Zero],
            99,
            0.7f);

        Assert.True(float.IsFinite(empty.TravelledPathDistance));
        Assert.Equal(empty.TravelledPathDistance, single.TravelledPathDistance);
        Assert.True(degenerate.TravelledPathDistance >= single.TravelledPathDistance);
        Assert.True(float.IsFinite(degenerate.RemainingPathDistance));
        Assert.True(degenerate.DesiredFacingDirection.IsFinite());
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_OverlappingRamps_LaterTerminalRampTakesPrecedence()
    {
        Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
        Transform3D destination = FacingTransform(Vector3.Left, new Vector3(1.0f, 0.0f, -3.0f));
        Vector3 next = new(0.0f, 0.0f, -1.0f);
        Vector3[] pathA = [next, new(0.0f, 0.0f, -2.0f), new(1.0f, 0.0f, -2.0f), destination.Origin];

        NavigationMotionIntent a = Calculate(actor, destination, next, pathA, 0, Vector3.Forward, 20.0f, 0.0f, 5.0f);
        float terminalWeight = NavigationSteering.SmoothstepRatio(1.0f, 5.0f);
        float terminalYaw = NavigationSteering.SignedYaw(Vector3.Forward, Vector3.Left);
        Vector3 expected = Vector3.Forward.Rotated(Vector3.Up, terminalYaw * terminalWeight);

        AssertVectorClose(expected, a.DesiredFacingDirection);
        Assert.True(a.DesiredFacingDirection.X < 0.0f);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_InteriorWaypointRamp_UsesOutgoingSegmentBearing()
    {
        Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
        Vector3 next = new(0.0f, 0.0f, -1.0f);
        Vector3[] path = [next, new(3.0f, 0.0f, -1.0f), new(10.0f, 0.0f, -1.0f)];
        Transform3D destination = FacingTransform(Vector3.Right, path[^1]);

        NavigationMotionIntent intent = Calculate(actor, destination, next, path, 0, Vector3.Forward, 20.0f, 0.0f, 2.0f);

        AssertVectorClose(new Vector3(Mathf.Sqrt(0.5f), 0.0f, -Mathf.Sqrt(0.5f)), intent.DesiredFacingDirection);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_ShortRouteAtOrBelowThreshold_UsesTerminalFacing()
    {
        foreach (float initialLength in new[] { 2.0f, 1.5f })
        {
            Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
            Transform3D destination = FacingTransform(Vector3.Right, new Vector3(2.0f, 0.0f, 0.0f));
            Vector3[] path = [destination.Origin];

            NavigationMotionIntent intent = Calculate(actor, destination, destination.Origin, path, 0, Vector3.Forward, initialLength, 10.0f, shortMoveDistance: 2.0f);

            AssertVectorClose(Vector3.Right, intent.DesiredFacingDirection);
            AssertVectorClose(Vector3.Right, intent.TravelDirection);
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_TravelAndFacing_AreIndependent()
    {
        Transform3D actor = FacingTransform(Vector3.Right, Vector3.Zero);
        Transform3D destination = FacingTransform(Vector3.Back, new Vector3(0.0f, 0.0f, -5.0f));
        Vector3[] path = [destination.Origin];

        NavigationMotionIntent intent = Calculate(actor, destination, destination.Origin, path, 0, Vector3.Right, 2.0f, 0.0f, shortMoveDistance: 3.0f);

        AssertVectorClose(Vector3.Forward, intent.TravelDirection);
        AssertVectorClose(Vector3.Back, intent.DesiredFacingDirection);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_PositionReachedWithFacingOutstanding_ReturnsTurnInPlace()
    {
        Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
        Transform3D destination = FacingTransform(Vector3.Right, Vector3.Zero);

        NavigationMotionIntent intent = Calculate(actor, destination, Vector3.Zero, [Vector3.Zero], 0, Vector3.Forward, 0.0f, 1.0f);

        Assert.True(intent.PositionReached);
        Assert.False(intent.FacingReached);
        Assert.False(intent.IsComplete);
        AssertVectorClose(Vector3.Zero, intent.TravelDirection);
        AssertVectorClose(Vector3.Right, intent.DesiredFacingDirection);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_TerminalPathProximityWithoutDestinationProximity_DoesNotReachPosition()
    {
        Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
        Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(0.2f, 0.0f, 0.0f));

        NavigationMotionIntent intent = NavigationSteering.Calculate(
            actor, destination, Vector3.Zero, [Vector3.Zero], 0, Vector3.Forward,
            0.2f, 0.0f, 1.0f, 1.0f, 0.5f, 0.05f, 0.05f, 0.01f);

        Assert.Equal(0.0f, intent.RemainingPathDistance);
        Assert.False(intent.PositionReached);
        Assert.False(intent.IsComplete);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_DestinationProximityAcrossCornerWithoutTerminalPathProximity_DoesNotReachPosition()
    {
        Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
        Transform3D destination = FacingTransform(Vector3.Forward, new Vector3(0.04f, 0.0f, 0.0f));
        Vector3 next = new(0.0f, 0.0f, -1.0f);
        Vector3[] path = [next, destination.Origin];

        NavigationMotionIntent intent = NavigationSteering.Calculate(
            actor, destination, next, path, 0, Vector3.Forward,
            2.0f, 0.0f, 1.0f, 1.0f, 0.5f, 0.05f, 0.05f, 0.01f);

        Assert.True(actor.Origin.DistanceTo(destination.Origin) <= 0.05f);
        Assert.True(intent.RemainingPathDistance > 0.05f);
        Assert.False(intent.PositionReached);
        Assert.False(intent.IsComplete);
        AssertVectorClose(Vector3.Forward, intent.TravelDirection);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_SamePositionEmptyPath_SatisfiesTerminalPathConditionForTurnInPlace()
    {
        Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
        Transform3D destination = FacingTransform(Vector3.Right, Vector3.Zero);

        NavigationMotionIntent intent = NavigationSteering.Calculate(
            actor, destination, Vector3.Zero, [], 0, Vector3.Forward,
            0.0f, 0.0f, 1.0f, 1.0f, 0.5f, 0.05f, 0.05f, 0.01f);

        Assert.True(intent.PositionReached);
        Assert.False(intent.FacingReached);
        Assert.False(intent.IsComplete);
        AssertVectorClose(Vector3.Zero, intent.TravelDirection);
        AssertVectorClose(Vector3.Right, intent.DesiredFacingDirection);
    }

    /// <inheritdoc/>
    [Fact]
    public void SignedYaw_WrapsAcrossPiUsingShortestAngle()
    {
        Vector3 from = Vector3.Forward.Rotated(Vector3.Up, Mathf.DegToRad(179.0f));
        Vector3 to = Vector3.Forward.Rotated(Vector3.Up, Mathf.DegToRad(-179.0f));

        float yaw = NavigationSteering.SignedYaw(from, to);

        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(yaw)), 1.999f, 2.001f);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_DegenerateNonFiniteInputs_ReturnsFiniteStableIntent()
    {
        float nan = float.NaN;
        var invalidBasis = new Basis(new Vector3(nan, 0.0f, 0.0f), Vector3.Zero, Vector3.Zero);
        var actor = new Transform3D(invalidBasis, new Vector3(nan, nan, nan));
        var destination = new Transform3D(invalidBasis, new Vector3(nan, nan, nan));

        NavigationMotionIntent intent = NavigationSteering.Calculate(
            actor, destination, new Vector3(nan, nan, nan), [new Vector3(nan, nan, nan)], 99,
            new Vector3(nan, nan, nan), nan, nan, nan, nan, nan, nan, nan, nan);

        Assert.True(intent.NextPathPosition.IsFinite());
        Assert.True(intent.TravelDirection.IsFinite());
        Assert.True(intent.DesiredFacingDirection.IsFinite());
        Assert.True(float.IsFinite(intent.SignedYawError));
        Assert.True(float.IsFinite(intent.RemainingPathDistance));
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_PositionAndFacingToleranceBoundaries_AreInclusive()
    {
        Transform3D actor = FacingTransform(Vector3.Forward, Vector3.Zero);
        Vector3 facing = Vector3.Forward.Rotated(Vector3.Up, 0.1f);
        Transform3D destination = FacingTransform(facing, new Vector3(1.0f, 0.0f, 0.0f));

        NavigationMotionIntent intent = NavigationSteering.Calculate(
            actor, destination, destination.Origin, [destination.Origin], 0, Vector3.Forward,
            1.0f, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f, 1.0f, 0.1f);

        Assert.True(intent.PositionReached);
        Assert.True(intent.FacingReached);
        Assert.True(intent.IsComplete);
    }

    /// <inheritdoc/>
    [Fact]
    public void LimitTravelDistance_LargeStepStopsAtCurrentThreeDimensionalPathPoint()
    {
        Vector3 current = Vector3.Zero;
        Vector3 next = new(0.0f, 3.0f, -4.0f);
        Vector3 destinationPosition = new(0.0f, 3.0f, -14.0f);
        NavigationMotionIntent intent = NavigationSteering.Calculate(
            FacingTransform(Vector3.Forward, current),
            FacingTransform(Vector3.Forward, destinationPosition),
            next,
            [next, destinationPosition],
            0,
            Vector3.Forward,
            15.0f,
            0.0f,
            1.0f,
            1.0f,
            0.0f,
            0.1f,
            0.1f,
            0.1f);

        float step = NavigationSteering.LimitTravelDistanceToNextPathPosition(20.0f, current, intent.NextPathPosition);

        Assert.Equal(15.0f, intent.RemainingPathDistance, 5);
        Assert.Equal(5.0f, step, 5);
    }

    /// <inheritdoc/>
    [Fact]
    public void Calculate_InvalidSample_ExplicitlyReportsNotReadyWithoutCompleting()
    {
        float nan = float.NaN;
        var actor = new Transform3D(Basis.Identity, new Vector3(nan, nan, nan));

        NavigationMotionIntent intent = NavigationSteering.Calculate(
            actor, Transform3D.Identity, Vector3.Zero, [Vector3.Zero], 0, Vector3.Forward,
            1.0f, 0.0f, 1.0f, 1.0f, 0.0f, 0.1f, 0.1f, 0.1f);

        Assert.False(intent.HasValidSample);
        Assert.False(intent.IsComplete);
    }

    private static NavigationMotionIntent Calculate(
        Transform3D actor,
        Transform3D destination,
        Vector3 next,
        Vector3[] path,
        int pathIndex,
        Vector3 initialFacing,
        float initialLength,
        float initialRamp,
        float downstreamRamp = 1.0f,
        float shortMoveDistance = 0.0f,
        float travelledDistance = 0.0f)
        => NavigationSteering.Calculate(
            actor, destination, next, path, pathIndex, initialFacing, initialLength,
            travelledDistance, initialRamp, downstreamRamp, shortMoveDistance, 0.05f, 0.05f, 0.01f);

    private static NavigationMotionIntent CalculateRoute(Vector3[] path, int pathIndex, float travelledDistance)
    {
        Vector3 next = path.Length == 0
            ? new Vector3(1.0f, 0.0f, 0.0f)
            : path[Math.Clamp(pathIndex, 0, path.Length - 1)];
        return Calculate(
            FacingTransform(Vector3.Forward, Vector3.Zero),
            FacingTransform(Vector3.Right, new Vector3(10.0f, 0.0f, 0.0f)),
            next,
            path,
            pathIndex,
            Vector3.Forward,
            10.0f,
            2.0f,
            downstreamRamp: 0.0f,
            travelledDistance: travelledDistance);
    }

    private static void AssertInitialWeight(float travelledDistance, NavigationMotionIntent intent)
    {
        float expectedWeight = NavigationSteering.SmoothstepRatio(travelledDistance, 2.0f);
        float actualWeight = Mathf.Abs(NavigationSteering.SignedYaw(Vector3.Forward, intent.DesiredFacingDirection))
            / (Mathf.Pi * 0.5f);
        Assert.Equal(expectedWeight, actualWeight, 4);
    }

    private static Transform3D FacingTransform(Vector3 facing, Vector3 origin)
    {
        Vector3 stableFacing = facing.Normalized();
        Vector3 right = stableFacing.Cross(Vector3.Up).Normalized();
        return new Transform3D(new Basis(right, Vector3.Up, -stableFacing), origin);
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(Mathf.Abs(expected.X - actual.X), 0.0f, Tolerance);
        Assert.InRange(Mathf.Abs(expected.Y - actual.Y), 0.0f, Tolerance);
        Assert.InRange(Mathf.Abs(expected.Z - actual.Z), 0.0f, Tolerance);
    }
}
