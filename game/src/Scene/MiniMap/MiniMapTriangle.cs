using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Triangle primitive with authored positive width (base, local X) and length (local Z) in metres, apex pointing
/// along local −Z (SCN-002 TR-6). Parenting it under a rotating marker — for example on a character — renders that
/// entity's heading for free because the map itself never rotates (UR-4).
/// </summary>
[Tool]
[GlobalClass]
public partial class MiniMapTriangle : MiniMapPrimitive
{
    /// <summary>Base width in local X metres; must be finite and positive.</summary>
    [ExportGroup("Geometry")]
    [Export]
    public float Width
    {
        get;
        set;
    } = 0.8f;

    /// <summary>Point-to-base length in local Z metres; must be finite and positive.</summary>
    [Export]
    public float Length
    {
        get;
        set;
    } = 1.2f;

    /// <inheritdoc />
    protected override IMiniMapElement CaptureValidated(
        string markerPath,
        string primitivePath,
        MiniMapIdentityCapture identity,
        MiniMapMarkerOverride? markerOverride,
        Transform3D worldTransform)
        => new MiniMapShapeSnapshot(markerPath, primitivePath, Fill(markerOverride), [.. ProjectToWorld(ValidatedLocalOutline(markerPath, primitivePath), worldTransform)]);

    /// <inheritdoc />
    protected override Vector2[] GetLocalOutline()
    {
        string path = GetPath().ToString();
        return ValidatedLocalOutline(path, path);
    }

    private Vector2[] ValidatedLocalOutline(string markerPath, string primitivePath) => HasValidSizes()
        // Apex at −Length / 2 (local forward, map up) with the base at +Length / 2, centred on the local origin.
        ? [new(-Width / 2, Length / 2), new(Width / 2, Length / 2), new(0, -Length / 2)]
        : throw InvalidSize(markerPath, primitivePath);

    private bool HasValidSizes() => float.IsFinite(Width) && Width > 0 && float.IsFinite(Length) && Length > 0;

    private MiniMapRenderException InvalidSize(string markerPath, string primitivePath) => new("InvalidSize", markerPath,
        $"Primitive '{primitivePath}': triangle Width and Length must be finite and positive; authored Width={Width}, Length={Length}.");
}
