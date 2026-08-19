using Godot;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only marker whose colour distinguishes gaze anchors in AI-009 orienting capture evidence.
/// </summary>
[GlobalClass]
public sealed partial class Ai009OrientingPhotoboothMarker : MeshInstance3D
{
    /// <summary>Gets or sets the marker colour used by the unshaded test-only anchor geometry.</summary>
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
