using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class ContraryTurnSession : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            Transform3D[][] frames = [.. Enumerable.Range(0, 31).Select(_ => (Transform3D[])context[3].Clone())];
            foreach (Transform3D[] frame in frames)
            {
                frame[0].Basis = new Basis(Vector3.Up, -.8f);
            }
            return frames;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Contrary generated hips stay in the world pose; the actor follows the first mapped lift instead.</summary>
    [Headless]
    [Fact]
    public async Task PlannedCTRLTurn_ContraryHipsDoNotSteerActor()
    {
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync(.75f);
        fixture.Player.ProcessMode = Node.ProcessModeEnum.Disabled;
        WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
        SomaInferenceWorker worker = fixture.Root.MotionWorkers.Create(() => new ContraryTurnSession());
        ExhaustionField("_worker").SetValue(driver, worker);
        Skeleton3D source = fixture.Skeleton.GetParent().GetParent<Skeleton3D>();
        double entry = fixture.Player.GlobalRotation.Y;
        fixture.Motion.SetStick(false, new Vector2(-1, 0));
        int checkedFrames = 0;
        for (int i = 0; i < 300; i++)
        {
            driver.Advance(1.0 / 120, Vector2.Zero, Basis.Identity, fixture.Motion.TurnIntent);
            driver.Observe();
            if (driver.RecordingPending && !driver.IsPlaying)
            {
                await WaitForMotionPublicationAsync(fixture.Tree, worker);
            }
            if (!driver.RecordingAdvanced)
            {
                continue;
            }
            // The first left lift is exactly one source-frame interval from entry.
            double expected = entry + (driver.RecordingFrame / 30 * (8.026955 * Math.PI / 180));
            Assert.InRange(Math.Abs(fixture.Player.GlobalRotation.Y - expected), 0, .00001);
            Basis world = source.GlobalBasis * source.GetBoneGlobalPose(0).Basis;
            Basis generated = SomaMotionData.ModelToWorld * new Basis(Vector3.Up, -.8f);
            Assert.True(world.IsEqualApprox(generated));
            checkedFrames++;
            if (driver.RecordingFrame == 30)
            {
                break;
            }
        }
        Assert.True(checkedFrames >= 120);
        Assert.Equal(30, driver.RecordingFrame);
        double held = fixture.Player.GlobalRotation.Y;
        fixture.Motion.SetStick(false, Vector2.Zero);
        // The player is manually stepped; evaluate its tree to supply the release destination.
        fixture.Player.GetNode<AnimationTree>("AnimationTree").Advance(1.0 / 120);
        for (int i = 0; i < 180; i++)
        {
            driver.Advance(1.0 / 120, Vector2.Zero, Basis.Identity, 0);
            driver.Observe();
            if (driver.RecordingPending)
            {
                await WaitForMotionPublicationAsync(fixture.Tree, worker);
            }
            if (driver.RecordingAdvanced && driver.Phase == "settling")
            {
                Assert.Equal(0, driver.RecordingFrame);
                Assert.InRange(Math.Abs(fixture.Player.GlobalRotation.Y - held), 0, .00001);
                Basis world = source.GlobalBasis * source.GetBoneGlobalPose(0).Basis;
                Assert.True(world.IsEqualApprox(SomaMotionData.ModelToWorld * new Basis(Vector3.Up, -.8f)));
                return;
            }
        }
        Assert.Fail("No settling frame zero was consumed.");
    }

    /// <summary>Native-free endpoint fixture; not generated movement or visual acceptance.</summary>
    [Headless]
    [Fact]
    public async Task MetadataTurn_ProductionFirstPairUsesMeasuredCuesAndAngles()
    {
        {
            await using StationaryTurnFixture fixture = await StationaryTurnFixture.CreateAsync();
            WalkingExperimentDriver driver = fixture.Driver;
            SomaInferenceWorker worker = fixture.UseSession(new MetadataTurnEndpointSession());
            var targets = new List<(int Frame, float Angle)>();
            driver.WindowSubmitted += (_, _, target) =>
            {
                Vector3 z = target[0][0].Basis.Z;
                targets.Add((driver.LastReviewedRequest!.Value.WindowStart, Mathf.RadToDeg(Mathf.Atan2(z.X, z.Z))));
            };
            fixture.Motion.SetStick(false, new Vector2(-1, 0));
            for (int i = 0; i < 600 && targets.Count < 2; i++)
            {
                await fixture.TickThroughPublicationAsync(worker);
            }
            Assert.True(targets.Count >= 2);
            Assert.Equal(947, targets[0].Frame);
            Assert.InRange(Math.Abs(targets[0].Angle - 8.026955038153414), 0, .001);
            Assert.Equal(961, targets[1].Frame);
            Assert.InRange(Math.Abs(targets[1].Angle - 69.82211664788655), 0, .001);
        }
    }

    /// <summary>Production scheduling with synthetic endpoints exercises prospective input and immutable cycle ownership.</summary>
    [Headless]
    [Fact]
    public async Task MetadataTurn_InputSnapshotsRetirementAndLagStayCoordinated()
    {
        {
            await using StationaryTurnFixture fixture = await StationaryTurnFixture.CreateAsync();
            WalkingExperimentDriver driver = fixture.Driver;
            SomaInferenceWorker worker = fixture.UseSession(new MetadataTurnEndpointSession());
            var submissions = new List<MetadataStationaryTurn.Snapshot>();
            long submittedTicket = 0;
            driver.WindowSubmitted += (ticket, _, _) =>
            {
                submittedTicket = ticket;
                if (driver.MetadataTurn is not null)
                {
                    submissions.Add(driver.LastMetadataTurnRequest!);
                }
            };
            fixture.Motion.SetStick(false, new Vector2(-1, 0));
            MetadataStationaryTurn state = driver.MetadataTurn!;
            async Task NextSubmissionAsync()
            {
                int previous = driver.Requests;
                for (int tick = 0; tick < 210 && driver.Requests == previous; tick++)
                {
                    await fixture.TickThroughPublicationAsync(worker);
                }
                Assert.Equal(previous + 1, driver.Requests);
            }
            await NextSubmissionAsync();
            MetadataStationaryTurn.Snapshot first = state.Owner!;
            long epoch = CompatibleTurnEpoch(fixture.Motion);
            long ticket = submittedTicket;
            fixture.Motion.SetStick(false, new Vector2(-.5f, 0));
            Assert.Same(first, state.Owner);
            Assert.Equal(ticket, submittedTicket);
            await NextSubmissionAsync();
            Assert.Equal(ticket, driver.LastAppliedRequest);
            Assert.Equal(1, state.Owner!.Magnitude);
            Assert.False(state.Complete(first));
            await NextSubmissionAsync();
            MetadataStationaryTurn.Snapshot partial = state.Owner!;
            Assert.Equal(.5, partial.Magnitude);
            ticket = submittedTicket;
            fixture.Motion.SetStick(false, new Vector2(-.75f, 0));
            Assert.Same(partial, state.Owner);
            Assert.Equal(ticket, submittedTicket);
            Assert.Equal(-.5f, driver.SubmittedTurnIntent);
            await NextSubmissionAsync();
            Assert.Equal(ticket, driver.LastAppliedRequest);
            Assert.Equal(-.5f, driver.SubmittedTurnIntent);
            await NextSubmissionAsync();
            Assert.Equal(-.75f, driver.SubmittedTurnIntent);
            Assert.Equal(epoch, CompatibleTurnEpoch(fixture.Motion));
            Assert.Equal(["LeftFoot", "RightFoot", "LeftFoot", "RightFoot", "LeftFoot"], submissions.Select(s => s.Foot));
            fixture.Motion.SetStick(false, Vector2.Zero);
            MetadataStationaryTurn.Snapshot retiredOnRelease = driver.LastMetadataTurnRequest!;
            Assert.Null(state.Owner);
            Assert.False(state.Complete(retiredOnRelease));
            Assert.Null(driver.MetadataTurn);
            Assert.False(driver.IsPlaying);
            Assert.False(driver.RecordingPending);
            fixture.Motion.SetStick(false, new Vector2(1, 0));
            Assert.NotSame(state, driver.MetadataTurn);
            Assert.Equal("right_turn", driver.MetadataTurn!.Reference.Role);
            Assert.Equal(0, driver.MetadataTurn.Cycle);
            await fixture.TickThroughPublicationAsync(worker);
            Assert.Equal(1103, driver.LastMetadataTurnRequest!.WindowStart);
            Assert.Equal("RightFoot", driver.LastMetadataTurnRequest.Foot);
            Assert.Equal("", fixture.Motion.Failure);
        }
    }

    /// <summary>Explicit windows retain raw articulation, uneven30Hz yaw variation and source order across the boundary.</summary>
    [Headless]
    [Fact]
    public void MetadataTurn_ExplicitWindowsPreserveDerivativesAndRawWrap()
    {
        string directory = ProjectSettings.GlobalizePath("res://assets/animations/locomotion/feminine");
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
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
            Transform3D[][] placed = sampler.FiniteTurnTargets("left_turn", 0, start, start - 946, 0, .5, Vector3.Zero);
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
