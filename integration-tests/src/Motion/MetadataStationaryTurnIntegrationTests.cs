using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Native-free endpoint fixture; not generated movement or visual acceptance.</summary>
    [Headless]
    [Fact]
    public async Task MetadataTurn_ProductionFirstPairUsesMeasuredCuesAndAngles()
    {
        {
            await using ClosureFixture fixture = await ClosureFixture.CreateAsync(.75f);
            fixture.Player.ProcessMode = Node.ProcessModeEnum.Disabled;
            WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
            Assert.Null(driver.Completion);
            SomaInferenceWorker worker = fixture.Root.MotionWorkers.Create(() => new MetadataTurnEndpointSession());
            ExhaustionField("_worker").SetValue(driver, worker);
            var targets = new List<(int Frame, float Angle)>();
            driver.WindowSubmitted += (_, _, target) =>
            {
                Vector3 z = target[0][0].Basis.Z;
                if (driver.LastMetadataTurnRequest!.IsPrimaryLift)
                {
                    targets.Add((driver.LastReviewedRequest!.Value.WindowStart, Mathf.RadToDeg(Mathf.Atan2(z.X, z.Z))));
                }
            };
            fixture.Motion.SetStick(false, new Vector2(-1, 0));
            for (int i = 0; i < 600 && targets.Count < 2; i++)
            {
                driver.Advance(1.0 / 120, fixture.Motion.MovementIntent, Basis.Identity, fixture.Motion.TurnIntent);
                driver.Observe();
                if (driver.RecordingPending)
                {
                    await Task.Delay(1); // Yield to the injected worker only; no scene/motion settling assumption.
                }
            }
            Assert.True(targets.Count >= 2);
            string evidence = ProjectSettings.GlobalizePath("res://temp/motionbricks/anim002-regenerated-source-adoption-01/first-pair-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(evidence, JsonSerializer.Serialize(targets.Select(t => new { t.Frame, t.Angle })));
            Assert.Equal(947, targets[0].Frame);
            Assert.InRange(Math.Abs(targets[0].Angle - 4.471126472374838), 0, .001);
            Assert.Equal(961, targets[1].Frame);
            Assert.InRange(Math.Abs(targets[1].Angle - 38.89189768944148), 0, .001);
        }
    }

    /// <summary>Production scheduling with synthetic endpoints exercises prospective input and constant-size lag debt.</summary>
    [Headless]
    [Fact]
    public async Task MetadataTurn_InputSnapshotsRetirementAndLagStayCoordinated()
    {
        {
            await using ClosureFixture fixture = await ClosureFixture.CreateAsync(.75f);
            fixture.Player.ProcessMode = Node.ProcessModeEnum.Disabled;
            WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
            Assert.Null(driver.Completion);
            ExhaustionField("_worker").SetValue(driver, fixture.Root.MotionWorkers.Create(() => new MetadataTurnEndpointSession()));
            var submissions = new List<MetadataStationaryTurn.Snapshot>();
            driver.WindowSubmitted += (_, _, _) =>
            {
                if (driver.MetadataTurn is not null)
                {
                    submissions.Add(driver.LastMetadataTurnRequest!);
                }
            };
            fixture.Motion.SetStick(false, new Vector2(-1, 0));
            MetadataStationaryTurn state = driver.MetadataTurn!;
            async Task TickAsync()
            {
                driver.Advance(1.0 / 120, fixture.Motion.MovementIntent, Basis.Identity, fixture.Motion.TurnIntent);
                driver.Observe();
                if (driver.RecordingPending)
                {
                    await Task.Delay(1);
                }
            }
            for (int tick = 0; tick < 1560; tick++)
            {
                if (tick is 120 or 360)
                {
                    MetadataStationaryTurn.Snapshot owner = state.Owner!;
                    int count = driver.Requests;
                    fixture.Motion.SetStick(false, new Vector2(tick == 120 ? -.95f : -1, 0));
                    Assert.Same(owner, state.Owner);
                    Assert.Equal(count, driver.Requests);
                }
                await TickAsync();
            }
            Assert.InRange(Math.Abs(state.Phase - (30 * (1 + (.95f * 2) + 10))), 0, .001);
            Assert.Contains(submissions, s => Math.Abs(s.Magnitude - .95) < 1e-6);
            Assert.True(state.DroppedCycles > 0);
            Assert.Equal(0, state.PassedUnplayed);
            Assert.Equal(["lift_LeftFoot", "recover_after_LeftFoot", "lift_RightFoot", "recover_after_RightFoot"],
                submissions.Take(4).Select(s => s.EventId));
            Assert.Equal([true, false, true, false], submissions.Take(4).Select(s => s.IsPrimaryLift));
            Assert.All(submissions.Where(s => s.EventKind == StationaryTurnReference.LandmarkKind.Recovery), s =>
            {
                Assert.Null(s.Foot);
                Assert.Equal(s.SubjectFoot, s.RecoveringFoot);
                Assert.Contains("\"EventKind\":\"recovery\"", JsonSerializer.Serialize(s));
            });
            Assert.True(state.PrimaryLiftCompletions >= 4);
            Assert.True(state.RecoveryCompletions >= 4);
            Assert.Equal(["LeftFoot", "RightFoot", "LeftFoot", "RightFoot"], submissions.Where(s => s.IsPrimaryLift).Take(4).Select(s => s.Foot));
            Assert.All(submissions.Zip(submissions.Skip(1)), pair =>
            {
                Assert.True(pair.Second.SelectedPhase >= pair.First.SelectedPhase);
                Assert.InRange(Math.Abs(pair.Second.RootYaw - pair.First.RootYaw), 0, pair.Second.Cap + 1e-5);
                if (pair.Second.DroppedCycles > pair.First.DroppedCycles)
                {
                    Assert.InRange((pair.Second.RootYaw - pair.First.RootYaw) * 180 / Math.PI, 2.733, 2.735);
                }
            });
            double phase = state.Phase;
            int floor = state.Floor;
            MetadataStationaryTurn.Snapshot old = state.Owner!;
            fixture.Motion.SetStick(false, new Vector2(-.5f, 0));
            Assert.Same(state, driver.MetadataTurn);
            Assert.Null(state.Owner);
            Assert.Equal(phase, state.Phase);
            Assert.Equal(floor, state.Floor);
            Assert.False(driver.IsPlaying);
            Assert.False(driver.RecordingPending);
            Assert.False(state.Complete(old, old.RootYaw));
            await TickAsync();
            Assert.Equal(.5, state.Owner!.Magnitude);
            Assert.True(state.Owner.SelectedPhase >= floor);
            Assert.InRange(Math.Abs(state.Phase - phase - .125), 0, 1e-7);
            fixture.Motion.SetStick(false, Vector2.Zero);
            MetadataStationaryTurn.Snapshot retiredOnRelease = driver.LastMetadataTurnRequest!;
            Assert.Null(state.Owner);
            Assert.False(state.Complete(retiredOnRelease, retiredOnRelease.RootYaw));
            Assert.Null(driver.MetadataTurn);
            Assert.False(driver.IsPlaying);
            Assert.False(driver.RecordingPending);
            fixture.Motion.SetStick(false, new Vector2(1, 0));
            Assert.NotSame(state, driver.MetadataTurn);
            Assert.Equal("right_turn", driver.MetadataTurn!.Reference.Role);
            Assert.Equal(0, driver.MetadataTurn.Phase);
            await TickAsync();
            Assert.Equal(1103, driver.LastMetadataTurnRequest!.WindowStart);
            Assert.Equal("RightFoot", driver.LastMetadataTurnRequest.Foot);
            Assert.Equal("", fixture.Motion.Failure);
        }
    }

    /// <summary>Reuse the gated late-return fixture; no native session can be created.</summary>
    [Headless]
    [Fact]
    public async Task MetadataTurn_RetirementRejectsActualLateWorkerResult() =>
        await CompatibleTurn_IncompatibleChangesRetireAndLateReturnStaysStale();

    /// <summary>Explicit windows retain raw articulation, uneven30Hz yaw variation and source order across the boundary.</summary>
    [Headless]
    [Fact]
    public void MetadataTurn_ExplicitWindowsPreserveDerivativesAndRawWrap()
    {
        string directory = ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine");
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var sampler = new ReviewedRoleSampler(Path.Combine(directory, "set.json"), data);
        var source = new ReviewedMotionSource(Path.Combine(directory, "landing-ablation-01.json"), data);
        ReviewedMotionSource.Role role = source.GetRole("feminine", "normal", "left_turn");
        using var set = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "set.json")));
        JsonElement declaration = set.RootElement.GetProperty("roles").EnumerateArray()
            .Single(e => e.GetProperty("cycle").GetProperty("role").GetString() == "left_turn").GetProperty("cycle");
        ReviewedMotionSource.ExperimentalCycle cycle = source.LoadExperimentalCycle(declaration, role);
        Assert.True(sampler.SetIntent("left_turn", 0));
        JsonElement samples = source.Manifest.GetProperty("samples");
        double entryYaw = samples[945].GetProperty("heading_radians").GetDouble();
        double span = samples[1005].GetProperty("heading_radians").GetDouble() - entryYaw;
        var raw = new Transform3D[data.Names.Length * 4];
        (int Start, int[] Originals, int[] Cycles)[] windows = [
            (985, [985,986,987,988], [0,0,0,0]),
            (1004, [1004,1005,946,947], [0,0,1,1])];
        foreach ((int start, int[] originals, int[] cycles) in windows)
        {
            double[] expectedYaw = [.. originals.Select((f, i) => .5 *
                (samples[f - 1].GetProperty("heading_radians").GetDouble() - entryYaw + (cycles[i] * span)))];
            MetadataStationaryTurn.Snapshot snapshot = new(1, 1, "left_turn", start - 946, start - 946,
                start - 946, start, "recover_after_RightFoot", StationaryTurnReference.LandmarkKind.Recovery,
                "RightFoot", false, .5, .629770886, .5, 0, 0, expectedYaw[0], 0, 0, 0);
            Transform3D[][] placed = sampler.TurnTargets(snapshot, Vector3.Zero);
            source.CopyCycleWindow(cycle, start, raw);
            Assert.Equal(start, sampler.Selection.WindowStart);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(originals[i], cycle.Locate(start + i).OriginalFrame);
                Assert.Equal(cycles[i], cycle.Locate(start + i).CycleIndex);
                for (int joint = 1; joint < data.Names.Length; joint++)
                {
                    Assert.Equal(raw[(i * data.Names.Length) + joint], placed[i][joint]);
                }
                double yaw = Math.Atan2(placed[i][0].Basis.Z.X, placed[i][0].Basis.Z.Z);
                Assert.InRange(Math.Abs(yaw - expectedYaw[i]), 0, 1e-5);
                if (i > 0)
                {
                    double previous = Math.Atan2(placed[i - 1][0].Basis.Z.X, placed[i - 1][0].Basis.Z.Z);
                    Assert.InRange(Math.Abs(((yaw - previous) * 30) - ((expectedYaw[i] - expectedYaw[i - 1]) * 30)), 0, 1e-4);
                }
            }
        }
    }

    private sealed class MetadataTurnEndpointSession : SomaInferenceWorker.ISession
    {
        private int _calls;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            int count = ++_calls % 2 == 1 ? 30 : 50;
            return [.. Enumerable.Range(0, count).Select(i => context[3].Select((t, j) =>
                t.InterpolateWith(target[0][j], (float)i / (count - 1))).ToArray())];
        }
        public void Dispose()
        {
        }
    }
}
