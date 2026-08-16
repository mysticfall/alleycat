using Godot;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only marker whose colour distinguishes the two actual selector-owned cue targets in capture evidence.
/// </summary>
[GlobalClass]
public sealed partial class AttentionGazeTargetSelectionPhotoboothMarker : MeshInstance3D
{
    /// <summary>Gets or sets the marker colour used by the unshaded test-only target geometry.</summary>
    [Export]
    public Color MarkerColor
    {
        get; set;
    } = Colors.White;

    /// <inheritdoc />
    public override void _Ready()
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = MarkerColor,
            EmissionEnabled = true,
            Emission = MarkerColor,
        };
        MaterialOverride = material;
    }
}
