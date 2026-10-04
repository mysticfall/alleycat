using System.Globalization;
using System.Text;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// CORNER-08 asset-bound schedule-prediction regression: the terminal plan a <c>travellingOnly</c> request selects
/// from the recorded lateral seed must be one whose exact prediction closes under the legal capped swing schedule,
/// and a non-closable continuation must not suppress the complete final-approach fallback merely by existing. The
/// seed is the exact recorded terminal boundary of the corner scenario's failed containment attempt 2 (tick 779 of
/// its saved trace): mid-<c>NPCWalkF</c> at the west corner exit, the destination <c>(0.45, 5.4)</c> almost fully
/// lateral to the actor's right, facing <c>+Z</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is NOT independent arrival evidence: the plan is requested through the normal production planner selection
/// and materialisation on the real rig binding (<see cref="ArrivalPlanner.Plan(ArrivalStart, ArrivalTarget, bool)"/>
/// with the production-plumbed arrival tolerances — the binding's defaults equal <c>NPCNavigation</c>'s exported
/// 10 mm / 1°), but the completion criterion is the PRODUCTION schedule model
/// (<see cref="TerminalCorrectionSchedule.Simulate(SequencePrediction, ArrivalTarget, SwingCorrectionAllocator, SwingCorrectionPhaseBudget)"/>) replaying the selected plan's exact per-tick prediction —
/// the same capped schedule the planner's admission gate itself applies — never engine playback. It proves
/// selection-level behaviour on the real asset binding (which family wins, and that the winner's predicted capped
/// schedule closes); engine-observed completion of this seed is established separately by the recorded-start
/// native-owner evidence under
/// <c>temp/nav001-corner-terminal-admissibility-review/run-01/native-seed-02</c> and its reruns. Physical
/// phase-budget legality across plan boundaries (CORNER-03) is not claimed here.
/// </para>
/// <para>
/// Against the unchanged pre-CORNER-08 planner this seed selected the recorded <c>ContinueTravel</c> continuation
/// whose raw end misses by 874.436 mm and whose engine-observed capped execution ended 487.601 mm short (Failed),
/// so the assertion was red before the admissibility correction. The full comparison (both the
/// <c>travellingOnly</c> terminal request and the complete-approach full-mode request from the same seed, with raw
/// and simulated ends) is written to the evidence directory before any assertion, so a red run still captures the
/// whole dataset. No recipe, ranking or budget is forced; the target and facing are unchanged from the recording.
/// </para>
/// </remarks>
public sealed class NAV001TerminalContinuationAdmissibilityIntegrationTests
{
    private static readonly string _evidenceDirectory = EvidenceDestinations.Resolve(
        ProjectSettings.GlobalizePath(DetermineIntendedEvidenceDirectory()));

    // Arrival tolerances of the production navigation component (NPCNavigation defaults in the navigation test scene).
    private const float PositionToleranceMetres = 0.01f;
    private static readonly float _yawToleranceRadians = Mathf.DegToRad(1f);

    // The recorded pre-movement terminal start (post-tick 778 of the saved attempt-2 trace): position
    // (-0.5142465, 5.2999997), yaw 180 deg, NPCWalkF at 0.316666666666668 s, no running crossfade, not a prompt.
    private static readonly SequencePose _seedPose = new(new Vector2(-0.5142465f, 5.2999997f), Mathf.Pi);
    private const double SeedWalkSeconds = 0.316666666666668d;

    // The requested final destination and facing: (0.45, 5.4) facing +Z (the corner scenario's replacement target).
    private static readonly ArrivalTarget _target = new(new Vector2(0.45f, 5.4f), -Mathf.Pi);

    /// <inheritdoc/>
    [Fact]
    public async Task TerminalContinuation_FromRecordedLateralSeed_SelectsAPlanWhosePredictedCappedScheduleCloses()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
        await fixture.StepAsync(NAV001ArrivalOutcomeIntegrationTests.WarmUpPhysicsTicks);
        NAV001ArrivalPlannerIntegrationTests.Binding binding = NAV001ArrivalPlannerIntegrationTests.Bind(fixture);

        string walkRole = WalkRoleOf(binding);
        var start = new ArrivalStart(_seedPose, walkRole, SeedWalkSeconds) { Prompt = false };
        var allocator = new SwingCorrectionAllocator(new SwingCorrectionCaps());

        var report = new StringBuilder();
        _ = report.AppendLine("CORNER-08 asset-bound schedule prediction (recorded seed, normal selection; oracle is the production simulator, not engine arrival)");
        _ = report.AppendLine($"start: pose {_seedPose.Position.X:R},{_seedPose.Position.Y:R} yaw {_seedPose.Yaw:R}");
        _ = report.AppendLine($"       {walkRole} @ {SeedWalkSeconds:R}s, no fade, prompt=false");
        _ = report.AppendLine($"target: {_target.Position.X:R},{_target.Position.Y:R} yaw {_target.Yaw:R}");
        _ = report.AppendLine($"tolerances: {PositionToleranceMetres:R} m / {_yawToleranceRadians:R} rad");

