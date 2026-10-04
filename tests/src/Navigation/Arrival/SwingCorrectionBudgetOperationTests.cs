using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>
/// CORNER-03/09/10 budget-operation contracts, imported from the independent phase-budget review's executed red
/// cases (archived PhaseBudgetReviewTests.cs.snapshot) with their assertions unchanged: applied increments are
/// consumed once per direction (never net displacement or maximum excursion, in flight as scalar path), a fresh
/// phase preserves the stateless Planar/yaw operation exactly (the translation allowance constrains the independent
/// <c>Planar</c> alone; the pivot-origin shift is the bounded yaw's displacement, not a second translation grant),
/// and a yaw bounded by the remaining allowance carries ALL its dependent geometry — endpoint compensation as well
/// as the pivot-origin shift — computed for the applied yaw by the one shared operation.
/// </summary>
public class SwingCorrectionBudgetOperationTests
{
    private static SwingCorrectionCaps Caps => new();

    private readonly SwingCorrectionAllocator _allocator = new(Caps);

    /// <summary>
    /// Returning over an earlier excursion does not make applied motion free to repeat: within one continuing
    /// physical phase (first grant 10 ticks, then same-phase fragments — no genuine metadata phase change),
    /// +30, −30, +30 mm must not apply 60 mm of rightward motion against the 50 mm directional allowance, and a
    /// flight's +36, −36, +36 mm must not consume 108 mm of path against its 60 mm scalar allowance.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamePhysicalPhase_ReversalsConsumeEachAppliedIncrement(bool flight)
    {
        SupportPhase phase = flight ? SupportPhase.BothSwing : SupportPhase.RightSwing;
        SupportWindow Window(int count)
        {
            return new SupportWindow(phase, 0, count - 1, new SequencePose(Vector2.Zero, 0f),
                new SequencePose(flight ? new Vector2(0.4f, 0f) : Vector2.Zero, 0f));
        }

        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(Window(10), _allocator.Caps)!;
        double positive = 0d;
        double path = 0d;
        foreach (int direction in new[] { 1, -1, 1 })
        {
            SupportWindow fragment = Window(direction == 1 && positive == 0d ? 10 : 6);
            for (int tick = 0; tick < 6; tick++)
            {
                SwingCorrection correction = _allocator.Step(new SwingCorrectionResidual(new Vector2(direction, 0f), 0f),
                    fragment, tick, [], Vector2.Zero, Vector2.Zero, phase, budget);
                float delta = (correction.Planar + correction.OriginShift).X;
                positive += Math.Max(0f, delta);
                path += Math.Abs(delta);
                budget = budget.Charge(correction);
            }
        }

        double spent = flight ? path : positive;
        double allowance = flight ? budget.FlightBudgetMetres : _allocator.Caps.LateralMetres;
        Assert.True(spent <= allowance + 1e-6d,
            $"Applied expenditure {spent:R} exceeds original allowance {allowance:R} within one physical phase " +
            $"(positive {positive:R}, path {path:R}, spent right {budget.SpentRight:R}, spent left {budget.SpentLeft:R}, flight spent {budget.FlightSpent:R}).");
    }

    /// <summary>
    /// A fresh phase (no consumption, original basis = this window) must reproduce the stateless operation exactly:
    /// the full 50 mm lateral <c>Planar</c> plus the 3° yaw's pivot-origin shift, with the yaw separately spent once
    /// — never the old Planar cap silently shared with or deducted by the shift.
    /// </summary>
    [Fact]
    public void FreshPhase_PreservesExistingPlanarAndYawOperation()
    {
        var pose = new SequencePose(Vector2.Zero, 0f);
        var window = new SupportWindow(SupportPhase.RightSwing, 0, 0, pose, pose);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(window, _allocator.Caps)!;
        var ball = new Vector2(0f, 0.2f);
        var residual = new SwingCorrectionResidual(new Vector2(0.05f, 0f), _allocator.Caps.YawRadians);

        SwingCorrection stateless = _allocator.Step(residual, window, 0, [], ball, ball, SupportPhase.RightSwing);
        SwingCorrection budgeted = _allocator.Step(residual, window, 0, [], ball, ball, SupportPhase.RightSwing, budget);

        Assert.Equal(stateless, budgeted);
        Assert.Equal(0.05f, stateless.Planar.X, 5);
        Assert.Equal(0.010467192f, stateless.OriginShift.X, 5);
        Assert.Equal(_allocator.Caps.YawRadians, budget.Charge(budgeted).SpentYawLeft, 5);
    }

