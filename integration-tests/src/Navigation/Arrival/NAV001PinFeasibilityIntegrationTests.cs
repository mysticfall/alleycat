using System.Globalization;
using System.Text;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.Arrival.NAV001ArrivalPlannerIntegrationTests;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// Integrity of the frozen allocator recording fixtures and exact repeatability of identical-request planning
/// ledgers. The pre-IK pin-feasibility population measurement itself is emitted by
/// <see cref="NAV001CorrectedArrivalIntegrationTests"/>, whose replays cover the identical population.
/// </summary>
public sealed class NAV001PinFeasibilityIntegrationTests
{
    /// <summary>Verifies the frozen allocator recording fixtures remain internally well-formed.</summary>
    [Fact]
    public void Female_FrozenRecordingFixtureIntegrity() => VerifyFrozenRecordingFixtures();

    /// <summary>Verifies identical idle requests reproduce exactly the same complete predicted planning ledger.</summary>
    [Fact]
    public async Task Female_PlanningLedgerRepeatsForIdenticalRequests()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(
            NPCNavigationRig.Female, useReducedRenderSize: true);
        await fixture.StepAsync(WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
            "res://temp/nav001-pin-feasibility/Female-ledger-repeatability"));
        foreach ((string name, (Vector3 Destination, float Yaw)[] legs) in Scenarios().Where(
                     scenario => scenario.Name is "oblique" or "terminal-slide" or "terminal-slide-mirror"))
        {
            await TeleportToIdleAtOriginAsync(fixture, binding);
            var target = new ArrivalTarget(new Vector2(legs[0].Destination.X, legs[0].Destination.Z), legs[0].Yaw);
            ArrivalStart start = IdleStart(fixture, binding);
            ArrivalPlan firstPlan = binding.Planner.Plan(start, target);
            ArrivalPlan secondPlan = binding.Planner.Plan(start, target);
            Assert.Equal(ArrivalPlanStatus.Planned, firstPlan.Status);
            Assert.Equal(ArrivalPlanStatus.Planned, secondPlan.Status);
            SequencePrediction firstLedger = binding.Planner.Predict(firstPlan);
            SequencePrediction secondLedger = binding.Planner.Predict(secondPlan);
            WriteLedger(directory, $"{name}-run-1.csv", firstLedger);
            WriteLedger(directory, $"{name}-run-2.csv", secondLedger);
            string? difference = FirstLedgerDifference(firstLedger, secondLedger);
            Assert.True(difference is null,
                $"{name}: identical idle requests must reproduce the exact same complete planning ledger. First difference: {difference}");
        }
    }

    private const string FrozenRecordingHeader =
        "plan_tick,physics_tick,window,planted_left,planted_right,planar_right_mm,planar_forward_mm,yaw_deg,pivot,"
        + "origin_right_mm,origin_forward_mm,residual_right_mm,residual_forward_mm,residual_yaw_deg,actor_x,actor_z,actor_yaw_deg,idle";

    private static readonly int[] _frozenRecordingNumericColumns = [5, 6, 7, 9, 10, 11, 12, 13, 14, 15, 16];

    private static readonly int[] _frozenRecordingBooleanColumns = [3, 4, 17];

    private static readonly string[] _frozenRecordings =
    [
        "behind-1.5m-0-corrections.csv",
        "distant-lateral-3m-0-corrections.csv",
        "lateral-0.5m-0-corrections.csv",
        "long-straight-4m-0-corrections.csv",
        "oblique-0-corrections.csv",
        "repeated-nearby-0-corrections.csv",
        "repeated-nearby-1-corrections.csv",
        "replan-mid-walk-0-corrections.csv",
        "replan-mid-walk-prompt-0-corrections.csv",
        "terminal-slide-0-corrections.csv",
        "terminal-slide-mirror-0-corrections.csv",
        "turn-only-90deg-0-corrections.csv",
        "user-shuffle-0-corrections.csv",
    ];

    /// <summary>
    /// Verifies the frozen allocator recording fixtures are internally well-formed: known header,
    /// contiguous tick rows, parseable Boolean and finite invariant numeric fields, and a row count that matches the
    /// last recorded tick. The recordings are never compared against current planner output.
    /// </summary>
    private static void VerifyFrozenRecordingFixtures()
    {
        string directory = ProjectSettings.GlobalizePath("res://tests/navigation/pin-recording-fixtures/Female-defaults");
        foreach (string file in _frozenRecordings)
        {
            string path = Path.Combine(directory, file);
            Assert.True(File.Exists(path),
                $"SETUP: frozen recording fixture is missing: {path}. Include all maintained fixture files byte-identically; "
                + "do not regenerate them from current planner output.");
            string[] lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= 2, $"{file}: expected a header and at least one recorded tick.");
            Assert.True(lines[0] == FrozenRecordingHeader,
                $"{file}: unexpected header: '{lines[0]}' (expected '{FrozenRecordingHeader}').");
            for (int row = 1; row < lines.Length; row++)
            {
                string[] fields = lines[row].Split(',');
                Assert.True(fields.Length == 18, $"{file} row {row}: expected 18 columns, found {fields.Length}.");
                Assert.True(int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tick),
                    $"{file} row {row}: plan_tick '{fields[0]}' is not an integer.");
                Assert.True(tick == row - 1, $"{file} row {row}: plan_tick {tick} breaks contiguity from zero.");
                Assert.True(
                    int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int physics) && physics >= 0,
                    $"{file} row {row}: physics_tick '{fields[1]}' is not a non-negative integer.");
                foreach (int column in _frozenRecordingBooleanColumns)
                {
                    Assert.True(bool.TryParse(fields[column], out _),
                        $"{file} row {row}: column {column + 1} '{fields[column]}' is not a Boolean.");
                }
                foreach (int column in _frozenRecordingNumericColumns)
                {
                    Assert.True(
                        float.TryParse(fields[column], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                        && float.IsFinite(value),
                        $"{file} row {row}: column {column + 1} '{fields[column]}' is not a finite number.");
                }
            }

            int lastTick = int.Parse(lines[^1].Split(',')[0], CultureInfo.InvariantCulture);
            Assert.True(lines.Length - 1 == lastTick + 1,
                $"{file}: {lines.Length - 1} data rows do not match the last recorded tick {lastTick} plus one.");
        }
    }

    /// <summary>Returns the first exact difference between two complete predicted planning ledgers, or null when identical.</summary>
    private static string? FirstLedgerDifference(SequencePrediction expected, SequencePrediction actual)
    {
        if (!expected.IsValid)
        {
            return $"first prediction rejected: {expected.Rejection}";
        }

        if (!actual.IsValid)
        {
            return $"second prediction rejected: {actual.Rejection}";
        }

        string? pose = FirstPoseDifference("start pose", expected.StartPose, actual.StartPose);
        if (pose is not null)
        {
            return pose;
        }

        if (expected.Ticks.Length != actual.Ticks.Length)
        {
            return $"tick count {expected.Ticks.Length} vs {actual.Ticks.Length}";
        }

        for (int tick = 0; tick < expected.Ticks.Length; tick++)
        {
            string? difference = FirstTickDifference(expected.Ticks[tick], actual.Ticks[tick]);
            if (difference is not null)
            {
                return $"tick {tick}: {difference}";
            }
        }

        if (expected.Windows.Length != actual.Windows.Length)
        {
            return $"support window count {expected.Windows.Length} vs {actual.Windows.Length}";
        }

        for (int window = 0; window < expected.Windows.Length; window++)
        {
            string? difference = FirstWindowDifference(expected.Windows[window], actual.Windows[window]);
            if (difference is not null)
            {
                return $"support window {window}: {difference}";
            }
        }

        if (expected.EntryTicks.Length != actual.EntryTicks.Length)
        {
            return $"entry tick count {expected.EntryTicks.Length} vs {actual.EntryTicks.Length}";
        }

        for (int entry = 0; entry < expected.EntryTicks.Length; entry++)
        {
            if (expected.EntryTicks[entry] != actual.EntryTicks[entry])
            {
                return $"entry tick {entry}: {expected.EntryTicks[entry]} vs {actual.EntryTicks[entry]}";
            }
        }

        return FirstPoseDifference("end pose", expected.EndPose, actual.EndPose);
    }

    private static string? FirstTickDifference(PredictedTick expected, PredictedTick actual)
    {
        if (expected.Index != actual.Index)
        {
            return $"index {expected.Index} vs {actual.Index}";
        }

        if (expected.Segment != actual.Segment)
        {
            return $"segment {expected.Segment} vs {actual.Segment}";
        }

        if (expected.IsEntryTick != actual.IsEntryTick)
        {
            return $"entry flag {expected.IsEntryTick} vs {actual.IsEntryTick}";
        }

        string? clip = FirstClipDifference("current clip", expected.Current, actual.Current);
        if (clip is not null)
        {
            return clip;
        }

        if ((expected.FadingFrom is null) != (actual.FadingFrom is null))
        {
            return $"fading presence {expected.FadingFrom is not null} vs {actual.FadingFrom is not null}";
        }

        if (expected.FadingFrom is { } fading)
        {
            clip = FirstClipDifference("fading clip", fading, actual.FadingFrom!.Value);
            if (clip is not null)
            {
                return clip;
            }
        }

        return expected.RootDelta != actual.RootDelta
            ? $"root delta {expected.RootDelta} vs {actual.RootDelta}"
            : expected.YawDelta != actual.YawDelta
            ? $"yaw delta {expected.YawDelta:R} vs {actual.YawDelta:R}"
            : FirstPoseDifference($"tick {expected.Index} pose", expected.Pose, actual.Pose)
            ?? (expected.Planted != actual.Planted ? $"planted {expected.Planted} vs {actual.Planted}" : null)
            ?? (expected.LeftBall != actual.LeftBall ? $"left ball {expected.LeftBall} vs {actual.LeftBall}" : null)
            ?? (expected.RightBall != actual.RightBall ? $"right ball {expected.RightBall} vs {actual.RightBall}" : null);
    }

    private static string? FirstClipDifference(string context, PredictedClipSample expected, PredictedClipSample actual)
    {
        return expected.Role != actual.Role
            ? $"{context} role '{expected.Role}' vs '{actual.Role}'"
            : expected.PositionSeconds != actual.PositionSeconds
                ? $"{context} '{expected.Role}' position {expected.PositionSeconds:R} vs {actual.PositionSeconds:R}"
                : expected.Weight != actual.Weight
                    ? $"{context} '{expected.Role}' weight {expected.Weight:R} vs {actual.Weight:R}"
                    : null;
    }

    private static string? FirstPoseDifference(string context, SequencePose expected, SequencePose actual)
    {
        return expected.Position != actual.Position
            ? $"{context} position {expected.Position} vs {actual.Position}"
            : expected.Yaw != actual.Yaw ? $"{context} yaw {expected.Yaw:R} vs {actual.Yaw:R}" : null;
    }

    private static string? FirstWindowDifference(SupportWindow expected, SupportWindow actual)
    {
        return expected.Phase != actual.Phase ? $"phase {expected.Phase} vs {actual.Phase}"
            : expected.FirstTick != actual.FirstTick ? $"first tick {expected.FirstTick} vs {actual.FirstTick}"
            : expected.LastTick != actual.LastTick ? $"last tick {expected.LastTick} vs {actual.LastTick}"
            : FirstPoseDifference("start pose", expected.StartPose, actual.StartPose)
            ?? FirstPoseDifference("end pose", expected.EndPose, actual.EndPose);
    }

    /// <summary>Writes one complete predicted ledger — ticks, support windows, entry ticks, and boundary poses.</summary>
    private static void WriteLedger(string directory, string file, SequencePrediction ledger)
    {
        var rows = new StringBuilder(
            "tick,segment,is_entry,current_role,current_position_s,current_weight,fading_role,fading_position_s,fading_weight,"
            + "root_delta_right,root_delta_forward,yaw_delta,pose_x,pose_z,pose_yaw,planted_left,planted_right,"
            + "left_ball_x,left_ball_y,left_ball_z,right_ball_x,right_ball_y,right_ball_z\n");
        foreach (PredictedTick tick in ledger.Ticks)
        {
            _ = rows.AppendLine(FormattableString.Invariant(
                $"{tick.Index},{tick.Segment},{tick.IsEntryTick},{tick.Current.Role},{tick.Current.PositionSeconds:R},{tick.Current.Weight:R},{tick.FadingFrom?.Role ?? ""},{tick.FadingFrom?.PositionSeconds:R},{tick.FadingFrom?.Weight:R},{tick.RootDelta.X:R},{tick.RootDelta.Y:R},{tick.YawDelta:R},{tick.Pose.Position.X:R},{tick.Pose.Position.Y:R},{tick.Pose.Yaw:R},{tick.Planted.Left},{tick.Planted.Right},{tick.LeftBall.X:R},{tick.LeftBall.Y:R},{tick.LeftBall.Z:R},{tick.RightBall.X:R},{tick.RightBall.Y:R},{tick.RightBall.Z:R}"));
        }

        foreach (SupportWindow window in ledger.Windows)
        {
            _ = rows.AppendLine(FormattableString.Invariant(
                $"window,{window.Phase},{window.FirstTick},{window.LastTick},{window.StartPose.Position.X:R},{window.StartPose.Position.Y:R},{window.StartPose.Yaw:R},{window.EndPose.Position.X:R},{window.EndPose.Position.Y:R},{window.EndPose.Yaw:R}"));
        }

        foreach (int entry in ledger.EntryTicks)
        {
            _ = rows.AppendLine(FormattableString.Invariant($"entry,{entry}"));
        }

        _ = rows.AppendLine(FormattableString.Invariant(
            $"pose,start,{ledger.StartPose.Position.X:R},{ledger.StartPose.Position.Y:R},{ledger.StartPose.Yaw:R}"));
        _ = rows.AppendLine(FormattableString.Invariant(
            $"pose,end,{ledger.EndPose.Position.X:R},{ledger.EndPose.Position.Y:R},{ledger.EndPose.Yaw:R}"));
        File.WriteAllText(Path.Combine(directory, file), rows.ToString());
    }
}
