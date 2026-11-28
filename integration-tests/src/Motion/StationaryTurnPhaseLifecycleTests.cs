using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Shared intended phase and raw pose continuity; no native output or foot-naturalness acceptance.</summary>
public sealed class StationaryTurnPhaseLifecycleTests
{
    /// <summary>Aligned activation occurs once; wraps keep raw articulation/SE2, interruption reinitialises, travel stays unchanged.</summary>
    [Headless]
    [Fact]
    public void AngularEntry_ContinuesThroughWrapReactivationAndTravelWithoutPlacementJump()
    {
        const double horizon = 49.0 / 30;
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
        string set = ProjectSettings.GlobalizePath("res://assets/animations/locomotion/feminine/set.json");
        var sampler = new ReviewedRoleSampler(set, data);
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath("res://assets/animations/locomotion/feminine/landing-ablation-01.json"), data);
        ReviewedMotionSource.Role role = source.GetRole("feminine", "normal", "left_turn");
        using var document = JsonDocument.Parse(File.ReadAllText(set));
        JsonElement receipt = document.RootElement.GetProperty("roles").EnumerateArray()
            .Single(e => e.GetProperty("cycle").GetProperty("role").GetString() == "left_turn").GetProperty("cycle");
        ReviewedMotionSource.ExperimentalCycle cycle = source.LoadExperimentalCycle(receipt, role);
        Basis facing = new(Vector3.Up, .4f);
        Vector3 ground = new(3, 0, 5);
        Assert.True(sampler.SetIntent("left_turn", 0, horizon));
        Transform3D[][] first = sampler.Targets(0, horizon, facing, ground);
        long revision = sampler.Selection.Revision;
        Assert.Equal(957, sampler.Selection.SourceFrame);
        Assert.Equal(1006, sampler.Selection.WindowStart);
        Assert.False(sampler.SetIntent("left_turn", 1, 34.0 / 30)); // Same role, changed request/horizon: no rearming.
        _ = sampler.Targets(1, horizon, facing, ground);
        Assert.Equal(987, sampler.Selection.SourceFrame);
        Assert.Equal(1036, sampler.Selection.WindowStart);
        Assert.Equal(revision, sampler.Selection.Revision);

        Transform3D[][] wrapped = sampler.Targets(1.95, horizon, facing, ground);
        Assert.Equal(1064, sampler.Selection.WindowStart);
        Assert.Equal([1004, 1005, 946, 947], Enumerable.Range(1064, 4).Select(f => cycle.Locate(f).OriginalFrame));
        var raw = new Transform3D[77 * 4];
        source.CopyCycleWindow(cycle, 1064, raw);
        Basis normalise = facing * new Basis(Vector3.Up, -Mathf.Atan2(raw[0].Basis.Z.X, raw[0].Basis.Z.Z));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(raw.AsSpan((i * 77) + 1, 76).ToArray(), wrapped[i][1..]);
            Vector3 relative = raw[i * 77].Origin - raw[0].Origin;
            relative.Y = raw[i * 77].Origin.Y;
            Assert.True((ground + (normalise * relative)).IsEqualApprox(wrapped[i][0].Origin));
            Assert.True((normalise * raw[i * 77].Basis).IsEqualApprox(wrapped[i][0].Basis));
        }
        Transform3D[][] nextCycle = sampler.Targets(2, horizon, facing, ground);
        Assert.Equal(1066, sampler.Selection.WindowStart);
        for (int i = 0; i < 4; i++)
        {
            // The accumulated episode yaw/drift is not an extra actor-root placement on first or later wraps.
            Assert.True(first[i][0].IsEqualApprox(nextCycle[i][0]));
            Assert.Equal(first[i][1..], nextCycle[i][1..]);
        }
        Assert.Equal(new Vector2(3, 5), new Vector2(first[0][0].Origin.X, first[0][0].Origin.Z));
        _ = sampler.SetIntent("", 2);
        Assert.False(sampler.Selection.Available);
        _ = Assert.Throws<InvalidOperationException>(() => sampler.Targets(2, horizon, facing, ground));
        _ = sampler.SetIntent("left_turn", 3, horizon);
        _ = sampler.Targets(3, horizon, facing, ground);
        Assert.Equal(1006, sampler.Selection.WindowStart);
        Assert.True(sampler.Selection.Revision > revision);
        _ = sampler.SetIntent("right_turn", 4, horizon);
        _ = sampler.Targets(4, horizon, facing, ground);
        Assert.Equal(1156, sampler.Selection.WindowStart);
        _ = sampler.SetIntent("forward", 5, horizon);
        Transform3D[][] travel = sampler.Targets(5, horizon, facing, ground);
        Assert.Equal(58, sampler.Selection.SourceFrame);
        Assert.Equal(107, sampler.Selection.WindowStart);
        var unchangedTravel = new ReviewedRoleSampler(set, data);
        _ = unchangedTravel.SetIntent("forward", 5);
        Transform3D[][] expectedTravel = unchangedTravel.Targets(5, horizon, facing, ground);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expectedTravel[i], travel[i]);
        }
    }
}