    /// <summary>
    /// A yaw limited by the remaining allowance must carry its endpoint-lever compensation as well as its
    /// pivot-origin shift: with the ball at the origin, an endpoint lever of (1, 0), zero position residual and a
    /// 3° demand against 0.3° remaining, the <c>Planar</c> compensation must equal lever − lever.Rotated(APPLIED yaw)
    /// (≈ (0.0000137, −0.005236) m), not the discarded 3° compensation (error 47.12 mm).
    /// </summary>
    [Fact]
    public void BoundedYaw_RecomputesEndpointCompensationAsWellAsOriginShift()
    {
        var pose = new SequencePose(Vector2.Zero, 0f);
        var window = new SupportWindow(SupportPhase.RightSwing, 0, 0, pose, pose);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(window, _allocator.Caps)!
            .Charge(new SwingCorrection(Vector2.Zero, _allocator.Caps.YawRadians * 0.9f, SwingCorrectionPivot.Ball, Vector2.Zero));

        SwingCorrection candidate = _allocator.Step(new SwingCorrectionResidual(Vector2.Zero, _allocator.Caps.YawRadians),
            window, 0, [], Vector2.Zero, Vector2.Right, SupportPhase.RightSwing, budget);

        Vector2 expected = Vector2.Right - Vector2.Right.Rotated(candidate.Yaw);
        Assert.InRange(candidate.Yaw, 0f, budget.RemainingYaw + 1e-7f);
        Assert.InRange((candidate.Planar - expected).Length(), 0f, 1e-6f);
        Assert.Equal(Vector2.Zero, candidate.OriginShift);
    }

    /// <summary>
    /// A fresh phase must retain the existing multi-window late-yaw schedule in both directions: with two later
    /// eligible windows, an 8° demand schedules 2°+3°+3° — the first tick's yaw is 0.2°, not zero (the whole-residual
    /// clamp defect postponed everything to the last window). The budgeted proposal equals the stateless one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void FreshPhase_WithFutureWindows_PreservesOriginalOperation(int sign)
    {
        SupportWindow current = Window(SupportPhase.RightSwing, 0, 10);
        SupportWindow[] future = [Window(SupportPhase.LeftSwing, 10, 10), Window(SupportPhase.RightSwing, 20, 10)];
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(current, _allocator.Caps)!;
        Vector2 ball = new(0.12f, 0.05f);
        Vector2 endpoint = new(0.5f, 0.2f);
        var residual = new SwingCorrectionResidual(Vector2.Zero, Mathf.DegToRad(8f) * sign);

        SwingCorrection stateless = _allocator.Step(residual, current, 0, future, ball, endpoint, current.Phase);
        SwingCorrection budgeted = _allocator.Step(residual, current, 0, future, ball, endpoint, current.Phase, budget);

        Assert.InRange(Mathf.Abs(stateless.Yaw - (Mathf.DegToRad(0.2f) * sign)), 0f, 1e-7f);
        Assert.Equal(stateless, budgeted);
    }

    /// <summary>
    /// Independent constructive schedules close fresh and partly consumed three-phase yaw requests: 0.2°/tick + two
    /// 3° phases close an 8° demand, and 0.8° over the three ticks a 2.1°-spent phase has left (each rate ≤ the
    /// 0.3°/tick original rate, one 3° absolute allowance per phase) closes the carried 6.8° demand — both within
    /// the unchanged 1° arrival bound. The whole-residual clamp left 4.999985°/3.800003° errors instead.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegalThreePhaseYawSchedule_ClosesWithinOriginalArrivalBound(bool carried)
    {
        SupportWindow first = Window(SupportPhase.RightSwing, 0, carried ? 3 : 10);
        SupportWindow[] windows = [first, Window(SupportPhase.LeftSwing, first.TickCount, 10), Window(SupportPhase.RightSwing, first.TickCount + 10, 10)];
        SwingCorrectionPhaseBudget? seed = null;
        float startYaw = 0f;
        if (carried)
        {
            SupportWindow original = Window(SupportPhase.RightSwing, 0, 10);
            seed = SwingCorrectionPhaseBudget.ForWindow(original, _allocator.Caps)!;
            for (int tick = 0; tick < 7; tick++)
            {
                SwingCorrection correction = _allocator.Step(new SwingCorrectionResidual(Vector2.Zero, 1f), original,
                    tick, [], Vector2.Zero, Vector2.Zero, original.Phase, seed);
                seed = seed.Charge(correction);
                startYaw += correction.Yaw;
            }

            Assert.InRange(Mathf.RadToDeg(startYaw), 2.0999f, 2.1001f);
        }

        float demand = Mathf.DegToRad(carried ? 6.8f : 8f);
        float[] legalPerTickDegrees = carried ? [0.8f / 3f, 0.3f, 0.3f] : [0.2f, 0.3f, 0.3f];
        float legalTotal = 0f;
        for (int index = 0; index < windows.Length; index++)
        {
            Assert.InRange(legalPerTickDegrees[index], 0f, 0.3f);
            legalTotal += legalPerTickDegrees[index] * windows[index].TickCount;
        }

        Assert.InRange(Mathf.Abs(Mathf.DegToRad(legalTotal) - demand), 0f, 1e-7f);
        SequencePrediction prediction = Standing(windows, startYaw);
        TerminalCorrectionOutcome result = TerminalCorrectionSchedule.Simulate(prediction,
            new ArrivalTarget(Vector2.Zero, startYaw + demand), _allocator, seed);
        Assert.True(result.ClosesWithin(0.01f, Mathf.DegToRad(1f)),
            $"Constructively legal multi-phase request must meet the unchanged 1 deg arrival bound " +
            $"(realised {Mathf.RadToDeg(result.EndPose.Yaw - startYaw):R} deg, remaining {Mathf.RadToDeg(result.YawErrorRadians):R} deg).");
    }

