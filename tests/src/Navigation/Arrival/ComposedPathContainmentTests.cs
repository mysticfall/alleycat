using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>
/// Geometry contracts of <see cref="ComposedPathContainment"/> (CORNER-07): the composed trajectory — realised
/// poses plus consecutive midpoints — is checked against a supplied surface oracle, so a segment that crosses
/// off the surface and returns is caught even though both endpoints are on it, on-boundary probes count as
/// on-surface, and the correction schedule's composed-position recording carries exactly the start pose plus one
/// sample per replayed tick. Expectations are hand-constructed surfaces, never derived from the production
/// navmesh query.
/// </summary>
public class ComposedPathContainmentTests
{
    /// <summary>A one-dimensional surface oracle over world X: on-surface for x ≤ 0, off-surface by x beyond it.</summary>
    private static float DepthPastHalfPlane(Vector2 worldXz) => MathF.Max(worldXz.X, 0f);

    /// <summary>A composed path whose endpoints are on the surface but whose segment crosses off it is rejected:
    /// the consecutive midpoint probe is what sees the crossing an endpoint-only check would miss.</summary>
    [Fact]
    public void SegmentCrossingOffSurfaceIsRejectedByTheMidpointProbe()
    {
        List<Vector2> composed = [new(-0.05f, 0f), new(0.05f, 0f), new(-0.05f, 0f)];

        ComposedPathContainmentReport report = ComposedPathContainment.Check(composed, DepthPastHalfPlane);

        Assert.False(report.IsContained);
        Assert.Equal(3, composed.Count);
        Assert.True(report.WorstDepthMetres > 0.0499f, $"worst {report.WorstDepthMetres:0.####}");
        Assert.Equal(1, report.FirstOffSurfaceTick);
    }

    /// <summary>A pose exactly on the surface boundary counts as on-surface: the epsilon is float-representation
    /// slack, never a behavioural margin.</summary>
    [Fact]
    public void OnBoundaryPoseIsOnSurface()
    {
        List<Vector2> composed = [new(0f, 0f), new(0f, 0.01f)];

        ComposedPathContainmentReport report = ComposedPathContainment.Check(composed, DepthPastHalfPlane);

        Assert.True(report.IsContained);
        Assert.Equal(0f, report.WorstDepthMetres);
        Assert.Equal(-1, report.FirstOffSurfaceTick);
    }

    /// <summary>A fully on-surface composed path is admitted and probes every pose and midpoint exactly once.</summary>
    [Fact]
    public void OnSurfacePathIsAdmitted()
    {
        List<Vector2> composed = [new(-0.1f, 0f), new(-0.1f, 0.017f), new(-0.09f, 0.034f)];

        ComposedPathContainmentReport report = ComposedPathContainment.Check(composed, DepthPastHalfPlane);

        Assert.True(report.IsContained);
        Assert.Equal(5, report.ProbeCount);
    }

    /// <summary>An interior pose off the surface is rejected, with the worst depth reported; the first offending
    /// probe is the consecutive midpoint that first crosses the surface boundary.</summary>
    [Fact]
    public void OffSurfacePoseIsRejectedOnItsTick()
    {
        List<Vector2> composed = [new(-0.1f, 0f), new(-0.02f, 0.01f), new(0.03f, 0.02f)];

        ComposedPathContainmentReport report = ComposedPathContainment.Check(composed, DepthPastHalfPlane);

        Assert.False(report.IsContained);
        Assert.Equal(2, report.FirstOffSurfaceTick);
        Assert.True(report.FirstOffSurfaceWasMidpoint);
        Assert.InRange(report.WorstDepthMetres, 0.03f, 0.0301f);
    }

    /// <summary>The correction schedule records the composed trajectory it realises: the start pose followed by
    /// one world position per replayed tick, the last being the outcome's end pose.</summary>
    [Fact]
    public void ScheduleRecordsTheComposedPositionsItRealises()
    {
        const int ticks = 10;
        SequencePrediction prediction = Walk(ticks, semanticStep: new Vector2(0f, 0.01f));
        // Forward is world -Y in the sequence frame: aim the schedule exactly at the native end so the replayed
        // correction is zero and the recorded samples are the native walk itself.
        ArrivalTarget target = new(new Vector2(0f, -0.1f), 0f);
        var allocator = new SwingCorrectionAllocator(new SwingCorrectionCaps());
        List<Vector2> composed = [];

        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(
            prediction, target, allocator, continuingPhase: null, composedPositions: composed);

        Assert.Equal(ticks + 1, composed.Count);
        Assert.Equal(prediction.StartPose.Position, composed[0]);
        Assert.Equal(outcome.EndPose.Position.X, composed[^1].X, 5);
        Assert.Equal(outcome.EndPose.Position.Y, composed[^1].Y, 5);
        for (int index = 1; index < composed.Count; index++)
        {
            Assert.True(composed[index].Y < composed[index - 1].Y, "the composed walk advances every tick");
        }
    }

    /// <summary>One whole single-swing window (the right foot swings, the left ball is planted at the origin).</summary>
    private static SequencePrediction Walk(int predictionTicks, Vector2 semanticStep)
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
                new PredictedClipSample("wf", index, 1d),
                null,
                semanticStep,
                0f,
                new SequencePose(position, 0f),
                new PlantedFeetState(true, false),
                Vector3.Zero,
                new Vector3(0.1f, 0f, 0f)));
        }

        return new SequencePrediction(
            new SequencePose(Vector2.Zero, 0f),
            [.. ticks],
            [new SupportWindow(SupportPhase.RightSwing, 0, predictionTicks - 1, new SequencePose(Vector2.Zero, 0f),
                ticks[^1].Pose)],
            []);
    }
}
