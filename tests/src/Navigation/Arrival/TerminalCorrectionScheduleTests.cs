using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>
/// Analytic contracts of <see cref="TerminalCorrectionSchedule"/>: it replays the runtime movement operation's
/// capped correction exactly, so its realised end is bounded by the allocator's own caps, refuses ineligible
/// windows, and carries native rotation and travel in the world frame. Expectations are hand-computed from the
/// <see cref="SwingCorrectionCaps"/> defaults, never from the planner.
/// </summary>
public class TerminalCorrectionScheduleTests
{
    private static SwingCorrectionCaps Caps => new();

    private readonly SwingCorrectionAllocator _allocator = new(Caps);

    /// <summary>A residual within one single-swing window's caps closes to the target exactly.</summary>
    [Fact]
    public void WithinCapResidualClosesExactly()
    {
        SequencePrediction prediction = Walk(predictionTicks: 10, semanticStep: Vector2.Zero);
        ArrivalTarget target = new(new Vector2(0.03f, 0f), 0f);

        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(prediction, target, _allocator);

        Assert.True(outcome.ClosesWithin(0.01f, Mathf.DegToRad(1f)),
            $"error {outcome.PositionErrorMetres * 1000:0.###} mm / {Mathf.RadToDeg(outcome.YawErrorRadians):0.###} deg");
    }

    /// <summary>A lateral residual beyond every cap stays beyond it: the schedule never moves more than the caps.</summary>
    [Fact]
    public void BeyondCapResidualKeepsTheExcess()
    {
        SequencePrediction prediction = Walk(predictionTicks: 10, semanticStep: Vector2.Zero);
        float demand = Caps.LateralMetres + 0.15f;
        ArrivalTarget target = new(new Vector2(demand, 0f), 0f);

        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(prediction, target, _allocator);

        // One single-swing window can spend at most its lateral cap to the right; the excess survives.
        Assert.False(outcome.ClosesWithin(0.01f, Mathf.DegToRad(1f)));
        Assert.InRange(outcome.PositionErrorMetres, demand - Caps.LateralMetres - 0.0005f, demand - Caps.LateralMetres + 0.0005f);
    }

    /// <summary>Windows without correction eligibility (double support) leave the native end untouched.</summary>
    [Fact]
    public void DoubleSupportOnlyNeverCorrects()
    {
        SequencePrediction prediction = Stand(10, semanticStep: new Vector2(0.001f, 0.002f));
        ArrivalTarget target = new(new Vector2(0.05f, 0.05f), 0f);

        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(prediction, target, _allocator);

        Assert.Equal(prediction.EndPose.Position.X, outcome.EndPose.Position.X, 5);
        Assert.Equal(prediction.EndPose.Position.Y, outcome.EndPose.Position.Y, 5);
        Assert.Equal(prediction.EndPose.Yaw, outcome.EndPose.Yaw, 5);
        Assert.False(outcome.ClosesWithin(0.01f, Mathf.DegToRad(1f)));
    }

    /// <summary>The per-tick residual is measured against the rotating actor frame: a native quarter-turn walk closes
    /// a small world offset expressed in the start frame without inventing yaw.</summary>
    [Fact]
    public void NativeRotationIsCarriedInTheWorldFrame()
    {
        const int ticks = 20;
        float quarter = Mathf.Pi / (2f * ticks);
        SequencePrediction prediction = Walk(ticks, Vector2.Zero, quarter);
        // The native end: quarter-turned with no translation; aim 20 mm to its right in the start frame.
        float endYaw = quarter * ticks;
        ArrivalTarget target = new(SequencePose.SemanticToWorld(new Vector2(0.02f, 0f), 0f), endYaw);

        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(prediction, target, _allocator);

        Assert.True(outcome.ClosesWithin(0.01f, Mathf.DegToRad(1f)),
            $"error {outcome.PositionErrorMetres * 1000:0.###} mm / {Mathf.RadToDeg(outcome.YawErrorRadians):0.###} deg");
        Assert.InRange(Mathf.Abs(Mathf.AngleDifference(outcome.EndPose.Yaw, endYaw)), 0f, Mathf.DegToRad(1f));
    }

    /// <summary>A rejected prediction has no schedule.</summary>
    [Fact]
    public void RejectedPredictionIsRefused()
    {
        SequencePrediction prediction = SequencePredictorTestsRejected();

        _ = Assert.Throws<ArgumentException>(
            () => TerminalCorrectionSchedule.Simulate(prediction, new ArrivalTarget(Vector2.Zero, 0f), _allocator));
    }

    private static SequencePrediction SequencePredictorTestsRejected() => new(new SequencePose(Vector2.Zero, 0f),
        new SequenceRejection(SequenceRejectionReason.InvalidRequest, -1, "test"));

    /// <summary>One whole single-swing window (the left foot swings, the right ball is planted at the origin).</summary>
    private static SequencePrediction Walk(int predictionTicks, Vector2 semanticStep, float yawStep = 0f)
    {
        List<PredictedTick> ticks = [];
        Vector2 position = Vector2.Zero;
        float yaw = 0f;
        for (int index = 0; index < predictionTicks; index++)
        {
            yaw += yawStep;
            position += SequencePose.SemanticToWorld(semanticStep, yaw);
            ticks.Add(new PredictedTick(
                index,
                -1,
                false,
                new PredictedClipSample("wf", index, 1d),
                null,
                semanticStep,
                yawStep,
                new SequencePose(position, yaw),
                new PlantedFeetState(true, false),
                Vector3.Zero,
                Vector3.Zero));
        }

        return new SequencePrediction(
            new SequencePose(Vector2.Zero, 0f),
            [.. ticks],
            [new SupportWindow(SupportPhase.RightSwing, 0, predictionTicks - 1, new SequencePose(Vector2.Zero, 0f),
                ticks[^1].Pose)],
            []);
    }

    /// <summary>One whole double-support window (both feet planted: no correction anywhere).</summary>
    private static SequencePrediction Stand(int predictionTicks, Vector2 semanticStep)
    {
        List<PredictedTick> ticks = [];
        Vector2 position = Vector2.Zero;
        for (int index = 0; index < predictionTicks; index++)
        {
            position += SequencePose.SemanticToWorld(semanticStep, 0f);
            ticks.Add(new PredictedTick(
                index,
                -1,
                false,
                new PredictedClipSample("idle", index, 1d),
                null,
                semanticStep,
                0f,
                new SequencePose(position, 0f),
                new PlantedFeetState(true, true),
                Vector3.Zero,
                Vector3.Zero));
        }

        return new SequencePrediction(
            new SequencePose(Vector2.Zero, 0f),
            [.. ticks],
            [new SupportWindow(SupportPhase.BothPlanted, 0, predictionTicks - 1, new SequencePose(Vector2.Zero, 0f),
                ticks[^1].Pose)],
            []);
    }
}
