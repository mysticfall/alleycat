using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Navigation;

public sealed class NPCAnimationProgrammeIntegrationTests
{
    [Fact]
    public void NoMovementProgramme_CompletesDirectlyAtIdleWithoutEntryOrExit()
    {
        NPCAnimationProgramme programme = new();

        NPCAnimationFrame frame = programme.CompleteNoMovement();

        Assert.Equal(NPCAnimationStage.Complete, frame.Stage);
        Assert.Equal(NPCAnimationEvent.Complete, frame.Event);
        Assert.Equal(new StringName("NPCIdle"), frame.State);
        Assert.Equal(0, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);
        _ = Assert.Throws<InvalidOperationException>(() => _ = programme.CompleteNoMovement());
    }

    [Theory]
    [InlineData(-0.6888549f, 2.2608037f, 2.8458345f)]
    [InlineData(-0.6938858f, -0.8398442f, 0.6905182f)]
    [InlineData(0.7323381f, -3.1248817f, -0.23020257f)]
    public void ExactRegressionScenario_HasOneEntryOneExitAndStableCompletion(float x, float z, float yaw)
    {
        NPCNavigationRoutePlan route = Plan(new Vector3(x, 0f, z), yaw);
        NPCAnimationProgramme programme = new();
        Transform3D actor = Transform3D.Identity;

        Assert.Equal(NPCAnimationEvent.StartEntry, programme.Step(actor, route, 0f, false, true).Event);
        Assert.Equal(NPCAnimationEvent.StartTravel, programme.Step(actor, route, 0f, true, false).Event);
        float committedProgress = Math.Max(route.TotalLength - 0.5f, 0f);
        Assert.Equal(NPCAnimationEvent.StartExit, programme.Step(actor, route, committedProgress, false, true).Event);
        _ = programme.Step(actor, route, committedProgress, true, true);
        Transform3D broadlyArrived = new(
            new Basis(Vector3.Up, yaw + 0.026f),
            new Vector3(x + 0.026f, 0f, z));
        for (int frame = 0; frame < NPCAnimationProgramme.StableConvergenceFrameCount + 2; frame++)
        {
            NPCAnimationFrame settling = programme.Step(
                broadlyArrived,
                route,
                route.TotalLength,
                false,
                true);
            Assert.Equal(NPCAnimationStage.Settlement, settling.Stage);
            Assert.Equal(Vector2.Zero, settling.DesiredLocalVelocity);
            Assert.Equal(0f, settling.DesiredYawRate);
        }

        Transform3D arrived = new(new Basis(Vector3.Up, yaw), new Vector3(x, 0f, z));
        for (int frame = 0; frame < NPCAnimationProgramme.StableConvergenceFrameCount; frame++)
        {
            _ = programme.Step(arrived, route, route.TotalLength, false, true);
        }

        Assert.Equal(NPCAnimationStage.Complete, programme.Stage);
        Assert.Equal(1, programme.EntryCount);
        Assert.Equal(1, programme.ExitCount);
        Assert.Equal(0, programme.IdleCrossingCount);
    }

    [Fact]
    public void Settlement_RequiresStableNumericalPositionAndYawConvergence()
    {
        NPCNavigationRoutePlan route = Plan(new Vector3(0f, 0f, -1f), 0.25f);
        NPCAnimationProgramme programme = EnterSettlement(route);
        float threshold = NPCAnimationProgramme.NumericalConvergenceThreshold;
        Transform3D outsidePosition = new(
            new Basis(Vector3.Up, route.TerminalYaw),
            route.Endpoint + new Vector3(threshold * 1.1f, 0f, 0f));

        for (int frame = 0; frame < NPCAnimationProgramme.StableConvergenceFrameCount + 1; frame++)
        {
            Assert.Equal(
                NPCAnimationStage.Settlement,
                programme.Step(outsidePosition, route, route.TotalLength, false, true).Stage);
        }

        Transform3D outsideYaw = new(
            new Basis(Vector3.Up, route.TerminalYaw + (threshold * 1.1f)),
            route.Endpoint);
        Assert.Equal(
            NPCAnimationStage.Settlement,
            programme.Step(outsideYaw, route, route.TotalLength, false, true).Stage);

        Transform3D converged = new(
            new Basis(Vector3.Up, route.TerminalYaw + (threshold * 0.5f)),
            route.Endpoint + new Vector3(threshold * 0.5f, 0f, 0f));
        for (int frame = 0; frame < NPCAnimationProgramme.StableConvergenceFrameCount; frame++)
        {
            _ = programme.Step(converged, route, route.TotalLength, false, true);
        }

        Assert.Equal(NPCAnimationStage.Complete, programme.Stage);
    }