        ArrivalPlan terminal = binding.Planner.Plan(start, _target, travellingOnly: true);
        TerminalCorrectionOutcome terminalOutcome = ReportPlan(report, "terminal travellingOnly=true", binding, terminal, allocator);
        ArrivalPlan full = binding.Planner.Plan(start, _target, travellingOnly: false);
        TerminalCorrectionOutcome fullOutcome = ReportPlan(report, "full mode travellingOnly=false", binding, full, allocator);

        WriteReport(report.ToString());

        Assert.True(terminal.Status == ArrivalPlanStatus.Planned,
            $"The terminal travellingOnly plan failed to plan ({terminal.Failure}): {terminal.FailureDetail}.");
        Assert.True(terminalOutcome.ClosesWithin(PositionToleranceMetres, _yawToleranceRadians),
            FormattableString.Invariant(
                $"The normally selected terminal plan's predicted capped schedule does not close: simulated end {terminalOutcome.EndPose.Position.X:R},{terminalOutcome.EndPose.Position.Y:R} yaw {terminalOutcome.EndPose.Yaw:R}, error {terminalOutcome.PositionErrorMetres * 1000:0.###} mm / {Mathf.RadToDeg(terminalOutcome.YawErrorRadians):0.###} deg (production-simulator prediction; the recorded engine outcome of this seed's pre-fix continuation was Failed at 487.601 mm)."));
    }

    /// <summary>Reports one planned request's selection and its raw and capped-corrected ends; never asserts.</summary>
    private static TerminalCorrectionOutcome ReportPlan(
        StringBuilder report,
        string name,
        NAV001ArrivalPlannerIntegrationTests.Binding binding,
        ArrivalPlan plan,
        SwingCorrectionAllocator allocator)
    {
        if (plan.Status != ArrivalPlanStatus.Planned)
        {
            _ = report.AppendLine($"{name}: FAILED ({plan.Failure}): {plan.FailureDetail}");
            return default;
        }

        SequencePrediction prediction = binding.Planner.Predict(plan);
        TerminalCorrectionOutcome outcome = TerminalCorrectionSchedule.Simulate(prediction, plan.Target, allocator);
        _ = report.AppendLine(CultureInfo.InvariantCulture, $"{name}: {plan.Kind}, {plan.TickCount} ticks, segments:");
        foreach (ArrivalPlanSegment segment in plan.Segments)
        {
            _ = report.AppendLine(
                $"  {segment.Role}@{segment.EntrySeconds:0.######}->{segment.ExitSeconds:0.######}+{segment.ExitCycles} ({segment.Stage})");
        }

        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"{name}: raw end {plan.PredictedEnd.Position.X:R},{plan.PredictedEnd.Position.Y:R} yaw {plan.PredictedEnd.Yaw:R}"
            + $" residual {plan.PlanarResidual.Length() * 1000:0.###} mm / {Mathf.RadToDeg(plan.YawResidual):0.###} deg");
        _ = report.AppendLine(CultureInfo.InvariantCulture,
            $"{name}: capped end {outcome.EndPose.Position.X:R},{outcome.EndPose.Position.Y:R} yaw {outcome.EndPose.Yaw:R}"
            + $" error {outcome.PositionErrorMetres * 1000:0.###} mm / {Mathf.RadToDeg(outcome.YawErrorRadians):0.###} deg"
            + $" closes={outcome.ClosesWithin(PositionToleranceMetres, _yawToleranceRadians)}");
        _ = report.AppendLine(
            $"{name}: windows {string.Join(", ", plan.Windows.Select(window => $"{window.Phase}#{window.FirstTick}-{window.LastTick}"))}");
        return outcome;
    }

    private static string WalkRoleOf(NAV001ArrivalPlannerIntegrationTests.Binding binding) =>
        binding.StateOf.FirstOrDefault(pair => pair.Value == NPCAnimationProgramme.WalkForwardState).Key
            ?? throw new InvalidOperationException("SETUP: the binding has no forward-walk role.");

    private static void WriteReport(string report)
    {
        string directory = _evidenceDirectory;
        File.WriteAllText(Path.Combine(directory, "terminal-seed-report.txt"), report);
    }

    private static string DetermineIntendedEvidenceDirectory()
    {
        string configured = OS.GetEnvironment("ALLEYCAT_NAV001_TERMINAL_EVIDENCE");
        return string.IsNullOrWhiteSpace(configured)
            ? "res://temp/nav001-corner-terminal-admissibility/run-01/baseline"
            : $"res://temp/{configured.TrimStart('/')}";
    }
}
