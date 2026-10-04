using AlleyCat.Control.Locomotion;
using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>
/// CORNER-03 physical-phase allowance contracts: the consumed allowance of one physical support phase is a value
/// that survives plan changes and request supersession, is never refunded by reversal, keeps its original per-tick
/// spreading limits, and gates both the runtime allocator and the terminal admission simulator through the same
/// charging rule. The exact independent counterexample (35 mm spent + 45 mm freshly granted inside one 50 mm
/// physical lateral allowance; legal residual ≥ 30 mm) is reproduced through the production pieces the runtime uses.
/// </summary>
public class SwingCorrectionPhaseBudgetTests
{
    private static SwingCorrectionCaps Caps => new();

    private readonly SwingCorrectionAllocator _allocator = new(Caps);

    /// <summary>
    /// The recorded counterexample: one physical 10-tick RightSwing with no native motion, 7 ticks spend ~35 mm of
    /// the 50 mm lateral allowance; a new 3-tick prediction of the SAME phase must not close a target 80 mm out.
    /// The legal end is ≤ 50 mm (residual ≥ 30 mm), so the 10 mm arrival tolerance cannot be met — while the same
    /// prediction from FRESH history still falsely closes at 80 mm, which is exactly the replenishment defect.
    /// </summary>
    [Fact]
    public void TruncatedSamePhaseWindow_DoesNotReplenishSpentLateralAllowance()
    {
        SupportWindow physical = Window(SupportPhase.RightSwing, ticks: 10);
        SwingCorrectionPhaseBudget budget = SpendPhysicalPhase(physical, ticks: 7, demand: 0.08f);
        Assert.InRange(budget.SpentRight, 0.034999, 0.035001);
        Assert.InRange(budget.RemainingRight, 0.014999, 0.015001);

        SequencePrediction truncated = Standing(Window(SupportPhase.RightSwing, ticks: 3, first: 0),
            new SequencePose(new Vector2((float)budget.CumulativeRight, 0f), 0f));
        ArrivalTarget target = new(new Vector2(0.08f, 0f), 0f);

        // Fresh history (the pre-fix behaviour) still closes at the full 80 mm: the discriminating control.
        TerminalCorrectionOutcome fresh = TerminalCorrectionSchedule.Simulate(truncated, target, _allocator);
        Assert.True(fresh.ClosesWithin(0.01f, Mathf.DegToRad(1f)), "SETUP: fresh history must reproduce the recorded false closure.");

        // The carried physical-phase allowance caps the same prediction at the legal end.
        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(truncated, target, _allocator, budget);
        Assert.False(outcome.ClosesWithin(0.01f, Mathf.DegToRad(1f)));
        Assert.InRange(outcome.EndPose.Position.X, 0f, 0.050001f);
        Assert.InRange(outcome.PositionErrorMetres, 0.029999f, 0.08f);
    }

    /// <summary>A truncated window of a partly spent phase keeps the ORIGINAL per-tick spreading limit, so a large
    /// residual cannot be jumped in fewer ticks than the original grant would have allowed.</summary>
    [Fact]
    public void TruncatedSamePhaseWindow_KeepsOriginalPerTickAllowance()
    {
        SupportWindow physical = Window(SupportPhase.RightSwing, ticks: 10);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(physical, Caps)!;
        SupportWindow truncated = Window(SupportPhase.RightSwing, ticks: 2);

        SwingCorrection step = _allocator.Step(
            new SwingCorrectionResidual(new Vector2(0.3f, 0f), 0f), truncated, 0, [], Vector2.Zero, Vector2.Zero,
            SupportPhase.RightSwing, budget);

        Assert.InRange(step.Planar.X, 0f, (Caps.LateralMetres / 10f) + 1e-6f);
    }

    /// <summary>Repeated same-phase replans never replenish: a chain of truncated windows of one physical phase
    /// spends at most the original allowance in total, across any number of plan boundaries.</summary>
    [Fact]
    public void RepeatedSamePhaseReplans_CannotExceedTheOriginalAllowance()
    {
        SupportWindow physical = Window(SupportPhase.RightSwing, ticks: 10);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(physical, Caps)!;
        for (int replan = 0; replan < 5; replan++)
        {
            SupportWindow truncated = Window(SupportPhase.RightSwing, ticks: 2);
            for (int tick = 0; tick < truncated.TickCount; tick++)
            {
                SwingCorrection step = _allocator.Step(
                    new SwingCorrectionResidual(new Vector2(1f, 0f), 0f), truncated, tick, [], Vector2.Zero, Vector2.Zero,
                    SupportPhase.RightSwing, budget);
                budget = budget.Charge(step);
            }
        }

        Assert.InRange(budget.CumulativeRight, 0f, Caps.LateralMetres + 1e-6);
        Assert.InRange(budget.SpentRight, 0f, Caps.LateralMetres + 1e-6);
        Assert.Equal(Caps.LateralMetres, budget.SpentRight + budget.RemainingRight, 6);
    }

