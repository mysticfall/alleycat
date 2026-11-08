using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Circle primitive with an authored positive radius in metres (SCN-002 TR-6). The captured footprint uses a fixed
/// deterministic 128-segment outline; under non-uniform positive scale it projects as an ellipse, and under negative
/// scale it projects mirrored geometry.
/// </summary>
[Tool]
[GlobalClass]
public partial class MiniMapCircle : MiniMapPrimitive
{
    /// <summary>Number of outline segments; fixed so identical state always captures identical footprints.</summary>
    internal const int SegmentCount = 128;

    /// <summary>Radius in metres; must be finite and positive.</summary>
    [ExportGroup("Geometry")]
    [Export]
    public float Radius
    {
        get;
        set;
    } = 0.5f;

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

    private Vector2[] ValidatedLocalOutline(string markerPath, string primitivePath) => HasValidRadius()
        ? [.. Enumerable.Range(0, SegmentCount).Select(index => Vector2.FromAngle(MathF.Tau * index / SegmentCount) * Radius)]
        : throw InvalidSize(markerPath, primitivePath);

    private bool HasValidRadius() => float.IsFinite(Radius) && Radius > 0;

    private MiniMapRenderException InvalidSize(string markerPath, string primitivePath) => new("InvalidSize", markerPath,
        $"Primitive '{primitivePath}': circle Radius must be finite and positive; authored Radius={Radius}.");
}
