using System.Globalization;
using System.Text;
using System.Text.Json;
using AlleyCat.Control.Locomotion;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Arrival;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Navigation.Arrival.NAV001ArrivalPlannerIntegrationTests;
using static AlleyCat.IntegrationTests.Navigation.NAV001ArrivalOutcomeIntegrationTests;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Navigation.Arrival;

/// <summary>
/// Read-only pre-IK feasibility measurement of the default corrected arrival population, plus integrity of the
/// frozen allocator recording fixtures and exact repeatability of identical-request planning ledgers.
/// </summary>
public sealed class NAV001PinFeasibilityIntegrationTests
{
    /// <summary>Measures the female default population.</summary>
    [Fact]
    public Task Female() => MeasurePopulation(NPCNavigationRig.Female);

    /// <summary>Measures the male default population.</summary>
    [Fact]
    public Task Male() => MeasurePopulation(NPCNavigationRig.Male);

    /// <summary>Preserves the complete female long-straight pin oracle at both real window sizes.</summary>
    [Fact]
    public Task Female_ReducedWindowPreservesPinMeasurements() => VerifyRenderSizeAsync(NPCNavigationRig.Female);

    /// <summary>Preserves the complete male long-straight pin oracle at both real window sizes.</summary>
    [Fact]
    public Task Male_ReducedWindowPreservesPinMeasurements() => VerifyRenderSizeAsync(NPCNavigationRig.Male);

