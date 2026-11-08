using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Authoring base for one authored minimap shape primitive (SCN-002 TR-6): geometry is authored in local X/Z metres
/// and composed through the node's world transform, so yaw, tilt, non-uniform and negative (mirroring) scale all
/// project; only non-finite transforms are rejected (TR-8). The authored local position of the node under its marker
/// is the primitive's offset from the marker origin. Fill colours render opaque; the alpha channel is not used.
/// </summary>
[Tool]
public abstract partial class MiniMapPrimitive : Node3D, IMiniMapElement
{
    /// <summary>Neutral opaque default fill; every legend colour is authored per primitive.</summary>
    [ExportGroup("Style")]
    [Export]
    public Color FillColour
    {
        get;
        set;
    } = new("a5adb3");

    /// <summary>World-space X/Z footprint polygon in metres for authoring gizmos; empty when authoring is invalid.</summary>
    /// <returns>Outline points, or an empty array when the authored state is invalid.</returns>
    public Vector3[] GetFootprintOutline()
    {
        try
        {
            Transform3D transform = GlobalTransform;
            return MiniMapMath.IsFinite(transform)
                ? [.. GetLocalOutline().Select(point => transform * new Vector3(point.X, 0, point.Y))]
                : [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Live-node convenience used by authoring tooling: captures this primitive as it currently stands, resolving an
    /// identity flavour from the primitive's own ancestry. Without a renderer scope, diagnostics fall back to full
    /// node paths and any editor notice carries no scripted-ancestor clause (TR-16). The renderer always composes
    /// marker-captured snapshots, which resolve identity from the marker's ancestry per TR-7.
    /// </remarks>
    public string BuildSvgFragment(MiniMapProjection projection) => CaptureSnapshot(
        MiniMapCaptureContext.Authoring,
        GetPath().ToString(),
        new MiniMapIdentityCapture(ResolveOwnIdentity(), Engine.IsEditorHint(), ScriptedAncestor: null)).BuildSvgFragment(projection);

    /// <summary>Captures this primitive's projected world X/Z description for one render.</summary>
    /// <param name="context">Capture context whose scope root diagnostic node paths are relative to (TR-16).</param>
    /// <param name="markerPath">Scope-relative owning marker path used in authored-failure diagnostics.</param>
    /// <param name="identity">Identity resolved from the marker's nearest identifiable ancestor, or preview-notice data (TR-7).</param>
    /// <param name="markerOverride">TR-19 override matching the marker's resolved <c>FullId</c>, or <c>null</c> when
    /// the marker does not match any supplied override.</param>
    /// <returns>Immutable element snapshot for composition.</returns>
    internal IMiniMapElement CaptureSnapshot(
        MiniMapCaptureContext context,
        string markerPath,
        MiniMapIdentityCapture identity,
        MiniMapMarkerOverride? markerOverride = null)
    {
        string primitivePath = context.PathFor(this);
        Transform3D transform = GlobalTransform;
        return MiniMapMath.IsFinite(transform)
            ? CaptureValidated(markerPath, primitivePath, identity, markerOverride, transform)
            : throw new MiniMapRenderException("InvalidTransform", markerPath, $"Primitive '{primitivePath}' has a non-finite transform.");
    }

    /// <summary>Shape-specific capture after transform validation.</summary>
    /// <param name="markerPath">Scope-relative owning marker path used in diagnostics.</param>
    /// <param name="primitivePath">Scope-relative path of this primitive used in diagnostics.</param>
    /// <param name="identity">Identity resolved from the marker's ancestry, or preview-notice data (TR-7).</param>
    /// <param name="markerOverride">TR-19 override matching the marker's resolved <c>FullId</c>, or <c>null</c> when
    /// none applies.</param>
    /// <param name="worldTransform">Validated world transform of this primitive.</param>
    /// <returns>Immutable element snapshot for composition.</returns>
    protected abstract IMiniMapElement CaptureValidated(
        string markerPath,
        string primitivePath,
        MiniMapIdentityCapture identity,
        MiniMapMarkerOverride? markerOverride,
        Transform3D worldTransform);

    /// <summary>Validated local X/Z outline in metres; throws <see cref="InvalidOperationException" /> on invalid authoring.</summary>
    /// <returns>Local outline points.</returns>
    protected abstract Vector2[] GetLocalOutline();

    /// <summary>Opaque SVG fill for the authored colour.</summary>
    /// <returns><c>#RRGGBB</c> fill string.</returns>
    protected string Fill() => "#" + FillColour.ToHtml(false);

    /// <summary>
    /// Opaque SVG fill honouring a TR-19 marker override: the override colour replaces the authored fill of every
    /// primitive of a matching marker — shapes and label glyphs alike — while geometry is untouched.
    /// </summary>
    /// <param name="markerOverride">Override matching the marker's resolved <c>FullId</c>, or <c>null</c>.</param>
    /// <returns>The override's <c>#RRGGBB</c> fill, or the authored fill when no override colour is set.</returns>
    protected string Fill(MiniMapMarkerOverride? markerOverride) =>
        markerOverride?.Colour is { } colour ? "#" + colour.ToHtml(false) : Fill();

    /// <summary>Projects a local X/Z outline through a world transform onto the world X/Z plane.</summary>
    /// <param name="local">Local outline points.</param>
    /// <param name="worldTransform">World transform to apply.</param>
    /// <returns>World X/Z polygon points.</returns>
    protected static Vector2[] ProjectToWorld(Vector2[] local, Transform3D worldTransform)
        => [.. local.Select(point =>
        {
            Vector3 world = worldTransform * new Vector3(point.X, 0, point.Y);
            return new Vector2(world.X, world.Z);
        })];

    private string? ResolveOwnIdentity()
    {
        for (Node? current = GetParent(); current is not null; current = current.GetParent())
        {
            if (current is Core.IIdentifiable identifiable)
            {
                return identifiable.FullId;
            }
        }

        return null;
    }
}
