using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Approved recovery source order through real input/submission wiring; synthetic endpoints, never native inference.</summary>
    [Headless]
    [Fact]
    public Task RecoveryTurn_ProductionSubmitsBothFourLandmarkGroups() => CheckRecoveryGroupAsync(.5f);

    /// <summary>The final recovery must complete before a clock-selected whole-cycle yaw drop.</summary>
    [Headless]
    [Fact]
    public Task RecoveryTurn_FullInputDropsOnlyAfterFinalRecovery() => CheckRecoveryGroupAsync(1);

    private static async Task CheckRecoveryGroupAsync(float magnitude)
    {
        var evidence = new List<object>();
        var observations = new List<(int[] Frames, float[] Angles, bool RawNonRoots)>();
        {
            foreach (int sign in new[] { -1, 1 })
            {
                await using ClosureFixture fixture = await ClosureFixture.CreateAsync(.75f);
                fixture.Player.ProcessMode = Node.ProcessModeEnum.Disabled;
                WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
                Assert.Null(driver.Completion);
                ExhaustionField("_worker").SetValue(driver, fixture.Root.MotionWorkers.Create(() => new MetadataTurnEndpointSession()));
                var starts = new List<int>();
                var angles = new List<float>();
                bool rawNonRoots = true;
                string directory = ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine");
                var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
                var source = new ReviewedMotionSource(Path.Combine(directory, "landing-ablation-01.json"), data);
                string role = sign < 0 ? "left_turn" : "right_turn";
                using var set = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "set.json")));
                JsonElement cycleData = set.RootElement.GetProperty("roles").EnumerateArray()
                    .Single(e => e.GetProperty("cycle").GetProperty("role").GetString() == role).GetProperty("cycle");
                ReviewedMotionSource.ExperimentalCycle cycle = source.LoadExperimentalCycle(cycleData, source.GetRole("feminine", "normal", role));
                var raw = new Transform3D[77 * 4];
                driver.WindowSubmitted += (_, _, target) =>
                {
                    int start = driver.LastReviewedRequest!.Value.WindowStart;
                    starts.Add(start);
                    angles.Add(Mathf.RadToDeg(Mathf.Atan2(target[0][0].Basis.Z.X, target[0][0].Basis.Z.Z)));
                    source.CopyCycleWindow(cycle, start, raw);
                    for (int i = 0; i < 4; i++)
                    {
                        for (int joint = 1; joint < 77; joint++)
                        {
                            rawNonRoots &= raw[(i * 77) + joint] == target[i][joint];
                        }
                    }
                };
                fixture.Motion.SetStick(false, new Vector2(sign * magnitude, 0));
                for (int tick = 0; tick < 1600 && starts.Count < 5; tick++)
                {
                    driver.Advance(1.0 / 120, fixture.Motion.MovementIntent, Basis.Identity, fixture.Motion.TurnIntent);
                    driver.Observe();
                    if (driver.RecordingPending)
                    {
                        await Task.Delay(1); // Worker yield only, no physical/gait settling assumption.
                    }
                }
                evidence.Add(new
                {
                    role,
                    magnitude,
                    starts,
                    angles,
                    rawNonRoots
                });
                observations.Add(([.. starts], [.. angles], rawNonRoots));
                Assert.Equal("", fixture.Motion.Failure);
            }
            string path = ProjectSettings.GlobalizePath($"res://temp/motionbricks/anim002-regenerated-source-adoption-01/driver-{magnitude}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(evidence));
            int nextLeft = magnitude == .5f ? 1007 : 1067, nextRight = magnitude == .5f ? 1163 : 1223;
            Assert.Equal([947, 951, 961, 1001, nextLeft], observations[0].Frames);
            Assert.Equal([1103, 1107, 1120, 1152, nextRight], observations[1].Frames);
            // Independent exported Hips-angle oracle. At half input the approved rate*intensity*H cap
            // is 27.293686847deg left / 37.302033696deg right; the last-left/second-right-lift deltas bind.
            // Synthetic endpoint completion carries that shortfall forward; it does not rewrite source angles.
            double[][] expected = magnitude == .5f ? [
                [4.471126472374838,18.00060249623247,38.89189768944148,66.1855845364488,68.91958446512824],
                [-20.907733993342532,-39.10439950786896,-76.40643320360022,-85.45024508691796,-106.34044565398865]] : [
                [4.471126472374838,18.00060249623247,38.89189768944148,68.57880861799906,71.3128085466785],
                [-20.907733993342532,-39.10439950786896,-82.32564079780614,-91.36945268112389,-112.25965324819458]];
            for (int role = 0; role < 2; role++)
            {
                Assert.True(observations[role].RawNonRoots);
                for (int i = 0; i < 5; i++)
                {
                    Assert.InRange(Math.Abs(observations[role].Angles[i] - expected[role][i]), 0, .001);
                }
            }
        }
    }
}
