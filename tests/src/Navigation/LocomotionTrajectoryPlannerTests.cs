using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Pure NAV-001 predictive planner contract coverage.
/// </summary>
public sealed class LocomotionTrajectoryPlannerTests
{
    private static readonly LocomotionResponseProfile _profile = StandingLocomotionResponseProfiles.Get(
        StandingLocomotionCharacter.ReferenceFemale);

    /// <summary>Verifies arc-length route compilation and straight forward intent.</summary>
    [Fact]
    public void StraightRoute_CompilesArcLengthAndPredictsForwardProgress()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -3.0f)]);

        LocomotionPlannerOutput output = Evaluate(route, Actor(Vector3.Zero, 0.0f));

        _ = Assert.Single(route.Segments);
        Assert.Empty(route.Corners);
        Assert.Equal(3.0f, route.TotalLength, 3);
        Assert.True(route.BrakingDistance > 0.0f);
        Assert.True(output.Movement.Y > 0.2f);
        Assert.InRange(Mathf.Abs(output.Movement.X), 0.0f, 0.01f);
        Assert.True(output.PredictedProgressAt02Seconds < output.PredictedProgressAt05Seconds);
        Assert.True(output.PredictedProgressAt05Seconds < output.PredictedProgressAt10Seconds);
    }

    /// <summary>Verifies a right-angle turn starts before its waypoint.</summary>
    [Fact]
    public void NinetyDegreeCorner_ProducesSignedAnticipationBeforeCorner()
    {
        LocomotionRoutePlan route = Plan(
            [Vector3.Zero, new Vector3(0.0f, 0.0f, -2.0f), new Vector3(-2.0f, 0.0f, -2.0f)]);
        LocomotionPlannerState prior = State(route, progress: 1.45f, movement: new Vector2(0.0f, 0.8f));

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -1.45f), 0.0f),
            0.1,
            prior,
            _profile,
            route);

        LocomotionRouteCorner corner = Assert.Single(route.Corners);
        Assert.Equal(Mathf.Pi / 2.0f, corner.SignedAngle, 3);
        Assert.True(corner.AnticipationStartDistance < corner.Distance);
        Assert.True(output.Turn > 0.0f);
        Assert.True(output.RemainingDistance > 2.0f);
    }

    /// <summary>Verifies measured left/right rates are not treated as mirrored.</summary>
    [Fact]
    public void MirroredCorners_PreserveAsymmetricTurnResponse()
    {
        var configuration = new LocomotionPlannerConfiguration { PlannedControlWeight = 100.0f };
        LocomotionResponseProfile asymmetricProfile = FasterTurningProfile();
        LocomotionRoutePlan left = Plan(
            [Vector3.Zero, new Vector3(0.0f, 0.0f, -2.0f), new Vector3(-2.0f, 0.0f, -2.0f)],
            configuration: configuration);
        LocomotionRoutePlan right = Plan(
            [Vector3.Zero, new Vector3(0.0f, 0.0f, -2.0f), new Vector3(2.0f, 0.0f, -2.0f)],
            configuration: configuration);
        LocomotionPlannerState leftState = State(left, 1.45f, new Vector2(0.0f, 0.8f));
        LocomotionPlannerState rightState = State(right, 1.45f, new Vector2(0.0f, 0.8f));

        LocomotionPlannerOutput leftOutput = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -1.45f), 0.0f), 0.5, leftState, asymmetricProfile, left, configuration);
        LocomotionPlannerOutput rightOutput = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -1.45f), 0.0f), 0.5, rightState, asymmetricProfile, right, configuration);

        Assert.True(leftOutput.Turn > 0.0f);
        Assert.True(rightOutput.Turn < 0.0f);
        Assert.NotEqual(Mathf.Abs(leftOutput.Turn), Mathf.Abs(rightOutput.Turn), 2);
    }

    /// <summary>Verifies alternating bends retain bounded, slew-limited command continuity.</summary>
    [Fact]
    public void SCurve_ControlsRemainBoundedAndContinuous()
    {
        var configuration = new LocomotionPlannerConfiguration { TurnSlewRate = 1.5f, MovementSlewRate = 1.5f };
        LocomotionRoutePlan route = Plan(
            [Vector3.Zero, new Vector3(0.0f, 0.0f, -1.0f), new Vector3(-0.8f, 0.0f, -1.8f), new Vector3(0.0f, 0.0f, -2.6f)],
            configuration: configuration);
        LocomotionPlannerState state = State(route, 0.0f, new Vector2(0.0f, 0.5f));
        float previousTurn = 0.0f;

        for (int index = 0; index < 12; index++)
        {
            float z = -0.18f * index;
            LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
                Actor(new Vector3(index > 6 ? -0.3f : 0.0f, 0.0f, z), 0.0f),
                0.1,
                state,
                _profile,
                route,
                configuration);
            Assert.InRange(Mathf.Abs(output.Turn - previousTurn), 0.0f, 0.1501f);
            Assert.InRange(output.Movement.Length(), 0.0f, 1.0f);
            state = output.State;
            previousTurn = output.Turn;
        }
    }

    /// <summary>Verifies large initial heading error requests turn-in-place before translation.</summary>
    [Fact]
    public void LargeInitialHeadingError_UsesTurnInPlace()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -2.0f)]);

        LocomotionPlannerOutput output = Evaluate(route, Actor(Vector3.Zero, Mathf.Pi / 2.0f));

        Assert.True(output.State.TurningInPlace);
        Assert.InRange(output.Movement.Length(), 0.0f, 0.001f);
        Assert.True(output.Turn < 0.0f);
    }

    /// <summary>Verifies a distant rear destination retains its signed stationary turn across pivot loops.</summary>
    [Fact]
    public void ExactDistantBehindDestination_RetainsSignedStationaryTurnUntilHeadingIsNearRoute()
    {
        Vector3 destinationPosition = new(-0.09982029f, 0.0f, 2.5849361f);
        Vector3 destinationForward = new(-0.038587395f, 0.0f, 0.99925524f);
        Transform3D destination = FacingTransform(destinationForward, destinationPosition);
        var route = LocomotionRoutePlan.Compile(
            [Vector3.Zero, destinationPosition],
            0,
            destination,
            1,
            1,
            _profile);

        LocomotionPlannerOutput pivot = Evaluate(route, Actor(Vector3.Zero, 0.0f));
        LocomotionPlannerOutput duringLaterPivotLoop = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, Mathf.DegToRad(116.0f)),
            0.2,
            pivot.State,
            _profile,
            route);

        Assert.True(route.TotalLength > 1.25f);
        Assert.True(pivot.State.TurningInPlace);
        Assert.InRange(pivot.Movement.Length(), 0.0f, 0.001f);
        Assert.True(duringLaterPivotLoop.State.TurningInPlace);
        Assert.InRange(duringLaterPivotLoop.Movement.Length(), 0.0f, 0.001f);
        Assert.True(Mathf.Sign(duringLaterPivotLoop.Turn) == Mathf.Sign(pivot.Turn));
        Assert.True(Mathf.Abs(duringLaterPivotLoop.Turn) > 0.01f);
    }

    /// <summary>Verifies near-route heading exits directly from stationary turn intent to forward travel.</summary>
    [Fact]
    public void ExactDistantBehindDestination_NearRouteHeadingTransitionsDirectlyToForwardProgress()
    {
        Vector3 destinationPosition = new(-0.09982029f, 0.0f, 2.5849361f);
        Vector3 destinationForward = new(-0.038587395f, 0.0f, 0.99925524f);
        Transform3D destination = FacingTransform(destinationForward, destinationPosition);
        var route = LocomotionRoutePlan.Compile(
            [Vector3.Zero, destinationPosition], 0, destination, 1, 1, _profile);

        LocomotionPlannerOutput firstPivot = Evaluate(route, Actor(Vector3.Zero, 0.0f));
        LocomotionPlannerOutput laterPivotLoop = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, Mathf.DegToRad(116.0f)), 0.2, firstPivot.State, _profile, route);
        LocomotionPlannerOutput routeDirected = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, Mathf.Pi), 0.2, laterPivotLoop.State, _profile, route);

        Assert.Equal(destination.Origin, route.Endpoint);
        Assert.Equal(LocomotionRoutePlan.YawFromDirection(destinationForward), route.TerminalYaw, 5);
        Assert.True(laterPivotLoop.State.TurningInPlace);
        Assert.True(Mathf.Abs(laterPivotLoop.Turn) > 0.01f);
        Assert.False(routeDirected.State.TurningInPlace);
        Assert.True(routeDirected.Movement.Y > 0.01f);
        Assert.True(routeDirected.PredictedProgressAt10Seconds > routeDirected.ProjectedProgress + 0.01f);
    }

    /// <summary>
    /// Verifies either side of the signed-angle seam gives up the final sub-tolerance translation
    /// and selects the matching held looped pivot instead of becoming stuck in endpoint correction.
    /// </summary>
    [Theory]
    [InlineData(-0.002f, -0.001f, 1.0f)]
    [InlineData(0.002f, 0.001f, -1.0f)]
    public void DistantNearPiDestination_AtTerminalPivotTolerance_ConvergesWithMatchingPivot(
        float destinationX,
        float destinationForwardX,
        float expectedTurnSign)
    {
        Vector3 destinationPosition = new(destinationX, 0.0f, 2.5849361f);
        Vector3 destinationForward = new(destinationForwardX, 0.0f, 0.9999995f);
        Transform3D destination = FacingTransform(destinationForward, destinationPosition);
        var route = LocomotionRoutePlan.Compile(
            [Vector3.Zero, destinationPosition], 0, destination, 1, 1, _profile);
        float actorYaw = route.TerminalYaw - (expectedTurnSign * 0.1f);
        Transform3D actor = Actor(destinationPosition - (destinationPosition.Normalized() * 0.04f), actorYaw);
        LocomotionPlannerState state = State(route, route.TotalLength - 0.04f, Vector2.Zero);

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(actor, 0.2, state, _profile, route);

        Assert.False(route.UsesShortEndpointCorrection);
        Assert.InRange(output.Movement.Length(), 0.0f, 0.001f);
        Assert.True(output.State.TurningInPlace);
        Assert.Equal(expectedTurnSign, Mathf.Sign(output.Turn));
        Assert.Equal(1.0f, Mathf.Abs(output.Turn));
    }

    /// <summary>Verifies shallow route error favours forward travel over strafing.</summary>
    [Fact]
    public void ShallowDiagonal_PrefersForwardMovingTurn()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(-0.5f, 0.0f, -3.0f)]);

        LocomotionPlannerOutput output = Evaluate(route, Actor(Vector3.Zero, 0.0f));

        Assert.True(output.Movement.Y > Mathf.Abs(output.Movement.X));
        Assert.True(output.Movement.Y > 0.0f);
    }

    /// <summary>Verifies short endpoint correction uses the bounded backwards-plus-lateral response.</summary>
    [Fact]
    public void ShortEndpoint_UsesSignedLocalCorrection()
    {
        LocomotionRoutePlan side = Plan([Vector3.Zero, new Vector3(0.3f, 0.0f, 0.0f)]);
        LocomotionRoutePlan back = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, 0.3f)]);

        LocomotionPlannerOutput sideOutput = Evaluate(side, Actor(Vector3.Zero, 0.0f));
        LocomotionPlannerOutput backOutput = Evaluate(back, Actor(Vector3.Zero, 0.0f));
        LocomotionPlannerOutput backOutputWithReverseHistory = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, 0.0f), 0.2, State(back, 0.0f, new Vector2(0.0f, -0.5f)), _profile, back);

        Assert.True(side.UsesShortEndpointCorrection);
        Assert.True(sideOutput.Movement.X > 0.0f);
        Assert.False(sideOutput.State.TurningInPlace);
        Assert.True(back.UsesShortEndpointCorrection);
        Assert.True(backOutput.Movement.Y < 0.0f);
        Assert.True(backOutput.State.LocalCorrectionActive);
        Assert.True(backOutputWithReverseHistory.Movement.Y < 0.0f);
    }

    /// <summary>Verifies local rear correction enters only at or below the exact 1 m contract boundary.</summary>
    [Theory]
    [InlineData(0.999f, true)]
    [InlineData(1.0f, true)]
    [InlineData(1.001f, false)]
    public void RearCorrection_EntersAtExactOneMetreBoundary(float endpointDistance, bool expectedActive)
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, endpointDistance)]);

        LocomotionPlannerOutput output = Evaluate(route, Actor(Vector3.Zero, 0.0f));

        Assert.Equal(expectedActive, output.State.LocalCorrectionActive);
    }

    /// <summary>Verifies an active local rear correction still releases at the existing 1.25 m boundary.</summary>
    [Fact]
    public void RearCorrection_AtOnePointTwentyFiveMetresReleases()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, 1.25f)]);
        LocomotionPlannerState prior = State(route, 0.0f, new Vector2(0.0f, -0.5f)) with
        {
            LocalCorrectionActive = true,
        };

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, 0.0f), 0.2, prior, _profile, route);

        Assert.False(output.State.LocalCorrectionActive);
        Assert.True(output.Movement.Y >= 0.0f);
    }

    /// <summary>Verifies normal route following cannot publish reverse forward movement retained from history.</summary>
    [Fact]
    public void StraightRoute_ReversePriorMovementPublishesNonNegativeForwardMovement()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -3.0f)]);
        LocomotionPlannerState prior = State(route, 0.0f, new Vector2(0.0f, -0.8f));

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, 0.0f), 0.01, prior, _profile, route);

        Assert.True(output.Movement.Y >= 0.0f);
        Assert.True(output.State.Movement.Y >= 0.0f);
    }

    /// <summary>Verifies the precompiled stop profile reduces forward intent near the endpoint.</summary>
    [Fact]
    public void BrakingProfile_ReducesCommandAndPredictedIntentDoesNotPassEndpoint()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -3.0f)]);
        LocomotionPlannerState cruising = State(route, 0.0f, new Vector2(0.0f, 1.0f));
        LocomotionPlannerOutput far = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, 0.0f), 0.2, cruising, _profile, route);
        LocomotionPlannerOutput near = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -2.9f), 0.0f), 0.2, cruising with
            {
                Progress = 2.9f
            }, _profile, route);

        Assert.True(near.Movement.Y < far.Movement.Y);
        Assert.InRange(near.PredictedProgressAt10Seconds, 0.0f, route.TotalLength);
    }

    /// <summary>Verifies position completion retains turn intent until terminal facing is reached.</summary>
    [Fact]
    public void TerminalFacing_TurnsInPlaceAtEndpoint()
    {
        Vector3 endpoint = new(0.0f, 0.0f, -1.0f);
        LocomotionRoutePlan route = Plan([Vector3.Zero, endpoint], terminalYaw: Mathf.Pi / 2.0f);

        LocomotionPlannerOutput output = Evaluate(route, Actor(endpoint, 0.0f));

        Assert.InRange(output.Movement.Length(), 0.0f, 0.001f);
        Assert.True(output.Turn > 0.0f);
    }

    /// <summary>Verifies a completed stationary pivot is released rather than holding another looped pivot cycle.</summary>
    [Fact]
    public void TerminalFacing_WithinToleranceExitsStationaryPivot()
    {
        Vector3 endpoint = new(0.0f, 0.0f, -1.0f);
        LocomotionRoutePlan route = Plan([Vector3.Zero, endpoint], terminalYaw: Mathf.Pi / 2.0f);
        LocomotionPlannerState prior = State(route, route.TotalLength, Vector2.Zero, turn: 1.0f) with
        {
            TurningInPlace = true,
        };

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(endpoint, (Mathf.Pi / 2.0f) - 0.02f), 0.2, prior, _profile, route);

        Assert.InRange(output.Movement.Length(), 0.0f, 0.001f);
        Assert.Equal(0.0f, output.Turn, 3);
        Assert.False(output.State.TurningInPlace);
    }

    /// <summary>Verifies endpoint route length does not select a different terminal policy at the former short-route boundary.</summary>
    [Theory]
    [InlineData(0.650f)]
    [InlineData(0.651f)]
    public void TerminalSettling_FormerShortRouteBoundaryUsesUnifiedPolicy(float routeLength)
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -routeLength)], terminalYaw: Mathf.Pi / 2.0f);
        Vector3 nearEndpoint = new(0.0f, 0.0f, -routeLength + 0.04f);

        LocomotionPlannerOutput output = Evaluate(route, Actor(nearEndpoint, 0.0f));

        Assert.InRange(output.Movement.Length(), 0.0f, 0.001f);
        Assert.True(output.State.TerminalSettling);
        Assert.True(output.State.TurningInPlace);
        Assert.Equal(1.0f, output.Turn);
    }

    /// <summary>Verifies minor terminal root-motion deviations remain settled without movement or pivot-sign flapping.</summary>
    [Fact]
    public void TerminalSettling_MinorRootMotionDeviationRetainsStationaryFacingIntent()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -1.0f)], terminalYaw: Mathf.Pi / 2.0f);
        LocomotionPlannerState state = State(route, route.TotalLength - 0.04f, Vector2.Zero);

        LocomotionPlannerOutput entered = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -0.96f), 0.0f), 0.1, state, _profile, route);
        LocomotionPlannerOutput deviated = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -0.94f), 0.0f), 0.1, entered.State, _profile, route);

        Assert.True(entered.State.TerminalSettling);
        Assert.True(deviated.State.TerminalSettling);
        Assert.Equal(Mathf.Sign(entered.Turn), Mathf.Sign(deviated.Turn));
        Assert.InRange(deviated.Movement.Length(), 0.0f, 0.001f);
    }

    /// <summary>Verifies a facing deviation observed after terminal entry re-acquires the stationary pivot before arrival completes.</summary>
    [Fact]
    public void TerminalSettling_FacingDeviationReacquiresStationaryPivot()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -1.0f)], terminalYaw: Mathf.Pi / 2.0f);
        LocomotionPlannerState state = State(route, route.TotalLength - 0.04f, Vector2.Zero);

        LocomotionPlannerOutput entered = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -0.96f), Mathf.Pi / 2.0f), 0.1, state, _profile, route);
        LocomotionPlannerOutput deviated = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -0.96f), (Mathf.Pi / 2.0f) - 0.25f), 0.1, entered.State, _profile, route);

        Assert.True(entered.State.TerminalSettling);
        Assert.Equal(0.0f, entered.Turn, 3);
        Assert.True(deviated.State.TerminalSettling);
        Assert.True(deviated.State.TurningInPlace);
        Assert.Equal(1.0f, deviated.Turn);
        Assert.InRange(deviated.Movement.Length(), 0.0f, 0.001f);
    }

    /// <summary>Verifies terminal settling releases to positional correction after a meaningful miss.</summary>
    [Fact]
    public void TerminalSettling_MeaningfulMissReacquiresEndpoint()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -1.0f)], terminalYaw: 0.0f);
        LocomotionPlannerState settled = State(route, route.TotalLength - 0.04f, Vector2.Zero) with
        {
            TerminalSettling = true
        };

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.10f, 0.0f, -0.94f), 0.0f), 0.2, settled, _profile, route);

        Assert.False(output.State.TerminalSettling);
        Assert.True(Mathf.Abs(output.Movement.X) > 0.01f || output.Movement.Y > 0.01f);
        Assert.Equal(0.0f, output.Turn, 3);
    }

    /// <summary>Verifies small same-side displacement produces proportionate sustained correction without sign flapping.</summary>
    [Fact]
    public void LateralDisplacement_ProducesContiguousSameSignFeedback()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -3.0f)]);
        LocomotionPlannerState state = State(route, 0.8f, new Vector2(0.0f, 0.7f));
        float previousMagnitude = float.PositiveInfinity;

        foreach (float displacement in new[] { 0.10f, 0.08f, 0.06f, 0.04f })
        {
            LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
                Actor(new Vector3(displacement, 0.0f, -0.8f), 0.0f), 0.1, state, _profile, route);
            Assert.True(output.Movement.X < 0.0f);
            Assert.Equal(-1, output.State.CorrectionSign);
            Assert.True(Mathf.Abs(output.Movement.X) <= previousMagnitude + 0.11f);
            Assert.InRange(Mathf.Abs(output.Movement.X - state.Movement.X), 0.0f, 0.2501f);
            previousMagnitude = Mathf.Abs(output.Movement.X);
            state = output.State;
        }

        LocomotionPlannerOutput crossing = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(-0.005f, 0.0f, -0.8f), 0.0f), 0.1, state, _profile, route);
        Assert.Equal(-1, crossing.State.CorrectionSign);
        Assert.True(crossing.Movement.X <= 0.0f);
    }

    /// <summary>Verifies low remaining arc distance cannot stop a cross-track actor away from the destination.</summary>
    [Fact]
    public void TerminalCrossTrackError_ContinuesEndpointCorrectionUntilPositionConverges()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -1.0f)]);
        LocomotionPlannerState prior = State(route, 0.98f, new Vector2(0.0f, 0.2f));

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.15f, 0.0f, -0.98f), 0.0f), 0.1, prior, _profile, route);

        Assert.True(Mathf.Abs(output.Movement.X) > 0.01f);
        Assert.True(output.RemainingDistance < 0.035f);
        Assert.Equal(0.0f, output.Turn, 3);
    }

    /// <summary>Verifies stale terminal projection cannot enter endpoint mode while world-position error is still large.</summary>
    [Fact]
    public void PassedProjectionFarFromEndpoint_ReacquiresRouteWithoutSideOrBackEndpointMode()
    {
        LocomotionRoutePlan route = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -1.0f)]);
        LocomotionPlannerState prior = State(route, 1.0f, new Vector2(0.0f, 0.3f));

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -2.0f), 0.0f), 0.1, prior, _profile, route);

        Assert.True(output.Movement.Y >= 0.0f);
        Assert.InRange(output.Movement.X, -0.001f, 0.001f);
        Assert.True(output.State.TurningInPlace);
        Assert.True(Mathf.Abs(output.Turn) > 0.01f);
    }

    /// <summary>Verifies changed route geometry keeps current command history in a bounded transition.</summary>
    [Fact]
    public void RouteRevision_RebuildsGeometryWithoutResettingControls()
    {
        var configuration = new LocomotionPlannerConfiguration
        {
            MovementSlewRate = 2.0f,
            TurnSlewRate = 2.0f,
            RouteRevisionTransitionSeconds = 0.3f,
        };
        LocomotionRoutePlan original = Plan([Vector3.Zero, new Vector3(0.0f, 0.0f, -3.0f)], revision: 1, configuration: configuration);
        LocomotionRoutePlan revised = Plan([Vector3.Zero, new Vector3(-3.0f, 0.0f, -1.0f)], revision: 2, configuration: configuration);
        LocomotionRoutePlan replaced = Plan(
            [Vector3.Zero, new Vector3(3.0f, 0.0f, -1.0f)],
            destinationGeneration: 2,
            revision: 1,
            configuration: configuration);
        LocomotionPlannerState prior = State(original, 0.5f, new Vector2(0.0f, 0.8f), 0.2f);

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -0.5f), 0.0f), 0.05, prior, _profile, revised, configuration);

        Assert.Equal(2, output.State.RouteRevision);
        Assert.True(output.State.RevisionTransitionRemaining > 0.0f);
        Assert.True(output.Movement.Y > 0.7f);
        Assert.InRange(Mathf.Abs(output.Turn - prior.Turn), 0.0f, 0.1f);

        LocomotionPlannerOutput replacementOutput = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -0.5f), 0.0f), 0.05, prior, _profile, replaced, configuration);
        Assert.Equal(2, replacementOutput.State.DestinationRequestGeneration);
        Assert.True(replacementOutput.Movement.Y > 0.7f);
    }

    /// <summary>Verifies route-revision blending cannot republish reverse forward movement retained from history.</summary>
    [Fact]
    public void RouteRevision_ReversePriorMovementPublishesNonNegativeForwardMovement()
    {
        var configuration = new LocomotionPlannerConfiguration
        {
            RouteRevisionTransitionSeconds = 0.3f,
        };
        LocomotionRoutePlan original = Plan(
            [Vector3.Zero, new Vector3(0.0f, 0.0f, -3.0f)], revision: 1, configuration: configuration);
        LocomotionRoutePlan revised = Plan(
            [Vector3.Zero, new Vector3(-3.0f, 0.0f, -1.0f)], revision: 2, configuration: configuration);
        LocomotionPlannerState prior = State(original, 0.0f, new Vector2(0.0f, -0.8f));

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(Vector3.Zero, 0.0f), 0.01, prior, _profile, revised, configuration);

        Assert.True(output.State.RevisionTransitionRemaining > 0.0f);
        Assert.True(output.Movement.Y >= 0.0f);
        Assert.True(output.State.Movement.Y >= 0.0f);
    }

    /// <summary>Verifies an active path-index advance does not rebase persistent arc-length progress.</summary>
    [Fact]
    public void CoherentSnapshot_ActiveIndexRetainsWholeRouteArcLength()
    {
        Vector3[] points = [Vector3.Zero, new Vector3(0.0f, 0.0f, -1.0f), new Vector3(-1.0f, 0.0f, -2.0f)];
        Transform3D destination = new(Basis.Identity, points[^1]);
        NavigationRouteSnapshot snapshot = new(points, 2, points[2], destination, 1, 2, false, false);

        var route = LocomotionRoutePlan.Compile(snapshot, _profile);
        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            Actor(new Vector3(0.0f, 0.0f, -1.0f), 0.0f),
            0.1,
            State(route, 1.0f, new Vector2(0.0f, 0.7f)),
            _profile,
            route);

        Assert.Equal(1.0f + Mathf.Sqrt(2.0f), route.TotalLength, 3);
        Assert.InRange(output.ProjectedProgress, 0.99f, 1.01f);
    }

    /// <summary>Verifies malformed route, transform, delta, state, and tuning cannot emit non-finite controls.</summary>
    [Fact]
    public void InvalidInputs_ReturnFiniteBoundedNeutralOutput()
    {
        var configuration = new LocomotionPlannerConfiguration
        {
            MovementSlewRate = float.NaN,
            TurnSlewRate = float.PositiveInfinity,
        };
        LocomotionRoutePlan route = Plan(
            [new Vector3(float.NaN, 0.0f, 0.0f), Vector3.Zero, Vector3.Zero],
            configuration: configuration);
        var invalidState = new LocomotionPlannerState(
            float.NaN,
            new Vector2(float.PositiveInfinity, 0.0f),
            float.NaN,
            1,
            true,
            1,
            1,
            float.NaN);
        Transform3D invalidActor = new(Basis.Identity, new Vector3(float.NaN, 0.0f, 0.0f));

        LocomotionPlannerOutput output = LocomotionTrajectoryPlanner.Evaluate(
            invalidActor, double.NaN, invalidState, _profile, route, configuration);

        Assert.True(output.Movement.IsFinite());
        Assert.True(float.IsFinite(output.Turn));
        Assert.InRange(output.Movement.Length(), 0.0f, 1.0f);
        Assert.InRange(output.Turn, -1.0f, 1.0f);
        Assert.True(float.IsFinite(route.TotalLength));
        Assert.True(float.IsFinite(route.TerminalYaw));
    }

    private static LocomotionPlannerOutput Evaluate(LocomotionRoutePlan route, Transform3D actor)
        => LocomotionTrajectoryPlanner.Evaluate(
            actor,
            0.2,
            State(route, 0.0f, Vector2.Zero),
            _profile,
            route);

    private static LocomotionPlannerState State(
        LocomotionRoutePlan route,
        float progress,
        Vector2 movement,
        float turn = 0.0f)
        => new(progress, movement, turn, 0, false, route.DestinationRequestGeneration, route.RouteRevision, 0.0f);

    private static LocomotionRoutePlan Plan(
        IReadOnlyList<Vector3> points,
        float terminalYaw = 0.0f,
        long destinationGeneration = 1,
        long revision = 1,
        LocomotionPlannerConfiguration? configuration = null)
    {
        Vector3 endpoint = points.Count == 0 ? Vector3.Zero : points[^1];
        var destination = new Transform3D(new Basis(Vector3.Up, terminalYaw), endpoint.IsFinite() ? endpoint : Vector3.Zero);
        return LocomotionRoutePlan.Compile(points, 0, destination, destinationGeneration, revision, _profile, configuration);
    }

    private static Transform3D Actor(Vector3 position, float yaw)
        => new(new Basis(Vector3.Up, yaw), position);

    private static Transform3D FacingTransform(Vector3 facing, Vector3 origin)
    {
        Vector3 stableFacing = facing.Normalized();
        Vector3 right = stableFacing.Cross(Vector3.Up).Normalized();
        return new Transform3D(new Basis(right, Vector3.Up, -stableFacing), origin);
    }

    private static LocomotionResponseProfile FasterTurningProfile()
        => new(
            StandingLocomotionCharacter.ReferenceFemale,
            _profile.Forwards,
            _profile.Backwards,
            _profile.SideStepLeft,
            _profile.SideStepRight,
            ScaleYaw(_profile.WalkArcLeft, 4.0f),
            ScaleYaw(_profile.WalkArcRight, 4.0f),
            _profile.TurnInPlaceLeft90,
            _profile.TurnInPlaceRight90);

    private static LocomotionCycleResponse ScaleYaw(LocomotionCycleResponse response, float scale)
        => new(
            response.PlanarDisplacement,
            response.AveragePlanarSpeed,
            response.Yaw * scale,
            response.MetricDurationSeconds,
            response.ImportedTimelineDurationSeconds);
}