    /// <summary>A genuine phase change grants exactly one fresh allowance of the new phase; the old phase's
    /// consumption does not leak into it.</summary>
    [Fact]
    public void GenuinePhaseChange_GrantsOneFreshAllowance()
    {
        SupportWindow right = Window(SupportPhase.RightSwing, ticks: 10);
        SwingCorrectionPhaseBudget spent = SpendPhysicalPhase(right, ticks: 5, demand: 0.05f);
        Assert.True(spent.SpentRight > 0d);

        var next = SwingCorrectionPhaseBudget.ForWindow(Window(SupportPhase.LeftSwing, ticks: 10), Caps);
        Assert.NotNull(next);
        Assert.Equal(0d, next!.SpentLeft);
        Assert.Equal(0d, next.SpentRight);
        Assert.Equal(Caps.LateralMetres, next.RemainingRight);
    }

    /// <summary>Reversing a correction direction does not refund the consumed allowance of the other side.</summary>
    [Fact]
    public void DirectionReversal_DoesNotRefundConsumedAllowance()
    {
        SupportWindow physical = Window(SupportPhase.RightSwing, ticks: 10);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(physical, Caps)!;
        budget = budget.Charge(new SwingCorrection(new Vector2(0.03f, 0f), 0f, SwingCorrectionPivot.Origin, Vector2.Zero));
        budget = budget.Charge(new SwingCorrection(new Vector2(-0.02f, 0f), 0f, SwingCorrectionPivot.Origin, Vector2.Zero));

        Assert.Equal(0.01d, budget.CumulativeRight, 6);
        // Incremental, per-direction consumption: the rightward increments are spent once (no refund from the
        // reversal), and the leftward increment consumes the LEFT allowance — not a free return over old ground.
        Assert.Equal(0.03d, budget.SpentRight, 6);
        Assert.Equal(0.02d, budget.SpentLeft, 6);
        Assert.Equal(Caps.LateralMetres - 0.03d, budget.RemainingRight, 6);
        Assert.Equal(Caps.LateralMetres - 0.02d, budget.RemainingLeft, 6);
    }

    /// <summary>Ineligible phases and live-phase mismatches never establish, spend or enlarge a budget.</summary>
    [Fact]
    public void DoubleSupportAndMismatches_NeverCorrectOrCharge()
    {
        Assert.Null(SwingCorrectionPhaseBudget.ForWindow(Window(SupportPhase.BothPlanted, ticks: 10), Caps));

        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(Window(SupportPhase.RightSwing, ticks: 10), Caps)!;
        Assert.Equal(default, _allocator.Step(
            new SwingCorrectionResidual(Vector2.One, 1f), Window(SupportPhase.RightSwing, ticks: 10), 0, [],
            Vector2.Zero, Vector2.Zero, SupportPhase.LeftSwing, budget));
        Assert.Equal(default, _allocator.Step(
            new SwingCorrectionResidual(Vector2.One, 1f), Window(SupportPhase.RightSwing, ticks: 10), 0, [],
            Vector2.Zero, Vector2.Zero, SupportPhase.RightSwing, budget, SwingCorrectionState.Idle));
        Assert.Equal(0d, budget.SpentRight);
    }

    /// <summary>A carried budget is an immutable value: simulating a candidate does not mutate the seed another
    /// candidate or the runtime holds.</summary>
    [Fact]
    public void CarriedBudgetIsImmutableUnderSimulation()
    {
        SupportWindow physical = Window(SupportPhase.RightSwing, ticks: 10);
        SwingCorrectionPhaseBudget seed = SpendPhysicalPhase(physical, ticks: 7, demand: 0.08f);
        SequencePrediction truncated = Standing(Window(SupportPhase.RightSwing, ticks: 3),
            new SequencePose(new Vector2((float)seed.CumulativeRight, 0f), 0f));

        TerminalCorrectionOutcome first = TerminalCorrectionSchedule.Simulate(truncated, new ArrivalTarget(new Vector2(0.08f, 0f), 0f), _allocator, seed);
        TerminalCorrectionOutcome second = TerminalCorrectionSchedule.Simulate(truncated, new ArrivalTarget(new Vector2(0.08f, 0f), 0f), _allocator, seed);

        Assert.Equal(first, second);
        Assert.InRange(seed.CumulativeRight, 0.034999, 0.035001);
        Assert.Equal(seed, SpendPhysicalPhase(physical, ticks: 7, demand: 0.08f));
    }

