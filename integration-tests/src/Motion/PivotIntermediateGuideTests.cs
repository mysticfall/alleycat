using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Non-generating preflight for the user-authorised staged pivot diagnostic.</summary>
public sealed class PivotIntermediateGuideTests
{
    /// <summary>Rejects the root-only alignment confound before any native inference.</summary>
    [Fact]
    [Headless]
    public void SourceAndIdlePlacement_MatchesEncodedConditionsBeforeInference()
    {
        if (System.Environment.GetEnvironmentVariable("PLAYER_PIVOT_INTERMEDIATE_PREFLIGHT") != "1")
        {
            return;
        }
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var guides = new PivotIntermediateGuides(
            ProjectSettings.GlobalizePath("res://temp/motionbricks/player-pivot-guide-01/pivot-poses.json"), data,
            ProjectSettings.GlobalizePath("res://data/motionbricks/model-frames.json"));
        var results = new List<object>();
        foreach (int start in PivotIntermediateProbeSession.Starts)
        {
            (Transform3D[][] idle, Transform3D[][] pivot) = guides.Pair(data.Idle[0], start);
            Assert.Equal(4, idle.Length);
            results.Add(new
            {
                startFrame = start + 1,
                comparison = guides.Verify(idle, pivot),
                idle = idle.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
                pivot = pivot.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray()
            });
        }
        File.WriteAllText(ProjectSettings.GlobalizePath("res://temp/motionbricks/player-pivot-intermediate-01/preflight.json"),
            JsonSerializer.Serialize(results));
    }
}
