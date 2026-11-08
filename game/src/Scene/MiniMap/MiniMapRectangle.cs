using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Centred rectangle primitive: authored positive width (local X) and depth (local Z) in metres, centred on the
/// primitive's local origin (SCN-002 TR-6). Under non-uniform, tilted or mirrored ancestry it projects like any
/// affine transform of the rectangle.
/// </summary>
[Tool]
[GlobalClass]
public partial class MiniMapRectangle : MiniMapPrimitive
{
    /// <summary>Width in local X metres; must be finite and positive.</summary>
    [ExportGroup("Geometry")]
    [Export]
    public float Width
    {
        get;
        set;
    } = 1;

    /// <summary>Depth in local Z metres; must be finite and positive.</summary>
    [Export]
    public float Depth
    {
        get;
        set;
    } = 1;

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
        ? [new(-Width / 2, -Depth / 2), new(Width / 2, -Depth / 2), new(Width / 2, Depth / 2), new(-Width / 2, Depth / 2)]
        : throw InvalidSize(markerPath, primitivePath);

    private bool HasValidSizes() => float.IsFinite(Width) && Width > 0 && float.IsFinite(Depth) && Depth > 0;

    private MiniMapRenderException InvalidSize(string markerPath, string primitivePath) => new("InvalidSize", markerPath,
        $"Primitive '{primitivePath}': rectangle Width and Depth must be finite and positive; authored Width={Width}, Depth={Depth}.");
}
