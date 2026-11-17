using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Non-generating preflight for the explicitly authorised strong cue.</summary>
public sealed class StrongStepGuideTests
{
    /// <summary>Proves matched stationary conditions and preserves independent source foot heights.</summary>
    [Fact]
    [Headless]
    public void StationaryPlacement_PreservesRaisedFootAndMatchesConditions()
    {
        if (System.Environment.GetEnvironmentVariable("PLAYER_STRONG_STEP_PREFLIGHT") != "1")
        {
            return;
        }
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var guides = new StrongStepGuides();
        (Transform3D[][] idle, Transform3D[][] step) = guides.Pair(data.Idle[0]);
        Assert.Equal(4, step.Length);
        object comparison = guides.Verify(idle, step);
        string directory = ProjectSettings.GlobalizePath("res://temp/motionbricks/player-strong-step-probe-01");
        File.WriteAllText(Path.Combine(directory, "preflight.json"), JsonSerializer.Serialize(new
        {
            comparison,
            idle = idle.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            candidate = step.Select(p => p.Select(PivotIntermediateGuides.Flatten).ToArray()).ToArray(),
            idleWorld = guides.ModelWorld(idle),
            candidateWorld = guides.ModelWorld(step)
        }));
        File.WriteAllText(Path.Combine(directory, "idle-skin-input.json"), JsonSerializer.Serialize(guides.SkinData(idle)));
        File.WriteAllText(Path.Combine(directory, "candidate-skin-input.json"), JsonSerializer.Serialize(guides.SkinData(step)));
    }
}