    [Fact]
    public void CompletedExit_TransitionsDirectlyToStationarySettlementWithoutAnEndpointTail()
    {
        NPCNavigationRoutePlan route = Plan(new Vector3(0f, 0f, -1f), 0.25f);
        NPCAnimationProgramme programme = new();
        float threshold = NPCAnimationProgramme.NumericalConvergenceThreshold;
        Transform3D outsidePosition = new(
            new Basis(Vector3.Up, route.TerminalYaw),
            route.Endpoint + new Vector3(threshold * 1.1f, 0f, 0f));
        Transform3D converged = new(new Basis(Vector3.Up, route.TerminalYaw), route.Endpoint);

        Assert.Equal(NPCAnimationEvent.StartEntry, programme.Step(Transform3D.Identity, route, 0f, false, true).Event);
        Assert.Equal(NPCAnimationEvent.StartTravel, programme.Step(Transform3D.Identity, route, 0f, true, false).Event);
        Assert.Equal(NPCAnimationEvent.StartExit, programme.Step(Transform3D.Identity, route, route.TotalLength, false, true).Event);
        NPCAnimationFrame settlement = programme.Step(outsidePosition, route, route.TotalLength, true, true);
        Assert.Equal(NPCAnimationEvent.StartSettlement, settlement.Event);
        Assert.Equal(NPCAnimationStage.Settlement, settlement.Stage);
        Assert.Equal(Vector2.Zero, settlement.DesiredLocalVelocity);
        Assert.Equal(0f, settlement.DesiredYawRate);

        Assert.Equal(
            NPCAnimationStage.Settlement,
            programme.Step(converged, route, route.TotalLength, false, true).Stage);
    }

