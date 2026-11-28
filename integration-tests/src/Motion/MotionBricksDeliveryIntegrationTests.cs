using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Current supported-rig delivery contracts, independent of obsolete body AnimationTree states.</summary>
[Headless]
public sealed class MotionBricksDeliveryIntegrationTests
{
    /// <summary>Local model setup resolves both defaults and explicit overrides before native worker IO.</summary>
    [Fact]
    public void ModelPath_GlobalisesDefaultAndHonoursNonBlankOverride()
    {
        using var navigation = new MotionBricksNavigation();
        const string expected = "res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx";
        Assert.Equal(expected, navigation.ModelPath);
        foreach (string? value in new string?[] { null, "", "  " })
        {
            Assert.Equal(ProjectSettings.GlobalizePath(expected), navigation.ResolveModelPath(value));
        }
        navigation.ModelPath = "res://models/custom.onnx";
        Assert.Equal(ProjectSettings.GlobalizePath(navigation.ModelPath), navigation.ResolveModelPath(null));
        Assert.Equal(ProjectSettings.GlobalizePath("res://models/override.onnx"),
            navigation.ResolveModelPath("res://models/override.onnx"));
        string absolute = ProjectSettings.GlobalizePath(expected);
        Assert.Equal(absolute, navigation.ResolveModelPath(absolute));
    }

    /// <summary>Absent weights fail clearly before native session construction, without fallback.</summary>
    [Fact]
    public void MissingModel_ReportsResolvedPathBeforeNativeLoad()
    {
        string missing = ProjectSettings.GlobalizePath("res://models/motionbricks/missing-model-test.onnx");
        bool nativeEntered = false;
        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => SomaNativeSession.Load(missing, _ => nativeEntered = true));
        Assert.Equal(missing, error.FileName);
        Assert.Contains("SOMA FP32 model is missing", error.Message);
        Assert.False(nativeEntered);
    }

    /// <summary>NPC navigation is authored exactly once; the player role is not silently given generated movement.</summary>
    [Fact]
    public void SupportedRole_AuthorsGeneratedNavigationWithoutPlayerLocomotion()
    {
        Node npc = GD.Load<PackedScene>("res://assets/characters/templates/reference_female/reference_female_npc.tscn").Instantiate();
        Node player = GD.Load<PackedScene>("res://assets/characters/templates/reference_female/reference_female_player.tscn").Instantiate();
        try
        {
            AlleyCat.Character.Character character = Assert.IsAssignableFrom<AlleyCat.Character.Character>(npc);
            MotionBricksNavigation navigation = Assert.IsType<MotionBricksNavigation>(character.Navigation);
            Assert.Same(navigation, Assert.Single(npc.GetChildren().OfType<NavigationBase>()));
            Assert.IsNotAssignableFrom<AlleyCat.Control.Locomotion.ILocomotive>(character);
            Assert.Empty(player.GetChildren().OfType<MotionBricksNavigation>());
        }
        finally { npc.Free(); player.Free(); }
    }

    /// <summary>
    /// Delivery loads the real calibration paths without any diagnostic fixture: the poses export carries the canonical
    /// skeleton only, with no clip or prototype walking source, and the named model frames resolve against it.
    /// </summary>
    [Fact]
    public void RuntimeData_LoadsCanonicalSkeletonAndNamedModelFrames()
    {
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
        _ = new SomaModelFrames(data, ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/model-frames.json"));
        Assert.False(data.Manifest.TryGetProperty("clips", out _));
        Assert.False(data.Manifest.GetProperty("sources").TryGetProperty(
            "game/assets/characters/animations/source/locomotion/locomotion.blend", out _));
        Assert.All(["LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase"], n => Assert.Contains(n, data.Names));
    }
}
