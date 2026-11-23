using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>NAV-R01: four source roots retain ordered local trajectory under one common positioning transform.</summary>
    [Headless]
    [Fact]
    public async Task PositioningPreservesRawRootTrajectoryAndSingleIntentScale()
    {
        const double precision = .000002;
        var errors = new List<string>();
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        using var set = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/set.json")));
        static Vector3 Horizontal(Vector3 p)
        {
            return new(p.X, 0, p.Z);
        }

        foreach ((string role, float distance, float scale) in new[] { ("left_noncross", 1f, 1f), ("left_noncross", 1f, .5f), ("backward", 1f, 1f), ("left_noncross", .11f, 1f) })
        {
            await using PositioningNavigationFixture fixture = await PositioningNavigationFixture.CreateAsync();
            MotionBricksNavigation nav = fixture.Navigation;
            Transform3D[][]? submitted = null, actualContext = null;
            MotionBricksNavigation.RoleSubmission? snapshot = null;
            bool contextCorrect = false;
            nav.WindowSubmitted += (_, context, target) =>
            {
                if (submitted is not null)
                {
                    return;
                }
                actualContext = context;
                submitted = target;
                snapshot = nav.SubmittedRole;
                contextCorrect = fixture.MatchesHistory(context);
            };
            nav.NormalisedSpeed = scale;
            Vector3 start = fixture.Actor.GlobalPosition;
            float heading = fixture.Heading;
            Vector3 direction = role == "backward" ? -new Vector3(Mathf.Sin(heading), 0, Mathf.Cos(heading)) : new(Mathf.Cos(heading), 0, -Mathf.Sin(heading));
            Vector3 goal = start + (direction * distance);
            await fixture.SelectDestinationAsync(new(new Basis(Vector3.Up, heading - Mathf.Pi), goal));
            for (int tick = 0; tick < 90 && submitted is null && nav.Failure.Length == 0; tick++)
            {
                await fixture.StepAsync(1);
            }

            Assert.True(nav.Failure.Length == 0, nav.Failure);
            Assert.NotNull(submitted);
            Assert.NotNull(snapshot);
            Assert.NotNull(actualContext);
            Assert.Equal(role, snapshot.SelectedRole);
            Assert.True(contextCorrect);
            Assert.Equal(0, fixture.Provider.Faults);
            JsonElement entry = set.RootElement.GetProperty("roles").EnumerateArray().Single(e => e.GetProperty("cycle").GetProperty("role").GetString() == role);
            var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/" + entry.GetProperty("source").GetString()), data);
            ReviewedMotionSource.Role sourceRole = source.GetRole("feminine", "normal", role);
            ReviewedMotionSource.ExperimentalCycle cycle = source.LoadExperimentalCycle(entry.GetProperty("cycle"), sourceRole);
            var raw = new Transform3D[source.JointCount * 4];
            source.CopyCycleWindow(cycle, snapshot.Source.WindowStart, raw);
            Assert.Equal(77, source.JointCount);
            Vector3[] rawPoints = [.. Enumerable.Range(0, 4).Select(i => Horizontal(raw[i * source.JointCount].Origin))];
            Vector3[] placed = [.. submitted.Select(p => Horizontal(SomaMotionData.ModelToWorld * p[0].Origin))];
            double[] rawIncrements = [.. Enumerable.Range(0, 3).Select(i => (double)rawPoints[i].DistanceTo(rawPoints[i + 1]))];
            Assert.True(rawIncrements.Max() - rawIncrements.Min() > precision, "Raw source must distinguish flattening above transport precision.");
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(raw[i * source.JointCount].Origin.Y, submitted[i][0].Origin.Y);
                for (int joint = 1; joint < source.JointCount; joint++)
                {
                    Assert.Equal(raw[(i * source.JointCount) + joint], submitted[i][joint]);
                }

                Basis rawRelative = raw[0].Basis.Inverse() * raw[i * source.JointCount].Basis;
                Basis placedRelative = submitted[0][0].Basis.Inverse() * submitted[i][0].Basis;
                Assert.True(rawRelative.IsEqualApprox(placedRelative), "Common root placement must preserve local orientation/yaw variation.");
                Assert.InRange(Math.Abs(Mathf.Wrap(fixture.Hip(submitted[0]) - fixture.Hip(actualContext[3]), -Mathf.Pi, Mathf.Pi)), 0, .00001f);
                float along = (placed[i] - start).Dot(direction);
                Assert.InRange(along, -precision, distance + precision);
                for (int j = i + 1; j < 4; j++)
                {
                    double error = Math.Abs(placed[i].DistanceTo(placed[j]) - (rawPoints[i].DistanceTo(rawPoints[j]) * scale));
                    if (error > precision)
                    {
                        errors.Add($"{role}/{distance}/{scale} samples{i}-{j}: horizontal differential error {error:R} m");
                    }
                }
            }
            Vector3 chord = placed[3] - placed[0];
            if (chord.Length() > precision)
            {
                Assert.True(chord.Dot(direction) > 0);
                Assert.InRange(Math.Abs(chord.Cross(direction).Y), 0, precision);
            }
            float rawArea = (rawPoints[1] - rawPoints[0]).Cross(rawPoints[3] - rawPoints[0]).Y * scale * scale;
            float placedArea = (placed[1] - placed[0]).Cross(placed[3] - placed[0]).Y;
            double areaPrecision = precision * ((rawPoints[0].DistanceTo(rawPoints[3]) * scale) + chord.Length());
            if (Math.Abs(rawArea - placedArea) > areaPrecision)
            {
                errors.Add($"{role}/{distance}/{scale}: signed trajectory area changed");
            }

            nav.ClearDestination();
        }
        Assert.Empty(errors);
    }
}