    [Fact]
    public void ShallowRight_PerFrameAdjustmentsRetainForwardLifecycleAndLane()
    {
        NPCNavigationRoutePlan route = Plan(new Vector3(0.7323381f, 0f, -3.1248817f), -0.23020257f);
        NPCAnimationProgramme programme = new();
        NPCAnimationFrame frame = programme.Step(Transform3D.Identity, route, 0f, false, true);
        NPCAnimationPhaseLane lane = frame.PhaseLane;
        frame = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationStage.SustainedTravel, frame.Stage);
        for (int index = 0; index < 60; index++)
        {
            frame = programme.Step(Transform3D.Identity, route, index * 0.02f, false, false);
            Assert.NotEqual(NPCAnimationEvent.StartEntry, frame.Event);
            Assert.NotEqual(NPCAnimationStage.Idle, frame.Stage);
            Assert.Equal(lane, frame.PhaseLane);
        }
        Assert.Equal(1, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);
    }

    [Fact]
    public void IdentityActor_StraightForwardRouteProducesForwardSemanticVelocity()
    {
        NPCAnimationProgramme programme = new();
        NPCNavigationRoutePlan route = Plan(new Vector3(0f, 0f, -3f), 0f);

        _ = programme.Step(Transform3D.Identity, route, 0f, false, true);
        NPCAnimationFrame travel = programme.Step(Transform3D.Identity, route, 0f, true, false);

        Assert.Equal(NPCAnimationStage.SustainedTravel, travel.Stage);
        Assert.True(travel.DesiredLocalVelocity.Y > 0f);
        NPCNavigationSemanticFrame frame = new(Basis.Identity, new Basis(Vector3.Up, Mathf.Pi));
        Assert.True(frame.ActorSemanticToWorld(travel.DesiredLocalVelocity).Dot(Vector3.Forward) > 0f);
    }

    [Fact]
    public void ReferenceRigFrame_AppliesAuthoredHalfTurnExactlyOnce()
    {
        NPCNavigationSemanticFrame frame = new(Basis.Identity, new Basis(Vector3.Up, Mathf.Pi));

        Vector3 worldDelta = frame.EvaluatedRootDeltaToWorld(Vector3.Back);

        Assert.True(worldDelta.Dot(Vector3.Forward) > 0.999f);
        Assert.True(frame.ActorWorldForward.Dot(frame.VisualWorldForward) > 0.999f);
        Assert.True((frame.RootReferenceRotation * worldDelta).Dot(Vector3.Back) > 0.999f);
    }

    [Fact]
    public void SemanticRightRoute_RequestsNegativeWorldYawAndReducesYawError()
    {
        NPCAnimationProgramme programme = new();
        NPCNavigationRoutePlan route = Plan(new Vector3(0.75f, 0f, -3f), -0.25f);
        _ = programme.Step(Transform3D.Identity, route, 0f, false, true);

        NPCAnimationFrame travel = programme.Step(Transform3D.Identity, route, 0f, true, false);
        float initialError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(route.DesiredRouteYaw(0.35f)));
        Transform3D turned = new(new Basis(Vector3.Up, travel.DesiredYawRate / 60f), Vector3.Zero);
        _ = programme.Step(turned, route, 0f, false, true);
        float turnedYaw = NPCNavigationRoutePlan.YawFromDirection(turned.Basis * Vector3.Forward);
        float reducedError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(route.DesiredRouteYaw(0.35f) - turnedYaw));

        Assert.True(travel.DesiredYawRate < 0f);
        Assert.True(reducedError < initialError);
    }

    [Fact]
    public void TerminalCommitment_WaitsUntilArmedReleaseGateExecutes()
    {
        NPCAnimationProgramme programme = new();
        NPCNavigationRoutePlan route = Plan(new Vector3(0f, 0f, -2f), 0f);
        _ = programme.Step(Transform3D.Identity, route, 0f, false, true);
        _ = programme.Step(Transform3D.Identity, route, 0f, true, false);
        float committedProgress = route.TotalLength - 0.5f;

        NPCAnimationFrame unreserved = programme.Step(
            Transform3D.Identity,
            route,
            committedProgress,
            false,
            false);
        NPCAnimationFrame reserved = programme.Step(
            Transform3D.Identity,
            route,
            committedProgress,
            false,
            true);

        Assert.NotEqual(NPCAnimationEvent.StartExit, unreserved.Event);
        Assert.Equal(NPCAnimationEvent.StartExit, reserved.Event);
    }

    [Fact]
    public void HeadingOnlyRequest_PlaysChainedPivotsFromIdleWithoutTranslationAndSettles()
    {
        // 1.9 rad / (π/4) = 2.42 → two chained pl45 clips; the −0.329 rad remainder converges in settlement.
        NPCNavigationRoutePlan route = Plan(Vector3.Zero, 1.9f);
        NPCAnimationProgramme programme = new();
        programme.ConfigureTurnInPlace(NPCTurnInPlace.Select(1.9f, Mathf.Pi / 4f, 0.04f));

        NPCAnimationFrame firstPivot = programme.Step(Transform3D.Identity, route, 0f, false, false);
        Assert.Equal(NPCAnimationEvent.StartPivot, firstPivot.Event);
        Assert.Equal(NPCAnimationStage.Pivot, firstPivot.Stage);
        Assert.Equal("NPCPivotL", firstPivot.State.ToString());
        Assert.Equal(Vector2.Zero, firstPivot.DesiredLocalVelocity);
        Assert.Equal(0f, firstPivot.DesiredYawRate);
        Assert.Equal(1, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);

        // The completed first pivot re-emits StartPivot and stays in the Pivot stage.
        NPCAnimationFrame secondPivot = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationEvent.StartPivot, secondPivot.Event);
        Assert.Equal(NPCAnimationStage.Pivot, secondPivot.Stage);
        Assert.Equal("NPCPivotL", secondPivot.State.ToString());
        Assert.Equal(2, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);

        NPCAnimationFrame settling = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationEvent.StartSettlement, settling.Event);
        Assert.Equal(NPCAnimationStage.Settlement, settling.Stage);
        Assert.Equal(Vector2.Zero, settling.DesiredLocalVelocity);
        Assert.Equal(0f, settling.DesiredYawRate);

        Transform3D converged = new(new Basis(Vector3.Up, 1.9f), Vector3.Zero);
        int stableSettlementFrames = 0;
        NPCAnimationFrame last = default;
        for (int frame = 0; frame < NPCAnimationProgramme.StableConvergenceFrameCount; frame++)
        {
            last = programme.Step(converged, route, route.TotalLength, false, true);
            if (last.Stage == NPCAnimationStage.Settlement)
            {
                stableSettlementFrames++;
                Assert.Equal(Vector2.Zero, last.DesiredLocalVelocity);
                Assert.Equal(0f, last.DesiredYawRate);
            }
        }

        Assert.Equal(NPCAnimationProgramme.StableConvergenceFrameCount - 1, stableSettlementFrames);
        Assert.Equal(NPCAnimationEvent.Complete, last.Event);
        Assert.Equal(NPCAnimationStage.Complete, programme.Stage);
        Assert.Equal(2, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);
    }

    [Fact]
    public void HeadingOnlyResidualBelowPivotThreshold_EntersPureConvergenceWithoutAnyClip()
    {
        NPCNavigationRoutePlan route = Plan(Vector3.Zero, 0.02f);
        NPCAnimationProgramme programme = new();
        programme.ConfigureTurnInPlace(NPCTurnInPlace.Select(0.02f, Mathf.Pi / 4f, 0.04f));

        NPCAnimationFrame settling = programme.Step(Transform3D.Identity, route, 0f, false, false);

        Assert.Equal(NPCAnimationEvent.StartSettlement, settling.Event);
        Assert.Equal(NPCAnimationStage.Settlement, settling.Stage);
        Assert.True(settling.State.IsEmpty);
        Assert.Equal(Vector2.Zero, settling.DesiredLocalVelocity);
        Assert.Equal(0f, settling.DesiredYawRate);
        Assert.Equal(0, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);

        Transform3D converged = new(new Basis(Vector3.Up, 0.02f), Vector3.Zero);
        for (int frame = 0; frame < NPCAnimationProgramme.StableConvergenceFrameCount; frame++)
        {
            _ = programme.Step(converged, route, route.TotalLength, false, true);
        }

        Assert.Equal(NPCAnimationStage.Complete, programme.Stage);
    }

    [Fact]
    public void SideStepComposition_PlaysStepChainThenPivotChainThenSettles_InThatOrder()
    {
        // The recorded decision chains the step first in the actor's current frame,
        // then the pivot chain, then settlement converges the remainder. The fixture's ~90° remainder composes
        // exactly two pr45 clips.
        Vector3 endpoint = new(0.18984336f, 0f, -0.06523204f);
        NPCNavigationRoutePlan route = Plan(endpoint, -Mathf.Pi / 2f);
        NPCAnimationProgramme programme = new();
        const float terminalYaw = -Mathf.Pi / 2f;
        float absorptionCapacity = NPCSideStep.YawAbsorptionCapacity(0.42499688f, 0.08f);
        programme.ConfigureSideStep(
            NPCSideStep.Select(new Vector2(0.18984336f, 0.06523204f), terminalYaw, 0.42499688f, absorptionCapacity, 0.08f),
            NPCTurnInPlace.Select(terminalYaw + absorptionCapacity, Mathf.Pi / 4f, 0.034906585f));

        NPCAnimationFrame sideStep = programme.Step(Transform3D.Identity, route, 0f, false, false);
        Assert.Equal(NPCAnimationEvent.StartSideStep, sideStep.Event);
        Assert.Equal(NPCAnimationStage.SideStep, sideStep.Stage);
        Assert.Equal("NPCSideStepR", sideStep.State.ToString());
        Assert.Equal(Vector2.Zero, sideStep.DesiredLocalVelocity);
        Assert.Equal(0f, sideStep.DesiredYawRate);
        Assert.Equal(1, programme.EntryCount);

        NPCAnimationFrame firstPivot = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationEvent.StartPivot, firstPivot.Event);
        Assert.Equal(NPCAnimationStage.Pivot, firstPivot.Stage);
        Assert.Equal("NPCPivotR", firstPivot.State.ToString());
        Assert.Equal(Vector2.Zero, firstPivot.DesiredLocalVelocity);
        Assert.Equal(0f, firstPivot.DesiredYawRate);
        Assert.Equal(2, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);

        NPCAnimationFrame secondPivot = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationEvent.StartPivot, secondPivot.Event);
        Assert.Equal(NPCAnimationStage.Pivot, secondPivot.Stage);
        Assert.Equal("NPCPivotR", secondPivot.State.ToString());
        Assert.Equal(3, programme.EntryCount);

        NPCAnimationFrame settling = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationEvent.StartSettlement, settling.Event);
        Assert.Equal(NPCAnimationStage.Settlement, settling.Stage);
        Assert.True(settling.State.IsEmpty);

        Transform3D converged = new(new Basis(Vector3.Up, -Mathf.Pi / 2f), endpoint);
        int stableSettlementFrames = 0;
        NPCAnimationFrame last = default;
        for (int frame = 0; frame < NPCAnimationProgramme.StableConvergenceFrameCount; frame++)
        {
            last = programme.Step(converged, route, route.TotalLength, false, true);
            if (last.Stage == NPCAnimationStage.Settlement)
            {
                stableSettlementFrames++;
            }
        }

        Assert.Equal(NPCAnimationProgramme.StableConvergenceFrameCount - 1, stableSettlementFrames);
        Assert.Equal(NPCAnimationEvent.Complete, last.Event);
        Assert.Equal(NPCAnimationStage.Complete, programme.Stage);
        Assert.Equal(3, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);
    }

    [Fact]
    public void ChainedSideStepPlan_ReplaysTheConfinedClipPerRepetitionBeforeSettling()
    {
        // A 0.947 m lateral request plans two chained side-step clips; each completed repetition
        // re-emits StartSideStep with the stage never leaving SideStep, and only the chain's end enters settlement.
        Vector3 endpoint = new(-0.946435f, 0f, -0.03213787f);
        NPCNavigationRoutePlan route = Plan(endpoint, -0.12529877f);
        NPCAnimationProgramme programme = new();
        programme.ConfigureSideStep(
            NPCSideStep.Select(new Vector2(-0.946435f, 0.03213787f), -0.12529877f, 0.42499688f, 0.1885162f, 0.08f));
        Assert.Equal(2, programme.SideStepChainRemaining);

        NPCAnimationFrame firstStep = programme.Step(Transform3D.Identity, route, 0f, false, false);
        Assert.Equal(NPCAnimationEvent.StartSideStep, firstStep.Event);
        Assert.Equal(NPCAnimationStage.SideStep, firstStep.Stage);
        Assert.Equal("NPCSideStepL", firstStep.State.ToString());
        Assert.Equal(2, programme.SideStepChainRemaining);

        NPCAnimationFrame secondStep = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationEvent.StartSideStep, secondStep.Event);
        Assert.Equal(NPCAnimationStage.SideStep, secondStep.Stage);
        Assert.Equal("NPCSideStepL", secondStep.State.ToString());
        Assert.Equal(1, programme.SideStepChainRemaining);
        Assert.Equal(2, programme.EntryCount);

        NPCAnimationFrame settling = programme.Step(Transform3D.Identity, route, 0f, true, false);
        Assert.Equal(NPCAnimationEvent.StartSettlement, settling.Event);
        Assert.Equal(NPCAnimationStage.Settlement, settling.Stage);
        Assert.True(settling.State.IsEmpty);
        Assert.Equal(2, programme.EntryCount);
        Assert.Equal(0, programme.ExitCount);
    }

    private static NPCNavigationRoutePlan Plan(Vector3 endpoint, float yaw)
        => NPCNavigationRoutePlan.Compile(
            [Vector3.Zero, endpoint],
            0,
            new Transform3D(new Basis(Vector3.Up, yaw), endpoint),
            1,
            1,
            0.475f);

    private static NPCAnimationProgramme EnterSettlement(NPCNavigationRoutePlan route)
    {
        NPCAnimationProgramme programme = new();
        Transform3D converged = new(new Basis(Vector3.Up, route.TerminalYaw), route.Endpoint);
        _ = programme.Step(Transform3D.Identity, route, 0f, false, true);
        _ = programme.Step(Transform3D.Identity, route, 0f, true, false);
        _ = programme.Step(Transform3D.Identity, route, route.TotalLength, false, true);
        _ = programme.Step(converged, route, route.TotalLength, true, true);
        _ = programme.Step(converged, route, route.TotalLength, false, true);
        Assert.Equal(NPCAnimationStage.Settlement, programme.Stage);
        return programme;
    }
}
