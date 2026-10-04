using AlleyCat.Control.Locomotion;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Control;

public sealed class PlayerLocomotionControllerTests
{
    [Fact]
    public void MovementFromIdle_SchedulesAuthoredEntryForSelectedLane()
    {
        var controller = new PlayerLocomotionController();

        PlayerLocomotionOutput output = Step(controller, new(0.4f, 0.8f));

        Assert.Equal(PlayerLocomotionState.EnteringForward, output.State);
        Assert.Equal(PlayerLocomotionCommand.EnterForward, output.Command);
        Assert.Equal(PlayerLocomotionPhaseLane.Right, output.CommandLane);
    }

    [Fact]
    public void NeutralDuringEntry_RequestsSharedExitOnFirstEligibleTick()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, 1f));

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.ExitingForward, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitForward, output.Command);
    }

    [Fact]
    public void LateralReleaseDuringForwardEntry_RoutesThroughTheNeutralStop()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(1f, 0f));

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        // A pure-lateral strafe enters through the forward entry clip, but its release still classifies on
        // the bout intent: UR6/UR11 bind at any release point, including during an entry, so a
        // lateral-dominant release never takes the forward stop clip's authored forward root.
        Assert.Equal(PlayerLocomotionState.ExitingNeutral, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitNeutral, output.Command);
    }

    [Fact]
    public void LateralHeavyBackwardReleaseDuringBackwardEntry_RoutesThroughTheNeutralStop()
    {
        var controller = new PlayerLocomotionController();
        PlayerLocomotionOutput entry = Step(controller, new(0.8f, -0.3f));
        Assert.Equal(PlayerLocomotionState.EnteringBackward, entry.State);

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.ExitingNeutral, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitNeutral, output.Command);
    }

    [Fact]
    public void BackwardReleaseDuringBackwardEntry_KeepsTheAuthoredBackwardStop()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, -1f));

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.ExitingBackward, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitBackward, output.Command);
    }

    [Fact]
    public void NeutralDuringTravel_RequestsSharedExitOnFirstEligibleTick()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, 1f));
        _ = Step(controller, new(0f, 1f), completed: true);

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.ExitingForward, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitForward, output.Command);
    }

    [Fact]
    public void LateralDominantRelease_RoutesThroughTheNeutralStop()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(1f, 0f));
        _ = Step(controller, new(1f, 0f), completed: true);

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        // A lateral-dominant release must not route through the forward stop clip (its authored forward
        // root rotates the remaining motion into a forward glide): it takes the neutral stop policy.
        Assert.Equal(PlayerLocomotionState.ExitingNeutral, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitNeutral, output.Command);
    }

    [Fact]
    public void LateralHeavyBackwardDiagonalRelease_RoutesThroughTheNeutralStop()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0.8f, -0.3f));
        _ = Step(controller, new(0.8f, -0.3f), completed: true);

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.ExitingNeutral, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitNeutral, output.Command);
    }

    [Fact]
    public void EvenForwardDiagonalRelease_KeepsTheAuthoredForwardStop()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0.7071f, 0.7071f));
        _ = Step(controller, new(0.7071f, 0.7071f), completed: true);

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.ExitingForward, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitForward, output.Command);
    }

    [Fact]
    public void BackwardDominantRelease_RoutesThroughTheAuthoredBackwardStop()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, -1f));
        _ = Step(controller, new(0f, -1f), completed: true);

        PlayerLocomotionOutput output = Step(controller, Vector2.Zero);

        Assert.Equal(PlayerLocomotionState.ExitingBackward, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitBackward, output.Command);
    }

    [Fact]
    public void NeutralStop_CompletesToIdleAndAcceptsFreshIntentWithoutInterlude()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(1f, 0f));
        _ = Step(controller, new(1f, 0f), completed: true);
        _ = Step(controller, Vector2.Zero);

        PlayerLocomotionOutput completed = Step(controller, Vector2.Zero, completed: true);
        PlayerLocomotionOutput resumed = Step(controller, new(1f, 0f));

        Assert.Equal(PlayerLocomotionState.Idle, completed.State);
        Assert.Equal(PlayerLocomotionCommand.Idle, completed.Command);
        Assert.Equal(PlayerLocomotionState.EnteringForward, resumed.State);
        Assert.Equal(PlayerLocomotionCommand.EnterForward, resumed.Command);
    }

    [Fact]
    public void SameDirectionIntentDuringNeutralStop_InvalidatesItWithoutIdleInterlude()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(1f, 0f));
        _ = Step(controller, new(1f, 0f), completed: true);
        _ = Step(controller, Vector2.Zero);

        PlayerLocomotionOutput output = Step(controller, new(1f, 0f));

        Assert.Equal(PlayerLocomotionState.TravellingDirectional, output.State);
        Assert.Equal(PlayerLocomotionCommand.Travel, output.Command);
    }

    [Fact]
    public void ReversalDuringTravel_RoutesThroughAuthoredExitForBraking()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, 1f));
        _ = Step(controller, new(0f, 1f), completed: true);

        PlayerLocomotionOutput output = Step(controller, new(0f, -1f));

        // CTRL-001 reversal cadence: the still-forward realised motion is braked by the authored exit —
        // the opposite direction enters through the idle→entry path once the stop has played out, never
        // by an instant full-rate opposite-gait retarget.
        Assert.Equal(PlayerLocomotionState.ExitingForward, output.State);
        Assert.Equal(PlayerLocomotionCommand.ExitForward, output.Command);
    }

    [Fact]
    public void OpposingIntentDuringExit_PlaysAuthoredStopOut()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, 1f));
        _ = Step(controller, new(0f, 1f), completed: true);
        _ = Step(controller, Vector2.Zero);

        PlayerLocomotionOutput output = Step(controller, new(0f, -1f));

        // Fresh intent that OPPOSES the travelled direction does not invalidate the pending stop: the
        // authored braking plays out first (same-direction resumes still replace it, below).
        Assert.Equal(PlayerLocomotionState.ExitingForward, output.State);
        Assert.Equal(PlayerLocomotionCommand.None, output.Command);
    }

    [Fact]
    public void SameDirectionIntentDuringExit_InvalidatesStopWithoutIdleInterlude()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, 1f));
        _ = Step(controller, new(0f, 1f), completed: true);
        _ = Step(controller, Vector2.Zero);

        PlayerLocomotionOutput output = Step(controller, new(0f, 1f));

        Assert.Equal(PlayerLocomotionState.TravellingForward, output.State);
        Assert.Equal(PlayerLocomotionCommand.Travel, output.Command);
    }

    [Fact]
    public void PermissionLoss_FailsClosedAndEchoesPermissions()
    {
        var controller = new PlayerLocomotionController();
        _ = Step(controller, new(0f, 1f));
        _ = Step(controller, new(0f, 1f), completed: true);
        var denied = new LocomotionPermissions(false, false);

        PlayerLocomotionOutput output = controller.Step(new(0f, 1f), Vector2.Zero, 1f, denied, false);

        Assert.Equal(PlayerLocomotionState.Idle, output.State);
        Assert.Equal(PlayerLocomotionCommand.Idle, output.Command);
        Assert.False(output.MovementAllowed);
        Assert.False(output.RotationAllowed);
    }

    private static PlayerLocomotionOutput Step(
        PlayerLocomotionController controller,
        Vector2 movement,
        bool completed = false)
        => controller.Step(movement, Vector2.Zero, 0f, LocomotionPermissions.Allowed, completed);
}
