using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Half-input source order through real input/submission wiring; synthetic endpoints, never native inference.</summary>
    [Headless]
    [Fact]
    public Task MappedTurn_HalfInputUsesBothPrimaryLifts() => CheckPrimaryGroupAsync(.5f);

    /// <summary>Full input uses the same source poses with wider angular separation.</summary>
    [Headless]
    [Fact]
    public Task MappedTurn_FullInputUsesBothPrimaryLifts() => CheckPrimaryGroupAsync(1);

    private static async Task CheckPrimaryGroupAsync(float magnitude)
    {
        var observations = new List<(int[] Frames, float[] Angles, bool RawNonRoots)>();
        foreach (int sign in new[] { -1, 1 })
        {
            await using StationaryTurnFixture fixture = await StationaryTurnFixture.CreateAsync();
            WalkingExperimentDriver driver = fixture.Driver;
            SomaInferenceWorker worker = fixture.UseSession(new MetadataTurnEndpointSession());
            var starts = new List<int>();
            var angles = new List<float>();
            bool rawNonRoots = true;
            string directory = ProjectSettings.GlobalizePath("res://assets/animations/locomotion/feminine");
            var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
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
                await fixture.TickThroughPublicationAsync(worker);
            }
            observations.Add(([.. starts], [.. angles], rawNonRoots));
            Assert.Equal("", fixture.Motion.Failure);
        }
        Assert.Equal([947, 961, 1007, 1021, 1067], observations[0].Frames);
        Assert.Equal([1103, 1120, 1163, 1180, 1223], observations[1].Frames);
        // Absolute primary-lift headings for 120 degrees, from independently exported source angles.
        double[][] expected = [[8.02696, 69.82212, 128.02696, 189.82212, 248.02696],
                [-27.46443, -108.14307, -147.46443, -228.14307, -267.46443]];
        for (int role = 0; role < 2; role++)
        {
            Assert.True(observations[role].RawNonRoots);
            for (int i = 0; i < 5; i++)
            {
                Assert.InRange(Math.Abs(Math.IEEERemainder(observations[role].Angles[i] - (magnitude * expected[role][i]), 360)), 0, .001);
            }
        }
    }
}