    /// <summary>
    /// A sign reversal cannot create a second absolute yaw allowance in the same physical phase: +3° followed by −3°
    /// of demand must not consume 6° against the one 3° ABSOLUTE phase cap.
    /// </summary>
    [Fact]
    public void SamePhysicalPhase_YawReversalDoesNotGrantAnotherAbsoluteCap()
    {
        SupportWindow original = Window(SupportPhase.RightSwing, 0, 10);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(original, _allocator.Caps)!;
        double absolute = 0d;
        foreach (int direction in new[] { 1, -1 })
        {
            SupportWindow fragment = Window(SupportPhase.RightSwing, 0, 10);
            for (int tick = 0; tick < 10; tick++)
            {
                SwingCorrection correction = _allocator.Step(new SwingCorrectionResidual(Vector2.Zero, direction),
                    fragment, tick, [], Vector2.Zero, Vector2.Zero, fragment.Phase, budget);
                absolute += Math.Abs(correction.Yaw);
                budget = budget.Charge(correction);
            }
        }

        Assert.True(absolute <= _allocator.Caps.YawRadians + 1e-7,
            $"Sign switch must not multiply the existing absolute phase yaw allowance: {absolute * 180d / Math.PI:R} deg consumed " +
            $"against {_allocator.Caps.YawRadians * 180d / Math.PI:R} deg.");
    }

    /// <summary>
    /// Applied bounded yaw drives both lever compensation and a nonzero pivot displacement: with 0.3° remaining and a
    /// 3° demand, the yaw is 0.3°, Planar equals lever − lever.Rotated(applied yaw) (≈ (0.00053727627, −0.0052345917) m),
    /// OriginShift equals ball − ball.Rotated(applied yaw), Planar is charged and the total recorded once.
    /// </summary>
    [Fact]
    public void BoundedYaw_WithNonzeroPivot_UsesAppliedYawForBothCompensations()
    {
        SupportWindow window = Window(SupportPhase.RightSwing, 0, 1);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(window, _allocator.Caps)!
            .Charge(new SwingCorrection(Vector2.Zero, Mathf.DegToRad(2.7f), SwingCorrectionPivot.Ball, Vector2.Zero));
        Vector2 ball = new(0.12f, 0.05f);
        Vector2 lever = new(1f, 0.1f);

        SwingCorrection correction = _allocator.Step(new SwingCorrectionResidual(Vector2.Zero, Mathf.DegToRad(3f)),
            window, 0, [], ball, ball + lever, window.Phase, budget);

        Assert.InRange(Mathf.Abs(correction.Yaw - Mathf.DegToRad(0.3f)), 0f, 1e-7f);
        Assert.InRange((correction.Planar - (lever - lever.Rotated(correction.Yaw))).Length(), 0f, 1e-6f);
        Assert.Equal(ball - ball.Rotated(correction.Yaw), correction.OriginShift);
        SwingCorrectionPhaseBudget charged = budget.Charge(correction);
        Assert.InRange(Math.Abs(charged.SpentRight - Math.Max(0f, correction.Planar.X)), 0d, 1e-8);
        Assert.InRange(Math.Abs(charged.CumulativeRight - (correction.Planar + correction.OriginShift).X), 0d, 1e-8);
    }

    private static SupportWindow Window(SupportPhase phase, int first, int count) =>
        new(phase, first, first + count - 1, new SequencePose(Vector2.Zero, 0f), new SequencePose(Vector2.Zero, 0f));

    private static SequencePrediction Standing(SupportWindow[] windows, float yaw)
    {
        var pose = new SequencePose(Vector2.Zero, yaw);
        List<PredictedTick> ticks = [];
        foreach (SupportWindow window in windows)
        {
            for (int index = window.FirstTick; index <= window.LastTick; index++)
            {
                var planted = new PlantedFeetState(window.Phase == SupportPhase.RightSwing, window.Phase == SupportPhase.LeftSwing);
                ticks.Add(new PredictedTick(index, -1, false, new PredictedClipSample("wf", index, 1d), null,
                    Vector2.Zero, 0f, pose, planted, Vector3.Zero, Vector3.Zero));
            }
        }

        return new SequencePrediction(pose, [.. ticks], [.. windows], []);
    }
}
