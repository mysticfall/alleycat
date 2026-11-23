using System.Text.Json.Nodes;
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
    /// <summary>SA-B01: normal idle/calibration startup must not require any prototype walking clip or stage.</summary>
    [Fact]
    public void RuntimeData_IdleOnlyCalibrationAndCatalogueNeedNoPrototypeWalk()
    {
        string runtime = ProjectSettings.GlobalizePath("res://data/motionbricks");
        JsonObject poses = JsonNode.Parse(File.ReadAllText(Path.Combine(runtime, "poses.json")))!.AsObject();
        _ = poses["clips"]!.AsObject().Remove("walk");
        _ = poses["sources"]!.AsObject().Remove("game/assets/characters/animations/source/locomotion/locomotion.blend");
        JsonObject catalogue = JsonNode.Parse(File.ReadAllText(Path.Combine(runtime, "catalogue.json")))!.AsObject();
        JsonArray stages = catalogue["variants"]![0]!["stages"]!.AsArray();
        foreach (JsonNode? stage in stages.Where(s => s!["action"]!.GetValue<string>() == "walk").ToArray())
        {
            _ = stages.Remove(stage);
        }
        string directory = ProjectSettings.GlobalizePath("res://temp/motionbricks/anim002-regenerated-source-adoption-01/blocker-fix-01/idle-only-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        string posePath = Path.Combine(directory, "poses.json"), cataloguePath = Path.Combine(directory, "catalogue.json");
        File.WriteAllText(posePath, poses.ToJsonString());
        File.WriteAllText(cataloguePath, catalogue.ToJsonString());
        var data = new SomaMotionData(posePath);
        var idle = new MotionCatalogue(cataloguePath, data);
        MotionCatalogue.Stage selected = Assert.Single(idle.Stages);
        Assert.Equal("idle", selected.Action);
        Assert.Equal(0, selected.Speed);
        Assert.True(data.Idle.Length >= 4);
        Assert.Same(data.Idle, MotionCatalogue.Clip(data, selected.Action));
        _ = new SomaModelFrames(data, Path.Combine(runtime, "model-frames.json"));
    }

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

    /// <summary>Delivery loads real runtime paths and approved action windows without any diagnostic fixture.</summary>
    [Fact]
    public void RuntimeData_LoadsApprovedWindowsAndNamedModelFrames()
    {
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var catalogue = new MotionCatalogue(ProjectSettings.GlobalizePath("res://data/motionbricks/catalogue.json"), data);
        _ = new SomaModelFrames(data, ProjectSettings.GlobalizePath("res://data/motionbricks/model-frames.json"));
        Assert.Equal("idle", catalogue.Select(0).Action);
        Assert.Equal("idle", Assert.Single(catalogue.Stages).Action);
        Assert.Equal(0, catalogue.Stages[0].Speed);
        Assert.False(data.Manifest.GetProperty("clips").TryGetProperty("walk", out _));
        Assert.False(data.Manifest.GetProperty("sources").TryGetProperty(
            "game/assets/characters/animations/source/locomotion/locomotion.blend", out _));
        Assert.All(["LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase"], n => Assert.Contains(n, data.Names));
        Assert.All(catalogue.Stages, stage =>
        {
            Transform3D[][] clip = MotionCatalogue.Clip(data, stage.Action);
            Assert.InRange(stage.Entry + 3, 3, clip.Length - 1);
            Assert.InRange(stage.Exit + 3, 3, clip.Length - 1);
        });
    }
}