    private static async Task VerifyRenderSizeAsync(NPCNavigationRig rig)
    {
        SceneTree tree = GetSceneTree();
        Vector2I previousSize = tree.Root.Size;
        int previousSteps = Engine.MaxPhysicsStepsPerFrame;
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
            $"res://temp/nav001-pin-feasibility/{rig}-render-size-equivalence"));
        var captures = new PinCapture[2];
        foreach (bool reduced in new[] { false, true })
        {
            try
            {
                await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig, useReducedRenderSize: reduced);
                await fixture.StepAsync(WarmUpPhysicsTicks);
                Binding binding = Bind(fixture);
                await TeleportToIdleAtOriginAsync(fixture, binding);
                ArrivalPlan plan = binding.Planner.Plan(IdleStart(fixture, binding), new ArrivalTarget(new Vector2(0, -4), 0));
                SequencePrediction prediction = binding.Planner.Predict(plan);
                string name = reduced ? "reduced" : "default";
                WriteLedger(directory, name + "-ledger.csv", prediction);
                Assert.Equal(60, Engine.PhysicsTicksPerSecond);
                Assert.Equal(1, Engine.MaxPhysicsStepsPerFrame);
                using var capture = new PinCapture(fixture, prediction.Ticks.Length + 60);
                captures[reduced ? 1 : 0] = capture;
                await Measure(fixture, binding, plan, directory, name, capture);
                capture.VerifyAndSave(directory, name);
            }
            finally
            {
                Assert.Equal(previousSize, tree.Root.Size);
                Assert.Equal(previousSteps, Engine.MaxPhysicsStepsPerFrame);
            }
        }

        string[] baseline = File.ReadAllLines(Path.Combine(directory, "default.csv"));
        string[] candidate = File.ReadAllLines(Path.Combine(directory, "reduced.csv"));
        Assert.Equal(baseline.Length, candidate.Length);
        Assert.Equal(baseline[0], candidate[0]);
        ulong first = ulong.Parse(baseline[1].Split(',')[1], CultureInfo.InvariantCulture);
        ulong second = ulong.Parse(candidate[1].Split(',')[1], CultureInfo.InvariantCulture);
        for (int row = 1; row < baseline.Length; row++)
        {
            string[] full = baseline[row].Split(',');
            string[] reduced = candidate[row].Split(',');
            Assert.Equal(first + (ulong)((row - 1) / 2), ulong.Parse(full[1], CultureInfo.InvariantCulture));
            Assert.Equal(second + (ulong)((row - 1) / 2), ulong.Parse(reduced[1], CultureInfo.InvariantCulture));
            // Compare relative native IDs only; preserve both complete raw foot streams, including Idle NaNs.
            full[1] = reduced[1] = ((row - 1) / 2).ToString(CultureInfo.InvariantCulture);
            Assert.True(full.SequenceEqual(reduced), $"{rig} pin size mismatch at row {row}: {baseline[row]} != {candidate[row]}");
        }

        Assert.Equal(File.ReadAllText(Path.Combine(directory, "default-ledger.csv")),
            File.ReadAllText(Path.Combine(directory, "reduced-ledger.csv")));
        captures[0].AssertEquivalent(captures[1]);
    }

    /// <summary>Verifies the frozen allocator recording fixtures remain internally well-formed.</summary>
    [Fact]
    public void Female_FrozenRecordingFixtureIntegrity() => VerifyFrozenRecordingFixtures();

    /// <summary>Verifies identical idle requests reproduce exactly the same complete predicted planning ledger.</summary>
    [Fact]
    public async Task Female_PlanningLedgerRepeatsForIdenticalRequests()
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(NPCNavigationRig.Female);
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

    private static async Task MeasurePopulation(NPCNavigationRig rig)
    {
        await using NPCNavigationRigFixture fixture = await NPCNavigationRigFixture.CreateAsync(rig, useReducedRenderSize: true);
        await fixture.StepAsync(WarmUpPhysicsTicks);
        Binding binding = Bind(fixture);
        string directory = EvidenceDestinations.Resolve(ProjectSettings.GlobalizePath(
            $"res://temp/nav001-pin-feasibility/{rig}"));
        foreach ((string name, (Vector3 Destination, float Yaw)[] legs) in Scenarios())
        {
            await TeleportToIdleAtOriginAsync(fixture, binding);
            for (int leg = 0; leg < legs.Length; leg++)
            {
                ArrivalPlan plan = binding.Planner.Plan(IdleStart(fixture, binding),
                    new ArrivalTarget(new Vector2(legs[leg].Destination.X, legs[leg].Destination.Z), legs[leg].Yaw));
                await Measure(fixture, binding, plan, directory, $"{name}-{leg}");
            }
        }

        foreach (bool prompt in new[] { false, true })
        {
            await TeleportToIdleAtOriginAsync(fixture, binding);
            ArrivalPlan first = binding.Planner.Plan(IdleStart(fixture, binding), new ArrivalTarget(new Vector2(0, -4), 0));
            SequencePrediction prediction = binding.Planner.Predict(first);
            string walk = binding.Catalogue[StandingLocomotionCapability.CyclicForwardPrimary].Role;
            int count = 0;
            int cut = -1;
            for (int tick = 0; tick < prediction.Ticks.Length; tick++)
            {
                PredictedTick at = prediction.Ticks[tick];
                count = at.Current.Role == walk && at.FadingFrom is null ? count + 1 : 0;
                if (count == 25)
                {
                    cut = tick;
                    break;
                }
            }

            Assert.True(cut >= 0);
            _ = await RunAsync(fixture, binding, first, prediction, cut + 1);
            SequencePose pose = PoseOf(fixture.Actor);
            var start = new ArrivalStart(pose, walk, prediction.Ticks[cut].Current.PositionSeconds) { Prompt = prompt };
            SequencePose goal = new PlanarMotion(0.6, 2, Mathf.DegToRad(-20)).From(pose);
            ArrivalPlan plan = binding.Planner.Plan(start, new ArrivalTarget(goal.Position, goal.Yaw));
            await Measure(fixture, binding, plan, directory, prompt ? "replan-mid-walk-prompt-0" : "replan-mid-walk-0");
        }
    }

    private static Transform3D Transform(SequencePose pose) =>
        new(new Basis(Vector3.Up, pose.Yaw), new Vector3(pose.Position.X, 0, pose.Position.Y));

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

    private static async Task Measure(NPCNavigationRigFixture fixture, Binding binding, ArrivalPlan plan, string directory, string name,
        PinCapture? capture = null)
    {
        Assert.Equal(ArrivalPlanStatus.Planned, plan.Status);
        SequencePrediction prediction = binding.Planner.Predict(plan);
        var allocator = new SwingCorrectionAllocator(new SwingCorrectionCaps());
        var actor = (CharacterBody3D)fixture.Actor;
        Node3D reference = fixture.Navigation.RootMotionReference!;
        AnimationNodeStateMachinePlayback playback = fixture.Tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>();
        AnimationGraphPlantedStateSource source = fixture.Actor.FindChildren("*", "", true, false)
            .OfType<AnimationGraphPlantedStateSource>().Single();
        FootPlantingTargetProvider[] providers = [.. fixture.Actor.FindChildren("*", "", true, false).OfType<FootPlantingTargetProvider>()];
        var left = new Chain(fixture.Skeleton, "Left", providers.Single(provider => provider.Side.ToString() == "Left"));
        var right = new Chain(fixture.Skeleton, "Right", providers.Single(provider => provider.Side.ToString() == "Right"));
        var rows = new StringBuilder("tick,physics,foot,planted,clip,fading,ball_x,ball_y,ball_z,hip_x,hip_y,hip_z,ankle_x,ankle_y,ankle_z,length,drift_mm,required_reach,ball_reach,post_x,post_y,post_z,post_reach,live_drift_mm,live_required_reach,pin_x,pin_z,release,animated_ball_x,animated_ball_z,preIK_stage_difference_mm,table_drift_mm,table_pose_error_mm\n");
        int tick = 0;
        int windowIndex = 0;
        ulong physics = 0;
        string clip = "";
        string fading = "";
        bool leftPlanted = false;
        bool rightPlanted = false;
        void Move()
        {
            physics = Engine.GetPhysicsFrames();
            bool idle = tick >= prediction.Ticks.Length;
            var frame = new NPCNavigationSemanticFrame(actor.GlobalBasis, reference.GlobalBasis);
            float rootYaw = frame.EvaluatedRootRotationToActorLocal(fixture.Tree.GetRootMotionRotation()).GetEuler().Y;
            rootYaw = Mathf.IsZeroApprox(rootYaw) ? 0 : rootYaw;
            Basis afterRoot = actor.GlobalBasis * new Basis(Vector3.Up, rootYaw);
            Basis afterReference = afterRoot * actor.GlobalBasis.Inverse() * reference.GlobalBasis;
            Vector3 delta = new NPCNavigationSemanticFrame(afterRoot, afterReference).EvaluatedRootDeltaToWorld(fixture.Tree.GetRootMotionPosition());
            delta.Y = 0;
            var actual = new Transform3D(afterRoot, actor.GlobalPosition + delta);
            Assert.True(source.TryGetPlantedState(out PlantedFeetState planted));
            leftPlanted = planted.Left;
            rightPlanted = planted.Right;
            Transform3D actorFromSkeleton = actor.GlobalTransform.AffineInverse() * fixture.Skeleton.GlobalTransform;
            Transform3D predictedSkeleton = Transform(idle ? prediction.EndPose : prediction.Ticks[tick].Pose) * actorFromSkeleton;
            left.Capture(predictedSkeleton, planted.Left);
            right.Capture(predictedSkeleton, planted.Right);
            if (!idle)
            {
                Assert.Equal(prediction.Ticks[tick].Planted, planted);
                left.CaptureTable(Transform(prediction.Ticks[tick].Pose) * prediction.Ticks[tick].LeftBall, planted.Left);
                right.CaptureTable(Transform(prediction.Ticks[tick].Pose) * prediction.Ticks[tick].RightBall, planted.Right);
            }
            else
            {
                left.CaptureTable(null, planted.Left);
                right.CaptureTable(null, planted.Right);
            }
            clip = idle ? "Idle" : prediction.Ticks[tick].Current.Role;
            fading = idle ? "" : prediction.Ticks[tick].FadingFrom?.Role ?? "";
            SwingCorrection correction = default;
            if (!idle)
            {
                while (tick > prediction.Windows[windowIndex].LastTick)
                {
                    windowIndex++;
                }

                Transform3D projected = actual * Transform(prediction.Ticks[tick].Pose).AffineInverse() * Transform(prediction.EndPose);
                Vector3 offset = actual.Basis.Inverse() * (projected.Origin - actual.Origin);
                Vector3 error = actual.Basis.Inverse() * (new Vector3(plan.Target.Position.X, 0, plan.Target.Position.Y) - projected.Origin);
                var residual = new SwingCorrectionResidual(new Vector2(error.X, -error.Z), Mathf.AngleDifference(YawOf(projected), plan.Target.Yaw));
                SupportPhase phase = SupportPhases.Of(planted);
                Vector3 ball = phase == SupportPhase.LeftSwing ? actorFromSkeleton * fixture.Skeleton.GetBoneGlobalPose(right.BallIndex).Origin
                    : phase == SupportPhase.RightSwing ? actorFromSkeleton * fixture.Skeleton.GetBoneGlobalPose(left.BallIndex).Origin : Vector3.Zero;
                correction = allocator.Step(residual, prediction.Windows[windowIndex], tick, prediction.Windows.AsSpan()[(windowIndex + 1)..],
                    new Vector2(ball.X, -ball.Z), new Vector2(offset.X, -offset.Z), phase);
            }

            capture?.RecordCorrection(tick, correction);
            Vector2 synthetic = correction.OriginShift + correction.Planar;
            float correctionYaw = correction.Yaw;

            Basis afterCorrection = afterRoot * new Basis(Vector3.Up, correctionYaw);
            delta += afterCorrection * new Vector3(synthetic.X, 0, -synthetic.Y);
            if (rootYaw + correctionYaw != 0)
            {
                actor.RotateY(rootYaw + correctionYaw);
            }

            if (!delta.IsZeroApprox())
            {
                Assert.Null(actor.MoveAndCollide(delta));
            }

            left.CaptureLive(planted.Left);
            right.CaptureLive(planted.Right);
        }

        using var replay = new PhysicsReplay(fixture, Move);
        var segments = new ImmutableSegmentsList(plan);
        int next = 0;
        RequestIfDue(fixture.Driver, binding, segments, prediction, 0, ref next);
        long initial = fixture.Driver.EvaluationCount;
        for (tick = 0; tick < prediction.Ticks.Length + 60; tick++)
        {
            await replay.StepAsync();
            Assert.Equal(initial + tick + 1, fixture.Driver.EvaluationCount);
            left.Write(rows, tick, physics, leftPlanted, clip, fading);
            right.Write(rows, tick, physics, rightPlanted, clip, fading);
            capture?.RecordSample(physics, fixture.Driver.EvaluationCount);
            if (tick + 1 < prediction.Ticks.Length)
            {
                RequestIfDue(fixture.Driver, binding, segments, prediction, tick + 1, ref next);
            }
        }

        binding.IdlePosition = playback.GetCurrentPlayPosition();
        File.WriteAllText(Path.Combine(directory, name + ".csv"), rows.ToString());
    }

    // Optional, local to the two render-size controls. Population callers retain their original sampling path.
    private sealed class PinCapture : IDisposable
    {
        private readonly NPCNavigationRigFixture _fixture;
        private readonly NativeEvent[] _mixer;
        private readonly NativeEvent[] _skeleton;
        private readonly NativeEvent[] _samples;
        private readonly CorrectionSample[] _corrections;
        private int _mixerCount;
        private int _skeletonCount;
        private int _sampleCount;

        private readonly record struct NativeEvent(ulong Physics, long Evaluation);
        private readonly record struct CorrectionSample(int Tick, float PlanarX, float PlanarY, float OriginX, float OriginY, float Yaw);

        public PinCapture(NPCNavigationRigFixture fixture, int ticks)
        {
            _fixture = fixture;
            _mixer = new NativeEvent[(ticks * 4) + 16];
            _skeleton = new NativeEvent[(ticks * 4) + 16];
            _samples = new NativeEvent[ticks];
            _corrections = new CorrectionSample[ticks];
            fixture.Tree.MixerApplied += OnMixer;
            fixture.Skeleton.SkeletonUpdated += OnSkeleton;
        }

        private void OnMixer() => _mixer[_mixerCount++] = new NativeEvent(Engine.GetPhysicsFrames(), _fixture.Driver.EvaluationCount + 1);
        private void OnSkeleton() => _skeleton[_skeletonCount++] = new NativeEvent(Engine.GetPhysicsFrames(), _fixture.Driver.EvaluationCount);

        public void RecordCorrection(int tick, SwingCorrection correction) => _corrections[tick] = new CorrectionSample(tick,
            correction.Planar.X, correction.Planar.Y, correction.OriginShift.X, correction.OriginShift.Y, correction.Yaw);

        public void RecordSample(ulong physics, long evaluation) => _samples[_sampleCount++] = new NativeEvent(physics, evaluation);

        public void VerifyAndSave(string directory, string name)
        {
            Assert.Equal(_samples.Length, _sampleCount);
            for (int index = 0; index < _sampleCount; index++)
            {
                NativeEvent expected = new(_samples[0].Physics + (ulong)index, _samples[0].Evaluation + index);
                Assert.Equal(expected, _samples[index]);
                Assert.Contains(expected, _mixer.Take(_mixerCount));
                Assert.Contains(expected, _skeleton.Take(_skeletonCount));
            }

            File.WriteAllText(Path.Combine(directory, name + "-native.json"), JsonSerializer.Serialize(new
            {
                prediction_ticks = _sampleCount - 60,
                idle_ticks = 60,
                mixer = _mixer.Take(_mixerCount).ToArray(),
                skeleton = _skeleton.Take(_skeletonCount).ToArray(),
                samples = _samples,
                corrections = _corrections
            }));
        }

        public void AssertEquivalent(PinCapture candidate)
        {
            Assert.Equal(_corrections, candidate._corrections);
            Assert.Equal(Relative(_samples, _sampleCount), candidate.Relative(candidate._samples, candidate._sampleCount));
            // Include all raw notifications: queued preparation ticks and extra mixer applications are not discarded.
            Assert.Equal(Relative(_mixer, _mixerCount), candidate.Relative(candidate._mixer, candidate._mixerCount));
            Assert.Equal(Relative(_skeleton, _skeletonCount), candidate.Relative(candidate._skeleton, candidate._skeletonCount));
        }

        private IEnumerable<(long Physics, long Evaluation)> Relative(NativeEvent[] events, int count)
        {
            for (int index = 0; index < count; index++)
            {
                long physics = (long)events[index].Physics - (long)_samples[0].Physics;
                long evaluation = events[index].Evaluation - _samples[0].Evaluation;
                Assert.Equal(physics, evaluation);
                yield return (physics, evaluation);
            }
        }

        public void Dispose()
        {
            _fixture.Tree.MixerApplied -= OnMixer;
            _fixture.Skeleton.SkeletonUpdated -= OnSkeleton;
        }
    }

    private sealed class Chain
    {
        private readonly Skeleton3D _skeleton;
        private readonly string _side;
        private readonly int _hip;
        private readonly int _ankle;
        private readonly float _length;
        private bool _previous;
        private Vector3 _pin;
        private Vector3 _ball;
        private Vector3 _hipPosition;
        private Vector3 _anklePosition;
        private float _drift;
        private float _reach;
        private float _ballReach;
        private bool _livePrevious;
        private Vector3 _livePin;
        private float _liveDrift;
        private float _liveReach;
        private Vector3 _liveBall;
        private readonly FootPlantingTargetProvider _provider;
        private Vector3 _tablePin;
        private bool _tablePrevious;
        private float _tableDrift;
        private float _tableError;
        public int BallIndex
        {
            get;
        }

        public Chain(Skeleton3D skeleton, string side, FootPlantingTargetProvider provider)
        {
            _provider = provider;
            _skeleton = skeleton;
            _side = side;
            _hip = skeleton.FindBone(side + "UpperLeg");
            _ankle = skeleton.FindBone(side + "Foot");
            int knee = skeleton.FindBone(side + "LowerLeg");
            BallIndex = skeleton.FindBone(side + "Toes");
            _length = skeleton.GetBoneGlobalRest(_hip).Origin.DistanceTo(skeleton.GetBoneGlobalRest(knee).Origin)
                + skeleton.GetBoneGlobalRest(knee).Origin.DistanceTo(skeleton.GetBoneGlobalRest(_ankle).Origin);
        }

        public void Capture(Transform3D transform, bool planted)
        {
            _ball = transform * _skeleton.GetBoneGlobalPose(BallIndex).Origin;
            _hipPosition = transform * _skeleton.GetBoneGlobalPose(_hip).Origin;
            _anklePosition = transform * _skeleton.GetBoneGlobalPose(_ankle).Origin;
            if (planted && !_previous)
            {
                _pin = _ball;
            }

            _previous = planted;
            Vector3 offset = _pin - _ball;
            offset.Y = 0;
            _drift = planted ? offset.Length() * 1000 : 0;
            _reach = planted ? (_anklePosition + offset).DistanceTo(_hipPosition) / _length : 0;
            _ballReach = planted ? _pin.DistanceTo(_hipPosition) / _length : 0;
        }

        public void Write(StringBuilder rows, int tick, ulong physics, bool planted, string clip, string fading)
        {
            Vector3 post = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(BallIndex).Origin;
            float reach = _skeleton.GetBoneGlobalPose(_hip).Origin.DistanceTo(_skeleton.GetBoneGlobalPose(_ankle).Origin) / _length;
            FootPlantingTickDiagnostic diagnostic = _provider.LastDiagnostic;
            Assert.Equal(physics, diagnostic.Tick);
            _ = rows.AppendLine(FormattableString.Invariant($"{tick},{physics},{_side},{planted},{clip},{fading},{_ball.X:R},{_ball.Y:R},{_ball.Z:R},{_hipPosition.X:R},{_hipPosition.Y:R},{_hipPosition.Z:R},{_anklePosition.X:R},{_anklePosition.Y:R},{_anklePosition.Z:R},{_length:R},{_drift:R},{_reach:R},{_ballReach:R},{post.X:R},{post.Y:R},{post.Z:R},{reach:R},{_liveDrift:R},{_liveReach:R},{diagnostic.Pin.X:R},{diagnostic.Pin.Z:R},{diagnostic.ReachBlendOutStarted},{diagnostic.AnimatedBall.X:R},{diagnostic.AnimatedBall.Z:R},{diagnostic.AnimatedBall.DistanceTo(_liveBall) * 1000:R},{_tableDrift:R},{_tableError:R}"));
        }

        public void CaptureLive(bool planted)
        {
            Transform3D transform = _skeleton.GlobalTransform;
            Vector3 ball = transform * _skeleton.GetBoneGlobalPose(BallIndex).Origin;
            _liveBall = ball;
            if (planted && !_livePrevious)
            {
                _livePin = ball;
            }

            _livePrevious = planted;
            Vector3 offset = _livePin - ball;
            offset.Y = 0;
            _liveDrift = planted ? offset.Length() * 1000 : 0;
            _liveReach = planted ? ((transform * _skeleton.GetBoneGlobalPose(_ankle).Origin) + offset)
                .DistanceTo(transform * _skeleton.GetBoneGlobalPose(_hip).Origin) / _length : 0;
        }

        public void CaptureTable(Vector3? predicted, bool planted)
        {
            if (predicted is not { } ball)
            {
                _tableDrift = float.NaN;
                _tableError = float.NaN;
                return;
            }

            if (planted && !_tablePrevious)
            {
                _tablePin = ball;
            }

            _tablePrevious = planted;
            _tableDrift = planted ? new Vector2(ball.X - _tablePin.X, ball.Z - _tablePin.Z).Length() * 1000 : 0;
            _tableError = ball.DistanceTo(_ball) * 1000;
        }
    }
}
