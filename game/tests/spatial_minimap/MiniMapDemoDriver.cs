using AlleyCat.Scene.MiniMap;
using Godot;

namespace AlleyCat.Testing;

/// <summary>
/// SCN-002 TR-18 demonstration driver. Resolves the production <see cref="MiniMapRenderer" /> through CORE-004
/// dependency injection, renders the whole authored coverage, a distance-filtered close request, the same request
/// after the demo character moves and rotates, and a TR-19 per-request override of that close-up restyling the
/// character's marker gold with a "You" label, saves every SVG/PNG pair under
/// <c>res://temp/minimap-redesign/</c>, and mirrors the latest image on the demo overlay. Prints a
/// MINIMAP_DEMO_RENDER line per render and MINIMAP_DEMO_COMPLETE before quitting.
/// </summary>
[GlobalClass]
public partial class MiniMapDemoDriver : Node3D
{
    private const string OutputDirectory = "res://temp/minimap-redesign";

    /// <summary>Live demo character (<c>ICharacter</c>, verified at runtime) carrying an authored marker child.</summary>
    [Export]
    public Node3D? DemoCharacter
    {
        get;
        set;
    }

    /// <summary>
    /// Runs the demonstration once the scene tree is live; markers that registered on tree entry (<c>_EnterTree</c>)
    /// are discovered by group membership, never by this driver.
    /// </summary>
    public override void _Ready() => Callable.From(RunScenario).CallDeferred();

    private async void RunScenario()
    {
        for (int index = 0; index < 3; index++)
        {
            _ = await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        MiniMapRenderer? renderer = Game.Instance.GetService<MiniMapRenderer>();
        if (renderer is null || DemoCharacter is not { } character || character is not Character.ICharacter)
        {
            GD.PushError("MINIMAP_DEMO_FAILED: MiniMapRenderer is not registered or DemoCharacter is not a live ICharacter.");
            GetTree().Quit(1);
            return;
        }

        _ = DirAccess.MakeDirRecursiveAbsolute(OutputDirectory);

        // Whole authored coverage: every marker, unbounded inclusion, coarse adaptive grid.
        MiniMapRenderResult wholeCoverage = renderer.RenderCoverage(new MiniMapRenderSettings(Width: 1280, Height: 800));
        Report(wholeCoverage, "demo_whole_coverage");

        // Close request around the character: 8 m inclusion — the far beacon is excluded, grid adapts to 2 m steps.
        Vector2 start = new(character.GlobalPosition.X, character.GlobalPosition.Z);
        MiniMapRenderSettings closeSettings = new(MaximumDistance: 8f, Width: 1280, Height: 800);
        Report(renderer.Render(start, closeSettings), "demo_close_filtered");

        // The character translates and rotates; the marker rides its transform, so the next request shows the moved
        // origin, the rotated heading triangle and the identity label following it — the map itself never rotates.
        character.GlobalTransform = new Transform3D(
            Basis.FromEuler(new Vector3(0, Mathf.DegToRad(140), 0)),
            character.GlobalPosition + new Vector3(3, 0, 1));
        _ = await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Report(renderer.Render(new Vector2(character.GlobalPosition.X, character.GlobalPosition.Z), closeSettings), "demo_close_filtered_moved");

        // TR-19 per-request override: the same moved close-up request with the subject character restyled — her
        // whole marker recoloured gold and relabelled "You" — without touching the scene. Geometry, extent, framing,
        // draw order and grid are those of the plain request above; a later plain request shows the authored style.
        MiniMapRenderSettings overrideSettings = new(
            MaximumDistance: 8f,
            Width: 1280,
            Height: 800,
            MarkerOverrides: new Dictionary<string, MiniMapMarkerOverride>
            {
                ["char:alice"] = new(Colour: new Color("d4af37"), LabelText: "You"),
            });
        Report(renderer.Render(new Vector2(character.GlobalPosition.X, character.GlobalPosition.Z), overrideSettings), "demo_close_filtered_overridden");

        GD.Print("MINIMAP_DEMO_COMPLETE");
        GetTree().Quit();
    }

    private void Report(MiniMapRenderResult result, string name)
    {
        GD.Print($"MINIMAP_DEMO_RENDER {name} status={result.Status} viewport={result.Viewport} "
            + $"scale={result.Scale:0.##} grid_step={result.GridStep} diagnostics={result.Diagnostics.Length}");
        foreach (MiniMapDiagnostic diagnostic in result.Diagnostics)
        {
            GD.Print($"MINIMAP_DEMO_DIAGNOSTIC {name} {diagnostic.Code} [{diagnostic.Reference}]: {diagnostic.Message}");
        }

        if (result is not { Status: MiniMapRenderStatus.Ready, Svg: { } svg, Image: { } image })
        {
            GD.PushError($"MINIMAP_DEMO_RENDER_FAILED {name}: {string.Join("; ", result.Diagnostics.Select(item => item.Message))}");
            return;
        }

        _ = image.SavePng($"{OutputDirectory}/{name}.png");
        using var file = Godot.FileAccess.Open($"{OutputDirectory}/{name}.svg", Godot.FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PushError($"MINIMAP_DEMO_RENDER_FAILED {name}: cannot write SVG evidence artefact.");
            return;
        }

        _ = file.StoreString(svg);
        file.Close();

        if (GetNodeOrNull<TextureRect>("%DemoPreview") is { } preview)
        {
            preview.Texture = ImageTexture.CreateFromImage(image);
        }
    }
}
