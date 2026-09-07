using AlleyCat.Testing.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NAV001Stage4ManualPlaytestSessionTests
{
    [Fact]
    public void Presets_RetainExactStage4IdentityAndUniqueKeys()
    {
        Assert.Equal(5, NAV001Stage4DiagnosticPresets.All.Count);
        Assert.Equal(5, NAV001Stage4DiagnosticPresets.All.Select(preset => preset.Key).Distinct().Count());
        AssertPreset("terminal-slide", new(0.48621222f, 0f, -1.419342f), -0.33003297f, new(0.32407424f, 0f, -0.9460317f));
        AssertPreset("oblique", new(-0.49705806f, 0f, -0.61596584f), 0.6789687f, new(-0.62799084f, 0f, -0.7782208f));
        AssertPreset("settlement", new(-0.09192729f, 0f, -1.8887208f), 0.048633344f, new(-0.048614178f, 0f, -0.9988177f));
        AssertPreset("original-exact", new(-0.7990171f, 0f, -1.8826954f), 0.40136284f, new(-0.39067325f, 0f, -0.9205294f));
        AssertPreset("mirrored-finite", new(0.7990171f, 0f, -1.8826954f), -0.40136284f, new(0.39067325f, 0f, -0.9205294f));
    }

    [Fact]
    public void Session_SelectionAndFreshRunTransitionsFailClosed()
    {
        var session = new NAV001Stage4ManualPlaytestSession();
        session.Select("oblique", NAV001ManualContactMode.Neutralised);
        session.BeginFresh();

        Assert.Equal(1, session.RunNumber);
        Assert.Equal(NAV001ManualRunState.Loading, session.State);
        _ = Assert.Throws<InvalidOperationException>(() => session.Select("settlement", NAV001ManualContactMode.Active));

        session.MarkReady();
        session.MarkRunning();
        session.MarkCompleted();
        session.Reset();

        Assert.Equal(NAV001ManualRunState.Idle, session.State);
        Assert.Equal("oblique", session.Preset.Key);
        Assert.Equal(NAV001ManualContactMode.Neutralised, session.ContactMode);
        session.BeginFresh();
        Assert.Equal(2, session.RunNumber);
    }

    [Fact]
    public void HumanPerceptualResults_CannotImplicitlyRepresentObjectivePass()
    {
        Assert.Equal(
            ["Unrecorded", "Improved", "Unchanged", "Worse"],
            Enum.GetNames<NAV001HumanPerceptualResult>());
        Assert.DoesNotContain("Pass", Enum.GetNames<NAV001HumanPerceptualResult>());
    }

    private static void AssertPreset(string key, Vector3 destination, float yaw, Vector3 facing)
    {
        NAV001Stage4DiagnosticPreset preset = NAV001Stage4DiagnosticPresets.Require(key);
        Assert.Equal(destination, preset.Destination);
        Assert.Equal(yaw, preset.YawRadians);
        Assert.Equal(facing, preset.Facing);
        Assert.True(preset.Facing.IsNormalized());
    }
}
