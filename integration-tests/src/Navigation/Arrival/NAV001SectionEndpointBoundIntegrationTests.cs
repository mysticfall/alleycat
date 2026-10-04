using System.Globalization;
using System.Text;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;
/// <summary>
/// CORNER-02 regression on the INSTALLED female rig: replays the exact starts and stations the independent review
/// recorded for the adopted route sections whose exact predicted endpoints escaped the declared 0.3 m endpoint
/// window (section 3: exact miss 1.228425931 m; section 4, with an in-flight crossfade: 1.246090815 m), plus the
/// adjacent in-bound section 5 (active fade, recorded miss 0.007841848 m) as the valid boundary-adjacent case.
/// Starts, fade progress, stations and prompt semantics come from the recorded review_plan rows; the planner binding
/// is the production one (bound trajectory table, package intervals, seam table, configured tolerance).
/// </summary>
public sealed class NAV001SectionEndpointBoundIntegrationTests
{
    private const float DeclaredEndpointWindowMetres = 0.3f;

    // Evidence root is env-selected so no prior evidence tree can be overwritten; the default is this task's
    // fresh baseline directory.
    private static readonly string _evidenceDirectory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
        OS.GetEnvironment("ALLEYCAT_NAV001_SECTION_EVIDENCE") is { Length: > 0 } path
            ? $"res://temp/{path.TrimStart('/')}"
            : "res://temp/nav001-corner-exact-sections/run-01/baseline/integration"));

    /// <inheritdoc/>
    [Fact]
    public async Task RecordedSections_EndExactlyWithinTheDeclaredEndpointWindow()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
        NAV001ArrivalPlannerIntegrationTests.Binding binding = NAV001ArrivalPlannerIntegrationTests.Bind(fixture);

        var failures = new List<string>();
        var report = new StringBuilder();
        _ = report.AppendLine("section,station_x,station_z,station_yaw,status,exact_end_x,exact_end_z,exact_miss_mm,segments");
        SectionCase(
            binding,
            report,
            failures,
            "review-section-3",
            startPose: new SequencePose(new Vector2(-0.6074435f, 2.5667233f), 2.978611f),
            startRole: "wf",
            startPosition: 1.0833333730697632d,
            startFading: null,
            station: new ArrivalTarget(new Vector2(-0.45000002f, 4.2f), -3.1415927f));
        SectionCase(
            binding,
            report,
            failures,
            "review-section-4-active-fade",
            startPose: new SequencePose(new Vector2(-0.5388187f, 3.043435f), 3.1415925f),
            startRole: "wf",
            startPosition: 0.4333333373069763d,
            startFading: new SequenceCrossfadeProgress("al_l", 0.9166666865348816d, 0.05f, 0.15f),
            station: new ArrivalTarget(new Vector2(-0.45000002f, 4.659259f), -3.1415927f));
        SectionCase(
            binding,
            report,
            failures,
            "review-section-5-active-fade-inbound",
            startPose: new SequencePose(new Vector2(-0.47555557f, 3.4697473f), -3.1415925f),
            startRole: "wf",
            startPosition: 0.9666666388511658d,
            startFading: new SequenceCrossfadeProgress("al_r", 0.9833333492279053d, 0.083333336f, 0.15f),
            station: new ArrivalTarget(new Vector2(-0.70000005f, 4.3999996f), -3.1415927f));

        string directory = _evidenceDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "section-endpoints.csv"), report.ToString());
        Assert.True(failures.Count == 0, $"{failures.Count} section endpoint failure(s):\n{string.Join("\n", failures)}\n{report}");
    }

    /// <summary>Replays one recorded section start against the production planner binding and reports its exact endpoint.</summary>
    private static void SectionCase(
        NAV001ArrivalPlannerIntegrationTests.Binding binding,
        StringBuilder report,
        List<string> failures,
        string name,
        SequencePose startPose,
        string startRole,
        double startPosition,
        SequenceCrossfadeProgress? startFading,
        ArrivalTarget station)
    {
        var start = new ArrivalStart(startPose, startRole, startPosition, startFading);
        ArrivalPlan plan = binding.Planner.PlanSection(start, station, promptRedirect: false);
        float exactMiss = plan.Status == ArrivalPlanStatus.Planned
            ? plan.PredictedEnd.Position.DistanceTo(station.Position)
            : float.NaN;
        string segments = string.Join(
            ";",
            plan.Segments.Select(segment => string.Create(
                CultureInfo.InvariantCulture,
                $"{segment.Role}@{segment.EntrySeconds:0.######}->{segment.ExitSeconds:0.######}+{segment.ExitCycles}")));
        _ = report.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name},{station.Position.X:R},{station.Position.Y:R},{station.Yaw:R},{plan.Status},{(plan.Status == ArrivalPlanStatus.Planned ? plan.PredictedEnd.Position.X.ToString("R", CultureInfo.InvariantCulture) : "n/a")},{(plan.Status == ArrivalPlanStatus.Planned ? plan.PredictedEnd.Position.Y.ToString("R", CultureInfo.InvariantCulture) : "n/a")},{(float.IsNaN(exactMiss) ? "n/a" : (exactMiss * 1000f).ToString("R", CultureInfo.InvariantCulture))},{segments}"));
        if (plan.Status != ArrivalPlanStatus.Planned)
        {
            failures.Add($"{name}: the recorded start no longer plans ({plan.Failure}: {plan.FailureDetail}).");
        }
        else if (exactMiss > DeclaredEndpointWindowMetres)
        {
            failures.Add(FormattableString.Invariant(
                $"{name}: exact predicted end {plan.PredictedEnd.Position.X:0.######},{plan.PredictedEnd.Position.Y:0.######} misses station {station.Position.X:0.######},{station.Position.Y:0.######} by {exactMiss * 1000f:0.##} mm > {DeclaredEndpointWindowMetres * 1000f:0} mm ({segments})."));
        }

    }
}
