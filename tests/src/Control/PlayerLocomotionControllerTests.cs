using AlleyCat.Control.Locomotion;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

public sealed class PlayerLocomotionControllerTests
{
    [Fact]
    public void MovementBout_WithoutAuthoritativeReleaseEvidence_HoldsTravelOnRelease()
    {
        PlayerLocomotionController controller = new();

        PlayerLocomotionOutput entry = Step(controller, new Vector2(0f, 1f));
        PlayerLocomotionOutput heldEntry = Step(controller, new Vector2(0f, 1f));
        PlayerLocomotionOutput travel = Step(controller, new Vector2(0f, 1f), completed: true);
        PlayerLocomotionOutput sustained = Step(controller, new Vector2(0.05f, 0.9f));
        PlayerLocomotionOutput released = Step(controller, Vector2.Zero);
        PlayerLocomotionOutput heldRelease = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionCommand.EnterForward, entry.Command);
        Assert.Equal(PlayerLocomotionCommand.None, heldEntry.Command);
        Assert.Equal(PlayerLocomotionCommand.Travel, travel.Command);
        Assert.Equal(PlayerLocomotionCommand.None, sustained.Command);
        Assert.Equal(PlayerLocomotionCommand.None, released.Command);
        Assert.Equal(PlayerLocomotionState.TravellingForward, released.State);
        Assert.Equal(PlayerLocomotionCommand.None, heldRelease.Command);
    }

    [Fact]
    public void MovingInputChanges_UpdateBlendWithoutEntryReselectionAndRetainPhaseLane()
    {
        PlayerLocomotionController controller = EnterTravel(new Vector2(0.2f, 1f));
        PlayerLocomotionPhaseLane lane = controller.PhaseLane;

        PlayerLocomotionOutput changed = Step(controller, new Vector2(-0.1f, 0.8f), turn: 0.2f);
        PlayerLocomotionOutput directional = Step(controller, new Vector2(0.8f, 0.2f));

        Assert.Equal(PlayerLocomotionCommand.None, changed.Command);
        Assert.Equal(new Vector2(-0.1f, 0.8f), changed.Blend);
        Assert.Equal(PlayerLocomotionCommand.None, directional.Command);
        Assert.Equal(PlayerLocomotionState.TravellingDirectional, directional.State);
        Assert.Equal(lane, directional.PhaseLane);
        Assert.DoesNotContain([changed.Command, directional.Command], command => command is PlayerLocomotionCommand.EnterForward or PlayerLocomotionCommand.EnterBackward);
    }

    [Fact]
    public void ReleasedTravel_RepushResumesWithoutArbitraryExit()
    {
        PlayerLocomotionController controller = EnterTravel(new Vector2(0f, 1f));
        Assert.Equal(PlayerLocomotionCommand.None, Step(controller, Vector2.Zero).Command);

        PlayerLocomotionOutput resumed = Step(controller, new Vector2(0f, -1f));

        Assert.Equal(PlayerLocomotionState.TravellingDirectional, resumed.State);
        Assert.Equal(PlayerLocomotionCommand.None, resumed.Command);
    }

    [Fact]
    public void EarlyRelease_DuringForwardEntry_RejectsArbitraryExit()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, 1f));

        PlayerLocomotionOutput released = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.EnteringForward, released.State);
        Assert.Equal(PlayerLocomotionCommand.None, released.Command);
    }

    [Fact]
    public void EarlyRelease_DuringBackwardEntry_RejectsArbitraryExit()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, -1f));

        PlayerLocomotionOutput released = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.EnteringBackward, released.State);
        Assert.Equal(PlayerLocomotionCommand.None, released.Command);
    }

    [Fact]
    public void ReleasedEntry_ContinuesToFailClosedTravelWhenItsFiniteEntryCompletes()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, 1f));
        _ = Step(controller, Vector2.Zero);

        PlayerLocomotionOutput completedEntry = Step(controller, Vector2.Zero, completed: true);
        PlayerLocomotionOutput heldRelease = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.TravellingForward, completedEntry.State);
        Assert.Equal(PlayerLocomotionCommand.Travel, completedEntry.Command);
        Assert.Equal(PlayerLocomotionState.TravellingForward, heldRelease.State);
        Assert.Equal(PlayerLocomotionCommand.None, heldRelease.Command);
    }

    [Fact]
    public void EarlyRelease_DuringEntry_IgnoresPivotIntentWithoutSchedulingExit()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, 1f));

        // A held turn while releasing the entry is not a pivot: pivots only start from Idle, so the release
        // remains the bout's single stop.
        PlayerLocomotionOutput released = Step(controller, Vector2.Zero, turn: -1f);

        Assert.Equal(PlayerLocomotionState.EnteringForward, released.State);
        Assert.Equal(PlayerLocomotionCommand.None, released.Command);
    }

    [Fact]
    public void HeldEntry_WithoutCompletion_StaysEnteringWithoutCommand()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, 1f));

        PlayerLocomotionOutput held = Step(controller, new Vector2(0f, 1f));

        Assert.Equal(PlayerLocomotionState.EnteringForward, held.State);
        Assert.Equal(PlayerLocomotionCommand.None, held.Command);
    }

    [Fact]
    public void PermissionLoss_DuringEntry_RejectsArbitraryExit()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, 1f));

        // Permission loss must not turn into a phase-derived exit while authoritative ANIM-002 release evidence
        // is unavailable.
        PlayerLocomotionOutput blocked = controller.Step(new Vector2(0f, 1f), 0f, LocomotionPermissions.RotationOnly, false, 0.75f, 1.2f);

        Assert.Equal(PlayerLocomotionState.EnteringForward, blocked.State);
        Assert.Equal(PlayerLocomotionCommand.None, blocked.Command);
        Assert.False(blocked.MovementAllowed);
    }

    [Fact]
    public void PermissionGates_AreIndependent()
    {
        PlayerLocomotionController rotationController = new();
        PlayerLocomotionOutput rotationOnly = rotationController.Step(new Vector2(0f, 1f), 1f, LocomotionPermissions.RotationOnly, false, 0.75f, 1.2f);

        PlayerLocomotionController movementController = new();
        PlayerLocomotionOutput movementOnly = movementController.Step(new Vector2(0f, 1f), 1f, new(true, false), false, 0.75f, 1.2f);

        // Blocked translation still leaves rotation available, so the held turn enters the standing pivot.
        Assert.Equal(PlayerLocomotionState.PivotingRight, rotationOnly.State);
        Assert.Equal(PlayerLocomotionCommand.PivotRight, rotationOnly.Command);
        Assert.True(rotationOnly.Blend.IsZeroApprox());
        Assert.False(rotationOnly.MovementAllowed);
        Assert.True(rotationOnly.RotationAllowed);
        Assert.Equal(PlayerLocomotionCommand.EnterForward, movementOnly.Command);
        Assert.True(movementOnly.MovementAllowed);
        Assert.False(movementOnly.RotationAllowed);
    }

    [Fact]
    public void BlockedRotation_DoesNotPivot()
    {
        PlayerLocomotionController controller = new();
        PlayerLocomotionOutput output = controller.Step(Vector2.Zero, 1f, LocomotionPermissions.Blocked, false, 0.75f, 1.2f);

        Assert.Equal(PlayerLocomotionState.Idle, output.State);
        Assert.Equal(PlayerLocomotionCommand.None, output.Command);
    }

    [Fact]
    public void StationaryTurn_EntersPivotBySign()
    {
        PlayerLocomotionController rightController = new();
        PlayerLocomotionOutput right = Step(rightController, Vector2.Zero, turn: 1f);

        PlayerLocomotionController leftController = new();
        PlayerLocomotionOutput left = Step(leftController, Vector2.Zero, turn: -1f);

        // ANIM-002 response coordinates: +x requests actor-right (pr45); -x requests actor-left (pl45).
        Assert.Equal(PlayerLocomotionState.PivotingRight, right.State);
        Assert.Equal(PlayerLocomotionCommand.PivotRight, right.Command);
        Assert.Equal(PlayerLocomotionState.PivotingLeft, left.State);
        Assert.Equal(PlayerLocomotionCommand.PivotLeft, left.Command);
    }

    [Fact]
    public void PivotHeld_ContinuesLoopingWithoutRetrigger()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, Vector2.Zero, turn: -1f);

        PlayerLocomotionOutput held = Step(controller, Vector2.Zero, turn: -1f);
        PlayerLocomotionOutput heldFurther = Step(controller, Vector2.Zero, turn: -0.6f);

        Assert.Equal(PlayerLocomotionState.PivotingLeft, held.State);
        Assert.Equal(PlayerLocomotionCommand.None, held.Command);
        Assert.Equal(PlayerLocomotionState.PivotingLeft, heldFurther.State);
        Assert.Equal(PlayerLocomotionCommand.None, heldFurther.Command);
    }

    [Fact]
    public void PivotReleased_ReturnsToIdle()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, Vector2.Zero, turn: 1f);

        PlayerLocomotionOutput released = Step(controller, Vector2.Zero, turn: 0f);

        Assert.Equal(PlayerLocomotionState.Idle, released.State);
        Assert.Equal(PlayerLocomotionCommand.Idle, released.Command);
    }

    [Fact]
    public void PivotSignFlip_SwapsPivotWithoutIdleInterlude()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, Vector2.Zero, turn: 1f);

        PlayerLocomotionOutput flipped = Step(controller, Vector2.Zero, turn: -1f);

        Assert.Equal(PlayerLocomotionState.PivotingLeft, flipped.State);
        Assert.Equal(PlayerLocomotionCommand.PivotLeft, flipped.Command);
    }

    [Fact]
    public void MovementDuringPivot_ReturnsToIdleBeforeEntry()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, Vector2.Zero, turn: 1f);

        PlayerLocomotionOutput toIdle = Step(controller, new Vector2(0f, 1f), turn: 1f);
        PlayerLocomotionOutput entry = Step(controller, new Vector2(0f, 1f), turn: 1f);

        // The pivot→locomotion hand-off routes via Idle so each hop rides its single pre-authored graph edge
        // (CTRL-001 TR11/TR26); the next frame's Idle-state logic then commands the entry.
        Assert.Equal(PlayerLocomotionState.Idle, toIdle.State);
        Assert.Equal(PlayerLocomotionCommand.Idle, toIdle.Command);
        Assert.Equal(PlayerLocomotionState.EnteringForward, entry.State);
        Assert.Equal(PlayerLocomotionCommand.EnterForward, entry.Command);
    }

    [Fact]
    public void PivotIsNotSelected_WhileMoving()
    {
        PlayerLocomotionController controller = EnterTravel(new Vector2(0f, 1f));

        PlayerLocomotionOutput turning = Step(controller, new Vector2(0f, 1f), turn: -0.8f);
        PlayerLocomotionOutput directional = Step(controller, new Vector2(-0.9f, 0.1f), turn: -0.8f);

        Assert.Equal(PlayerLocomotionState.TravellingForward, turning.State);
        Assert.Equal(PlayerLocomotionCommand.None, turning.Command);
        Assert.Equal(PlayerLocomotionState.TravellingDirectional, directional.State);
        Assert.Equal(PlayerLocomotionCommand.None, directional.Command);
    }

    [Fact]
    public void ForwardArcBlend_CarriesTurnWhileMoving()
    {
        PlayerLocomotionController controller = EnterTravel(new Vector2(0f, 1f));

        PlayerLocomotionOutput right = Step(controller, new Vector2(0f, 1f), turn: 0.8f);
        PlayerLocomotionOutput left = Step(controller, new Vector2(0f, 1f), turn: -0.8f);

        Assert.InRange(right.ForwardArcBlend, 0.79f, 1f);
        Assert.InRange(left.ForwardArcBlend, -1f, -0.79f);
        Assert.True(right.ForwardArcBlend > left.ForwardArcBlend);
    }

    [Fact]
    public void PivotStates_SuppressSyntheticResiduals()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, Vector2.Zero, turn: -1f);

        Vector2 planar = controller.CalculatePlanarResidual(new Vector2(10f, 0f), Vector2.Zero, 1f, 1f, 0.1f, 0.3f, 0.2f, 100f, 0.5f);
        float yaw = controller.CalculateYawResidual(10f, 0f, 1f, 1f, 1f, 0.1f, 0.5f, 0.25f, 100f, 0.5f);

        Assert.True(planar.IsZeroApprox());
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void SubThresholdTurn_AtIdleKeepsBoundedResidualWithoutPivot()
    {
        PlayerLocomotionController controller = new();
        PlayerLocomotionOutput output = controller.Step(Vector2.Zero, 0.05f, LocomotionPermissions.Allowed, false, 0.75f, 1.2f);
        float yawRate = controller.CalculateYawResidual(0.125f, 0f, 1f, 0.05f, 1f, 0.1f, 0.5f, 0.25f, 100f, 0.5f);

        Assert.Equal(PlayerLocomotionState.Idle, output.State);
        Assert.Equal(PlayerLocomotionCommand.None, output.Command);
        Assert.InRange(yawRate, 0f, 0.50001f);
    }

    [Fact]
    public void SyntheticResiduals_ObeyRateAndCumulativeSegmentBudgets()
    {
        PlayerLocomotionController controller = EnterTravel(new Vector2(0f, 1f));
        float planarDistance = 0f;
        float yawDistance = 0f;
        for (int sample = 0; sample < 100; sample++)
        {
            Vector2 planar = controller.CalculatePlanarResidual(new Vector2(10f, 0f), Vector2.Zero, 1f, 1f, 0.1f, 0.3f, 0.2f, 100f, 0.5f);
            float yaw = controller.CalculateYawResidual(10f, 0f, 1f, 1f, 1f, 0.1f, 0.5f, 0.25f, 100f, 0.5f);
            Assert.InRange(planar.Length(), 0f, 0.30001f);
            Assert.InRange(Mathf.Abs(yaw), 0f, 0.50001f);
            planarDistance += planar.Length() * 0.1f;
            yawDistance += Mathf.Abs(yaw) * 0.1f;
        }
        Assert.InRange(planarDistance, 0f, 0.20001f);
        Assert.InRange(yawDistance, 0f, 0.25001f);
    }

    [Fact]
    public void LateralDominantInput_FromIdle_EntersSideStepByDirection()
    {
        PlayerLocomotionController leftController = new();
        PlayerLocomotionOutput left = Step(leftController, new Vector2(-1f, 0f));

        PlayerLocomotionController rightController = new();
        PlayerLocomotionOutput right = Step(rightController, new Vector2(1f, 0f));

        // The lateral sign picks the confined sl/sr unit, mirroring the entry lane convention (−X left, +X right).
        Assert.Equal(PlayerLocomotionState.SideSteppingLeft, left.State);
        Assert.Equal(PlayerLocomotionCommand.SideStepLeft, left.Command);
        Assert.Equal(PlayerLocomotionState.SideSteppingRight, right.State);
        Assert.Equal(PlayerLocomotionCommand.SideStepRight, right.Command);
    }

    [Fact]
    public void ForwardDominantDiagonal_FromIdle_DoesNotSideStep()
    {
        PlayerLocomotionController diagonal = new();
        PlayerLocomotionOutput diagonalOutput = Step(diagonal, new Vector2(0.4f, 1f));

        PlayerLocomotionController boundary = new();
        PlayerLocomotionOutput boundaryOutput = Step(boundary, new Vector2(1f, 1f));

        // Longitudinal intent at or beyond the diagonal boundary keeps the entry regime; only strict lateral
        // dominance selects the confined side step (mirrors NPCSideStep.Select's mostly-lateral test).
        Assert.Equal(PlayerLocomotionState.EnteringForward, diagonalOutput.State);
        Assert.Equal(PlayerLocomotionCommand.EnterForward, diagonalOutput.Command);
        Assert.Equal(PlayerLocomotionState.EnteringForward, boundaryOutput.State);
        Assert.Equal(PlayerLocomotionCommand.EnterForward, boundaryOutput.Command);
    }

    [Fact]
    public void SideStepHeld_ContinuesLoopingWithoutRetrigger()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(1f, 0f));

        PlayerLocomotionOutput held = Step(controller, new Vector2(1f, 0f));
        PlayerLocomotionOutput heldWeaker = Step(controller, new Vector2(0.6f, 0f));

        Assert.Equal(PlayerLocomotionState.SideSteppingRight, held.State);
        Assert.Equal(PlayerLocomotionCommand.None, held.Command);
        Assert.Equal(PlayerLocomotionState.SideSteppingRight, heldWeaker.State);
        Assert.Equal(PlayerLocomotionCommand.None, heldWeaker.Command);
    }

    [Fact]
    public void SideStepReleased_ReturnsToIdleCommand()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(1f, 0f));

        PlayerLocomotionOutput released = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.Idle, released.State);
        Assert.Equal(PlayerLocomotionCommand.Idle, released.Command);
    }

    [Fact]
    public void SideStepSignFlip_SwapsSideStepWithoutIdleInterlude()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(1f, 0f));

        PlayerLocomotionOutput flipped = Step(controller, new Vector2(-1f, 0f));

        Assert.Equal(PlayerLocomotionState.SideSteppingLeft, flipped.State);
        Assert.Equal(PlayerLocomotionCommand.SideStepLeft, flipped.Command);
    }

    [Fact]
    public void ForwardDominanceDuringSideStep_ReturnsToIdleThenEntersNextFrame()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(1f, 0f));

        PlayerLocomotionOutput toIdle = Step(controller, new Vector2(0f, 1f));
        PlayerLocomotionOutput entry = Step(controller, new Vector2(0f, 1f));

        // Longitudinal dominance ends the confined manoeuvre through Idle so the next frame's entry command rides
        // the idle→entry crossfade as one smooth hop (NPC side-step→idle→entry flow).
        Assert.Equal(PlayerLocomotionState.Idle, toIdle.State);
        Assert.Equal(PlayerLocomotionCommand.Idle, toIdle.Command);
        Assert.Equal(PlayerLocomotionState.EnteringForward, entry.State);
        Assert.Equal(PlayerLocomotionCommand.EnterForward, entry.Command);
    }

    [Fact]
    public void SideStepStates_SuppressSyntheticResiduals()
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(1f, 0f));

        Vector2 planar = controller.CalculatePlanarResidual(new Vector2(10f, 0f), Vector2.Zero, 1f, 1f, 0.1f, 0.3f, 0.2f, 100f, 0.5f);
        float yaw = controller.CalculateYawResidual(10f, 0f, 1f, 1f, 1f, 0.1f, 0.5f, 0.25f, 100f, 0.5f);

        // The authored precision manoeuvre is the sole authority, exactly like the pivots.
        Assert.True(planar.IsZeroApprox());
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void EntryResidualRamp_ScalesPlanarAssistanceWithAuthoredEntrySpeed()
    {
        // CTRL-001 UR9: the weight-shift portion of an i2* entry (authored speed ~0) gets no planar assistance;
        // authority ramps linearly to the full budget at the ramp reference and saturates beyond it.
        float still = MeasureEntryPlanarResidual(Vector2.Zero);
        float partial = MeasureEntryPlanarResidual(new Vector2(0.25f, 0f));
        float full = MeasureEntryPlanarResidual(new Vector2(0.5f, 0f));
        float saturated = MeasureEntryPlanarResidual(new Vector2(2f, 0f));

        Assert.True(Mathf.IsZeroApprox(still));
        Assert.InRange(partial, 0.14f, 0.16f);
        Assert.InRange(full, 0.29f, 0.31f);
        Assert.InRange(saturated, 0.29f, 0.31f);
    }

    [Fact]
    public void EntryResidualRamp_ScalesYawAssistanceWithAuthoredEntrySpeed()
    {
        float still = MeasureEntryYawResidual(0f);
        float partial = MeasureEntryYawResidual(0.25f);
        float full = MeasureEntryYawResidual(0.5f);

        Assert.True(Mathf.IsZeroApprox(still));
        Assert.InRange(Mathf.Abs(partial), 0.24f, 0.26f);
        Assert.InRange(Mathf.Abs(full), 0.49f, 0.51f);
    }

    [Fact]
    public void TravellingResiduals_KeepFullAuthorityDespiteEntryRamp()
    {
        PlayerLocomotionController controller = EnterTravel(new Vector2(0f, 1f));

        // Authored speed is zero here, yet sustained travel keeps the full residual budget: the ramp only gates
        // i2* entries, not the travelling regime.
        Vector2 planar = controller.CalculatePlanarResidual(new Vector2(10f, 0f), Vector2.Zero, 1f, 1f, 0.1f, 0.3f, 0.2f, 100f, 0.5f);
        float yaw = controller.CalculateYawResidual(10f, 0f, 0f, 1f, 1f, 0.1f, 0.5f, 0.25f, 100f, 0.5f);

        Assert.InRange(planar.Length(), 0.29f, 0.31f);
        Assert.InRange(Mathf.Abs(yaw), 0.49f, 0.51f);
    }

    private static float MeasureEntryPlanarResidual(Vector2 authoredVelocity)
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, 1f));
        return controller.CalculatePlanarResidual(new Vector2(10f, 0f), authoredVelocity, 1f, 1f, 0.1f, 0.3f, 0.2f, 100f, 0.5f).Length();
    }

    private static float MeasureEntryYawResidual(float authoredPlanarSpeed)
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, new Vector2(0f, 1f));
        return controller.CalculateYawResidual(10f, 0f, authoredPlanarSpeed, 1f, 1f, 0.1f, 0.5f, 0.25f, 100f, 0.5f);
    }

    private static PlayerLocomotionController EnterTravel(Vector2 movement)
    {
        PlayerLocomotionController controller = new();
        _ = Step(controller, movement);
        _ = Step(controller, movement, completed: true);
        return controller;
    }

    private static PlayerLocomotionOutput Step(
        PlayerLocomotionController controller,
        Vector2 movement,
        float turn = 0f,
        bool completed = false)
        => controller.Step(movement, turn, LocomotionPermissions.Allowed, completed, 0.75f, 1.2f);

    [Fact]
    public void TravelRelease_RetainsPresentationAndDoesNotScheduleFiniteExit()
    {
        PlayerLocomotionController controller = EnterTravel(new Vector2(0.2f, 1f));

        PlayerLocomotionOutput released = Step(controller, Vector2.Zero);
        PlayerLocomotionOutput held = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.TravellingForward, released.State);
        Assert.Equal(PlayerLocomotionCommand.None, released.Command);
        Assert.Equal(PlayerLocomotionCommand.None, held.Command);
        Assert.Equal(new Vector2(0.2f, 1f), released.Blend);
        Assert.Equal(new Vector2(0.2f, 1f), held.Blend);
    }

    [Fact]
    public void ManualAndOptionalNavigationReleases_ShareTheFailClosedExitBoundary()
    {
        foreach (string source in new[] { "manual input", "optional navigation" })
        {
            PlayerLocomotionController controller = EnterTravel(new Vector2(0f, 1f));

            PlayerLocomotionOutput released = Step(controller, Vector2.Zero);

            Assert.True(released.State == PlayerLocomotionState.TravellingForward, $"{source} must retain travel without release evidence.");
            Assert.True(released.Command == PlayerLocomotionCommand.None, $"{source} must not schedule an arbitrary exit.");
            Assert.True(released.CommandLane == PlayerLocomotionPhaseLane.Left, $"{source} must not select a replacement exit lane.");
        }
    }

    [Fact]
    public void RetiredExitStrategiesAndPhaseGate_AreNotSelectable()
    {
        System.Reflection.Assembly assembly = typeof(PlayerLocomotionController).Assembly;

        Assert.Null(assembly.GetType("AlleyCat.Control.Locomotion.PlayerTravelExitStrategy"));
        Assert.Null(assembly.GetType("AlleyCat.Control.Locomotion.PlayerImmediateExitSlicer"));
        Assert.Null(assembly.GetType("AlleyCat.Control.Locomotion.PlayerExitReleaseGate"));
        Assert.Null(typeof(CharacterLocomotion).GetProperty("TravelExitStrategy"));
    }
}