    /// <summary>Yaw limited by the phase's remaining allowance recomputes its pivot-origin shift for the bounded
    /// yaw: the compensation stays one consistent operation, never a clamped yaw with the old shift.</summary>
    [Fact]
    public void YawLimitedByRemainingAllowance_RecomputesItsPivotShift()
    {
        SupportWindow physical = Window(SupportPhase.RightSwing, ticks: 10);
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(physical, Caps)!
            .Charge(new SwingCorrection(Vector2.Zero, Caps.YawRadians * 0.9f, SwingCorrectionPivot.Ball, Vector2.Zero));
        Vector2 ball = new(0.12f, 0.05f);

        SwingCorrection step = _allocator.Step(
            new SwingCorrectionResidual(Vector2.Zero, 1f), Window(SupportPhase.RightSwing, ticks: 1), 0, [], ball, Vector2.Zero,
            SupportPhase.RightSwing, budget);

        Assert.InRange(step.Yaw, 0f, (Caps.YawRadians * 0.1f) + 1e-7f);
        Assert.Equal(ball - ball.Rotated(step.Yaw), step.OriginShift);
        Assert.Equal(SwingCorrectionPivot.Ball, step.Pivot);
    }

    /// <summary>A flight phase consumes its original travel-derived budget: a truncated re-prediction of the same
    /// flight cannot re-derive or re-grant it.</summary>
    [Fact]
    public void TruncatedFlightWindow_DoesNotRegrantTravelDerivedBudget()
    {
        SupportWindow flight = new(SupportPhase.BothSwing, 0, 9, new SequencePose(Vector2.Zero, 0f),
            new SequencePose(new Vector2(0.4f, 0f), 0f));
        float budgetMetres = 0.4f * Caps.FlightTravelShare;
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(flight, Caps)!;
        Assert.Equal(budgetMetres, budget.FlightBudgetMetres, 6);
        for (int tick = 0; tick < 7; tick++)
        {
            budget = budget.Charge(_allocator.Step(
                new SwingCorrectionResidual(new Vector2(1f, 0f), 0f), flight, tick, [], Vector2.Zero, Vector2.Zero,
                SupportPhase.BothSwing, budget));
        }

        Assert.Equal(budgetMetres * 0.7, budget.FlightSpent, 4);
        SupportWindow truncated = new(SupportPhase.BothSwing, 0, 2, new SequencePose(Vector2.Zero, 0f),
            new SequencePose(new Vector2(0.4f, 0f), 0f));
        for (int tick = 0; tick < truncated.TickCount; tick++)
        {
            budget = budget.Charge(_allocator.Step(
                new SwingCorrectionResidual(new Vector2(1f, 0f), 0f), truncated, tick, [], Vector2.Zero, Vector2.Zero,
                SupportPhase.BothSwing, budget));
        }

        Assert.InRange(budget.FlightSpent, (budgetMetres * 0.7) - 1e-4, budgetMetres + 1e-6);
        Assert.Equal(budgetMetres, budget.FlightSpent + budget.RemainingFlight, 4);
    }

    /// <summary>Spends the first <paramref name="ticks"/> ticks of a physical phase through the production
    /// allocation and charging path (the same pieces <c>NPCNavigation</c>'s movement boundary uses), driven by a
    /// constant lateral demand.</summary>
    private SwingCorrectionPhaseBudget SpendPhysicalPhase(SupportWindow physical, int ticks, float demand)
    {
        SwingCorrectionPhaseBudget budget = SwingCorrectionPhaseBudget.ForWindow(physical, Caps)!;
        for (int tick = 0; tick < ticks; tick++)
        {
            budget = budget.Charge(_allocator.Step(
                new SwingCorrectionResidual(new Vector2(demand - (float)budget.CumulativeRight, 0f), 0f),
                physical, tick, [], Vector2.Zero, Vector2.Zero, physical.Phase, budget));
        }

        return budget;
    }

    private static SupportWindow Window(SupportPhase phase, int ticks, int first = 0) =>
        new(phase, first, first + ticks - 1, new SequencePose(Vector2.Zero, 0f), new SequencePose(Vector2.Zero, 0f));

    private static SequencePrediction Standing(SupportWindow window, SequencePose seed)
    {
        List<PredictedTick> ticks = [];
        for (int index = 0; index < window.TickCount; index++)
        {
            ticks.Add(new PredictedTick(
                index,
                -1,
                false,
                new PredictedClipSample("wf", index, 1d),
                null,
                Vector2.Zero,
                0f,
                seed,
                new PlantedFeetState(true, false),
                Vector3.Zero,
                Vector3.Zero));
        }

        return new SequencePrediction(seed, [.. ticks], [window], []);
    }
}
