using System.Numerics;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Mutation coverage for the NAV-001 collision-realised raw-path ledger contract.
/// </summary>
public sealed class RoutePassageOracleTests
{
    /// <inheritdoc/>
    [Fact]
    public void Mutation_ProgressPlaneCrossingOutsideTolerance_FailsFinalAggregate()
    {
        RoutePassageOracle oracle = Activate(Route("request-a", 1, 1, Point("corner", 0, 1.0f, 0.0f)));

        RoutePassageSegmentDiagnostic diagnostic = oracle.ObserveCollisionRealisedSegment(
            Motion(new Vector3(0.0f, 0.0f, 0.25f), new Vector3(2.0f, 0.0f, 0.25f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        RoutePassagePointCheck check = Assert.Single(diagnostic.PointChecks);
        Assert.Equal("corner", check.PointID);
        Assert.Equal(0, check.Order);
        Assert.Equal(0.25f, check.SweptMinimumDistanceMetres, 4);
        Assert.False(check.Passed);
        Assert.False(aggregate.Passed);
        Assert.Equal(["corner"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void Mutation_AgentIndexAndPathDesiredDistance_CannotCreditSkippedPoint()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("first", 0, 1.0f, 0.0f),
            Point("second", 1, 2.0f, 0.25f)));

        RoutePassageSegmentDiagnostic diagnostic = oracle.ObserveCollisionRealisedSegment(
            Motion(
                new Vector3(0.0f, 0.0f, 0.25f),
                new Vector3(2.0f, 0.0f, 0.25f),
                reportedAgentPathIndex: 2,
                reportedPathDesiredDistance: 10.0f));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        RoutePassagePointCheck check = Assert.Single(diagnostic.PointChecks);
        Assert.Equal("first", check.PointID);
        Assert.False(check.Passed);
        Assert.Equal(2, diagnostic.Motion.ReportedNavigationAgentPathIndex);
        Assert.Equal(10.0f, diagnostic.Motion.ReportedPathDesiredDistance);
        Assert.False(aggregate.Passed);
        Assert.Equal(["first", "second"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void Mutation_OutOfOrderProximity_FailsFinalAggregateUntilTheSecondPointIsSweptAfterTheFirst()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("first", 0, 1.0f, 0.0f),
            Point("second", 1, 2.0f, 0.0f)));

        _ = oracle.ObserveCollisionRealisedSegment(Motion(new Vector3(2.0f, 0.0f, 0.0f), new Vector3(2.0f, 0.0f, 0.0f)));
        RoutePassageSegmentDiagnostic firstPoint = oracle.ObserveCollisionRealisedSegment(
            Motion(new Vector3(1.0f, 0.0f, 0.0f), new Vector3(1.0f, 0.0f, 0.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.True(firstPoint.PointChecks[0].Passed);
        Assert.False(firstPoint.PointChecks[1].Passed);
        Assert.False(aggregate.Passed);
        Assert.Equal(1, aggregate.PassedPointCount);
        Assert.Equal(["second"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void Mutation_EndpointOnlyArrivalWithUnpassedInteriorPoint_FailsFinalAggregate()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("interior", 0, 1.0f, 0.30f),
            Point("endpoint", 1, 2.0f, 0.0f)));

        RoutePassageSegmentDiagnostic diagnostic = oracle.ObserveCollisionRealisedSegment(
            Motion(Vector3.Zero, new Vector3(2.0f, 0.0f, 0.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.False(Assert.Single(diagnostic.PointChecks).Passed);
        Assert.False(aggregate.Passed);
        Assert.Equal(["interior", "endpoint"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void SweptSegment_EnteringToleranceBetweenDiscreteFrames_PassesFinalAggregate()
    {
        RoutePassageOracle oracle = Activate(Route("request-a", 1, 1, Point("interior", 0, 1.0f, 0.0f)));

        RoutePassageSegmentDiagnostic diagnostic = oracle.ObserveCollisionRealisedSegment(
            Motion(Vector3.Zero, new Vector3(2.0f, 0.0f, 0.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        RoutePassagePointCheck check = Assert.Single(diagnostic.PointChecks);
        Assert.True(check.Passed);
        Assert.Equal(0.0f, check.SweptMinimumDistanceMetres, 4);
        Assert.True(aggregate.Passed);
        Assert.Empty(aggregate.RemainingPoints);
    }

    /// <inheritdoc/>
    [Fact]
    public void Mutation_CollisionRealisedMiss_DoesNotUseRequestedSegmentForFinalAggregate()
    {
        RoutePassageOracle oracle = Activate(Route("request-a", 1, 1, Point("constraint", 0, 1.0f, 0.0f)));

        RoutePassageSegmentDiagnostic diagnostic = oracle.ObserveCollisionRealisedSegment(new RoutePassageMotion(
            RequestedStart: Vector3.Zero,
            RequestedEnd: new Vector3(2.0f, 0.0f, 0.0f),
            CollisionRealisedStart: new Vector3(0.0f, 0.0f, 0.25f),
            CollisionRealisedEnd: new Vector3(2.0f, 0.0f, 0.25f),
            ReportedNavigationAgentPathIndex: 1,
            ReportedPathDesiredDistance: 0.10f));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.Equal(0.25f, Assert.Single(diagnostic.PointChecks).SweptMinimumDistanceMetres, 4);
        Assert.False(aggregate.Passed);
        Assert.Equal(["constraint"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void Mutation_SharpCornerOvershoot_FailsFinalAggregate()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("entry", 0, 1.0f, 0.0f),
            Point("corner", 1, 1.0f, 1.0f),
            Point("endpoint", 2, 2.0f, 1.0f)));

        _ = oracle.ObserveCollisionRealisedSegment(Motion(Vector3.Zero, new Vector3(1.2f, 0.0f, 0.0f)));
        RoutePassageSegmentDiagnostic overshoot = oracle.ObserveCollisionRealisedSegment(
            Motion(new Vector3(1.2f, 0.0f, 0.0f), new Vector3(2.0f, 0.0f, 1.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        RoutePassagePointCheck corner = Assert.Single(overshoot.PointChecks);
        Assert.Equal("corner", corner.PointID);
        Assert.False(corner.Passed);
        Assert.False(aggregate.Passed);
        Assert.Equal(["corner", "endpoint"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void RouteRevision_DoesNotGrantRetroactiveCreditAtReanchor()
    {
        RoutePassageOracle oracle = Activate(Route("request-a", 7, 1, Point("old", 0, 1.0f, 0.0f)));
        _ = oracle.ObserveCollisionRealisedSegment(Motion(Vector3.Zero, new Vector3(0.5f, 0.0f, 0.0f)));

        bool replaced = oracle.TryReplace(
            Route("request-a", 7, 2, Point("new-at-reanchor", 0, 0.5f, 0.0f)),
            new Vector3(0.5f, 0.0f, 0.0f));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.True(replaced);
        Assert.False(aggregate.Passed);
        Assert.Equal(7, aggregate.RequestGeneration);
        Assert.Equal(2, aggregate.RouteRevision);
        Assert.Equal(["new-at-reanchor"], aggregate.RemainingPoints.Select(point => point.PointID));
        Assert.Equal(["old"], Assert.Single(aggregate.Retirements).UnpassedPoints.Select(point => point.PointID));
        RoutePassageReanchor reanchor = Assert.Single(
            aggregate.Reanchors,
            fact => fact.Reason == RoutePassageReanchorReason.Replacement);
        Assert.Equal(new Vector3(0.5f, 0.0f, 0.0f), reanchor.CollisionRealisedPosition);
    }

    /// <inheritdoc/>
    [Fact]
    public void AcceptedReplacement_RetiresOldRemainderReanchorsNewRouteAndPassesNewFinalAggregate()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("old-passed", 0, 1.0f, 0.0f),
            Point("old-retired", 1, 2.0f, 0.0f)));
        _ = oracle.ObserveCollisionRealisedSegment(Motion(Vector3.Zero, new Vector3(1.0f, 0.0f, 0.0f)));

        bool replaced = oracle.TryReplace(
            Route("request-b", 2, 1, Point("new", 0, 3.0f, 0.0f)),
            new Vector3(1.0f, 0.0f, 0.0f));
        _ = oracle.ObserveCollisionRealisedSegment(
            Motion(new Vector3(1.0f, 0.0f, 0.0f), new Vector3(3.0f, 0.0f, 0.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.True(replaced);
        Assert.True(aggregate.Passed);
        Assert.Equal("request-b", aggregate.RequestID);
        Assert.Equal(2, aggregate.RequestGeneration);
        Assert.Equal(1, aggregate.RouteRevision);
        RoutePassageRetirement retirement = Assert.Single(aggregate.Retirements);
        Assert.Equal("request-a", retirement.RequestID);
        Assert.Equal(["old-retired"], retirement.UnpassedPoints.Select(point => point.PointID));
        RoutePassageReanchor reanchor = Assert.Single(
            aggregate.Reanchors,
            fact => fact.Reason == RoutePassageReanchorReason.Replacement);
        Assert.Equal("request-b", reanchor.RequestID);
        Assert.Equal(new Vector3(1.0f, 0.0f, 0.0f), reanchor.CollisionRealisedPosition);
    }

    /// <inheritdoc/>
    [Fact]
    public void RejectedReplacement_RetainsOldLedgerForFinalAggregate()
    {
        RoutePassageOracle oracle = Activate(Route("request-a", 1, 1, Point("old", 0, 1.0f, 0.0f)));

        bool replaced = oracle.TryReplace(
            Route("request-b", 2, 1, Point("invalid", 0, float.NaN, 0.0f)),
            Vector3.Zero);
        _ = oracle.ObserveCollisionRealisedSegment(Motion(Vector3.Zero, new Vector3(1.0f, 0.0f, 0.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.False(replaced);
        Assert.True(aggregate.Passed);
        Assert.Equal("request-a", aggregate.RequestID);
        Assert.Empty(aggregate.Retirements);
        Assert.False(Assert.Single(aggregate.ReplacementAttempts).Accepted);
    }

    /// <inheritdoc/>
    [Fact]
    public void SweptSegment_CreditsSuccessivePointsOnlyInRawOrder()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("first", 0, 1.0f, 0.0f),
            Point("second", 1, 2.0f, 0.0f),
            Point("third", 2, 3.0f, 0.0f)));

        RoutePassageSegmentDiagnostic diagnostic = oracle.ObserveCollisionRealisedSegment(
            Motion(Vector3.Zero, new Vector3(4.0f, 0.0f, 0.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.True(aggregate.Passed);
        Assert.Equal(["first", "second", "third"], diagnostic.PointChecks.Select(check => check.PointID));
        Assert.Equal([0, 1, 2], diagnostic.PointChecks.Select(check => check.Order));
        Assert.All(diagnostic.PointChecks, check => Assert.True(check.Passed));
    }

    /// <inheritdoc/>
    [Fact]
    public void Mutation_ReverseSweep_CreditsOnlyTheFirstRawPointBeforeEncounterOrderReverses()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("first", 0, 1.0f, 0.0f),
            Point("second", 1, 2.0f, 0.0f),
            Point("third", 2, 3.0f, 0.0f)));

        RoutePassageSegmentDiagnostic diagnostic = oracle.ObserveCollisionRealisedSegment(
            Motion(new Vector3(4.0f, 0.0f, 0.0f), Vector3.Zero));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.Collection(
            diagnostic.PointChecks,
            first =>
            {
                Assert.Equal("first", first.PointID);
                Assert.Equal(0.0f, first.SweptMinimumDistanceMetres, 4);
                Assert.Equal(0.75f, first.SweptPlanarParameter, 4);
                Assert.True(first.Passed);
            },
            second =>
            {
                Assert.Equal("second", second.PointID);
                Assert.Equal(0.0f, second.SweptMinimumDistanceMetres, 4);
                Assert.Equal(0.50f, second.SweptPlanarParameter, 4);
                Assert.False(second.Passed);
            });
        Assert.False(aggregate.Passed);
        Assert.Equal(1, aggregate.PassedPointCount);
        Assert.Equal(["second", "third"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void PassageLedger_IsMonotonicWhenTheCollisionRealisedTrajectoryBacktracks()
    {
        RoutePassageOracle oracle = Activate(Route(
            "request-a",
            1,
            1,
            Point("first", 0, 1.0f, 0.0f),
            Point("second", 1, 2.0f, 0.0f)));

        _ = oracle.ObserveCollisionRealisedSegment(Motion(Vector3.Zero, new Vector3(1.0f, 0.0f, 0.0f)));
        _ = oracle.ObserveCollisionRealisedSegment(Motion(new Vector3(1.0f, 0.0f, 0.0f), Vector3.Zero));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.False(aggregate.Passed);
        Assert.Equal(1, aggregate.PassedPointCount);
        Assert.Equal(["second"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void InvalidNonFiniteInput_FailsClosedWithoutFabricatingCredit()
    {
        RoutePassageOracle oracle = Activate(Route("request-a", 1, 1, Point("constraint", 0, 1.0f, 0.0f)));

        RoutePassageSegmentDiagnostic invalid = oracle.ObserveCollisionRealisedSegment(
            Motion(Vector3.Zero, new Vector3(float.NaN, 0.0f, 0.0f)));
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.False(invalid.IsValidInput);
        Assert.Empty(invalid.PointChecks);
        Assert.False(aggregate.Passed);
        Assert.Equal(0, aggregate.PassedPointCount);
        Assert.Equal(["constraint"], aggregate.RemainingPoints.Select(point => point.PointID));
    }

    /// <inheritdoc/>
    [Fact]
    public void InvalidNonFiniteRawPoint_FailsClosedWithoutCreatingAFinalAggregatePass()
    {
        RoutePassageOracle oracle = new();

        bool activated = oracle.TryActivate(
            Route("request-a", 1, 1, Point("invalid", 0, float.PositiveInfinity, 0.0f)),
            Vector3.Zero);
        RoutePassageAggregate aggregate = oracle.EvaluateFinalAggregate();

        Assert.False(activated);
        Assert.False(aggregate.Passed);
        Assert.Null(aggregate.RequestID);
        Assert.Empty(aggregate.Reanchors);
    }

    private static RoutePassageOracle Activate(RoutePassageRoute route)
    {
        RoutePassageOracle oracle = new();
        Assert.True(oracle.TryActivate(route, Vector3.Zero));
        return oracle;
    }

    private static RoutePassageRoute Route(string requestID, long generation, long revision, params RoutePassagePoint[] points)
        => new(requestID, generation, revision, points);

    private static RoutePassagePoint Point(string pointID, int order, float x, float z)
        => new(pointID, order, new Vector3(x, 0.0f, z));

    private static RoutePassageMotion Motion(
        Vector3 realisedStart,
        Vector3 realisedEnd,
        int reportedAgentPathIndex = 0,
        float reportedPathDesiredDistance = RoutePassageOracle.DefaultPlanarToleranceMetres)
        => new(
            RequestedStart: realisedStart,
            RequestedEnd: realisedEnd,
            CollisionRealisedStart: realisedStart,
            CollisionRealisedEnd: realisedEnd,
            ReportedNavigationAgentPathIndex: reportedAgentPathIndex,
            ReportedPathDesiredDistance: reportedPathDesiredDistance);
}
