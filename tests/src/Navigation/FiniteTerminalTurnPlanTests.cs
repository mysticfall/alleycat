using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>Approved finite allocation mathematics, not generated gait quality.</summary>
public sealed class FiniteTerminalTurnPlanTests
{
    /// <summary>Source angles, rather than time fractions, determine both signed primary cue maps.</summary>
    [Fact]
    public void HalfTurnsUse120Then60AndPrimarySourceAnglesInBothDirections()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(directory.FullName, "game/assets/animations/locomotion/feminine")))
        {
            directory = directory.Parent!;
        }
        string path = Path.Combine(directory.FullName, "game/assets/animations/locomotion/feminine");
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "landing-ablation-01.json")));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "turn-angular-reference.json")));
        JsonElement samples = source.RootElement.GetProperty("samples");
        foreach ((string role, int first, int end, int a, int b, int sign) in new[] { ("left_turn", 946, 1006, 947, 961, 1), ("right_turn", 1096, 1156, 1103, 1120, -1) })
        {
            var reference = new StationaryTurnReference(role, metadata.RootElement.GetProperty(role), source.RootElement,
                "dd5837bd66f83a5117ca06d3305b5789d377f447673743961f2a198f8fd023af", first, end);
            var plan = new FiniteTerminalTurnPlan(reference, .3, sign * Math.PI);
            Assert.Equal(2, plan.Allocations.Count);
            Assert.Equal(sign * 120, plan.Allocations[0] * 180 / Math.PI, 8);
            Assert.Equal(sign * 60, plan.Allocations[1] * 180 / Math.PI, 8);
            Assert.Equal([a, b, a, b], plan.Cues.Select(c => c.Frame));
            double entry = samples[first - 1].GetProperty("heading_radians").GetDouble();
            double span = samples[end - 1].GetProperty("heading_radians").GetDouble() - entry;
            foreach (FiniteTerminalTurnPlan.Cue cue in plan.Cues)
            {
                double progress = samples[cue.Frame - 1].GetProperty("heading_radians").GetDouble() - entry;
                double allocation = sign * (cue.Cycle == 0 ? 120 : 60) * Math.PI / 180;
                double expected = .3 + (cue.Cycle == 0 ? 0 : sign * 120 * Math.PI / 180) + (allocation * progress / span);
                Assert.Equal(expected, cue.MappedWorldYaw, 8);
                Assert.Equal(cue.Frame + (cue.Cycle * 60), cue.VirtualFrame);
                Assert.False(plan.FinishedCues);
                plan.Service();
            }
            Assert.True(plan.FinishedCues);
            var small = new FiniteTerminalTurnPlan(reference, .3, sign * .1);
            _ = Assert.Single(small.Allocations);
            Assert.Equal(2, small.Cues.Count);
        }
    }
}
