using System.Collections.Immutable;
using AlleyCat.Core;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// One minimap entity (SCN-002 TR-2): a <see cref="Node3D" /> that parents into the scene wherever its creator wants
/// it, owns an explicitly authored ordered collection of <see cref="MiniMapPrimitive" />s, and self-registers into the
/// <see cref="GroupName" /> scene group on every tree entry (<see cref="_EnterTree" />) so the <see cref="MiniMapRenderer" /> discovers it by
/// live group query. Characters receive no special handling: a marker hosted under a character is an ordinary marker
/// (UR-2). An empty primitive collection is legal and renders no fragments.
/// </summary>
[Tool]
[GlobalClass]
public partial class MiniMapMarker : Node3D, IMiniMapElement
{
    /// <summary>Scene group every marker joins; membership follows scene-tree lifecycle (pattern: IHearing.GroupName).</summary>
    public const string GroupName = "minimap_markers";

    /// <summary>
    /// Authored draw-order ordinal (SCN-002 TR-2): markers paint in ascending ordinal, tie-broken by scene node path
    /// (TR-12). Later markers may cover earlier ones; overlap is creator-controlled.
    /// </summary>
    [ExportGroup("Painting")]
    [Export]
    public int DrawOrder
    {
        get;
        set;
    }

    /// <summary>
    /// Explicitly authored, ordered primitive collection. Duplicate references are authoring errors and fail the
    /// render loudly; entries are typically child nodes whose authored local positions offset them from the marker
    /// origin.
    /// </summary>
    [Export]
    public MiniMapPrimitive[] Primitives
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Registers this marker for discovery on every tree entry — <c>_EnterTree</c> fires on the first add and on each
    /// later re-entry (for example reparenting), keeping membership aligned with scene-tree lifecycle; adding to a
    /// group is idempotent.
    /// </summary>
    public override void _EnterTree() => AddToGroup(GroupName);

    /// <summary>Deregisters this marker.</summary>
    public override void _ExitTree() => RemoveFromGroup(GroupName);

    /// <inheritdoc />
    /// <remarks>Live-node convenience used by authoring tooling; the renderer composes captured snapshots (TR-11).
    /// Without a renderer scope, diagnostics fall back to full node paths (TR-16).</remarks>
    public string BuildSvgFragment(MiniMapProjection projection)
        => CaptureSnapshot(MiniMapCaptureContext.Authoring).BuildSvgFragment(projection);

    /// <summary>
    /// Captures this marker's current state into an immutable snapshot, resolving identity from the marker's
    /// ancestry first: validated transform, identity text resolved from the nearest <see cref="IIdentifiable" />
    /// ancestor (the marker itself is not consulted, TR-7), the TR-19 override matching that identity, and the
    /// ordered primitive element snapshots. Diagnostic node paths are relative to the context's scope root (TR-16).
    /// Throws <see cref="MiniMapRenderException" /> on authored errors so the render fails loudly (TR-16).
    /// </summary>
    /// <param name="context">Capture context carrying the diagnostic scope root, editor-tool discriminator and
    /// per-request marker style overrides (TR-19).</param>
    /// <returns>Immutable marker snapshot.</returns>
    internal MiniMapMarkerSnapshot CaptureSnapshot(MiniMapCaptureContext context) => CaptureSnapshot(context, ResolveIdentity(context));

    /// <summary>
    /// Captures this marker's current state with a pre-resolved identity — the single-resolution path the renderer
    /// uses when overrides are supplied, because its unused-key pass already resolved every discovered marker's
    /// identity before the distance filter (TR-19). The TR-19 override matching the resolved <c>FullId</c> restyles
    /// every primitive of this marker; overrides never change geometry, extent, inclusion or ordering.
    /// </summary>
    /// <param name="context">Capture context carrying the diagnostic scope root, editor-tool discriminator and
    /// per-request marker style overrides (TR-19).</param>
    /// <param name="identity">Identity resolved from the marker's nearest identifiable ancestor, or preview-notice
    /// data (TR-7).</param>
    /// <returns>Immutable marker snapshot.</returns>
    internal MiniMapMarkerSnapshot CaptureSnapshot(MiniMapCaptureContext context, MiniMapIdentityCapture identity)
    {
        string path = context.PathFor(this);
        Transform3D transform = GlobalTransform;
        if (!MiniMapMath.IsFinite(transform))
        {
            throw new MiniMapRenderException("InvalidTransform", path, $"Marker '{path}' has a non-finite transform.");
        }

        MiniMapMarkerOverride? markerOverride = context.ResolveOverride(identity.FullId);
        ImmutableArray<IMiniMapElement>.Builder elements = ImmutableArray.CreateBuilder<IMiniMapElement>();
        var seen = new HashSet<MiniMapPrimitive>();
        MiniMapPrimitive[] primitives = Primitives;
        for (int index = 0; index < primitives.Length; index++)
        {
            MiniMapPrimitive primitive = primitives[index];
            if (primitive is null || !IsInstanceValid(primitive) || primitive.IsQueuedForDeletion() || !primitive.IsInsideTree())
            {
                throw new MiniMapRenderException("MissingPrimitive", path,
                    $"Primitive slot {index} is empty, freed or outside the scene tree; assign a live primitive or remove the slot.");
            }

            if (!seen.Add(primitive))
            {
                throw new MiniMapRenderException("DuplicatePrimitive", path,
                    $"Primitive '{context.PathFor(primitive)}' is referenced more than once in the marker's collection.");
            }

            elements.Add(primitive.CaptureSnapshot(context, path, identity, markerOverride));
        }

        return new MiniMapMarkerSnapshot(path, DrawOrder, new Vector2(transform.Origin.X, transform.Origin.Z), elements.ToImmutable());
    }

    /// <summary>
    /// Resolves identity from the marker's ancestry (TR-7): the canonical <c>FullId</c> of the nearest instantiated
    /// <see cref="IIdentifiable" /> ancestor — the marker node itself is not consulted — or, in an editor-tool
    /// context with no ancestor, the preview-notice data. Internal so the renderer's TR-19 unused-key pass can
    /// resolve the identity of markers the distance filter later excludes, without capturing them.
    /// </summary>
    /// <param name="context">Capture context carrying the editor-tool discriminator and diagnostic scope root.</param>
    /// <returns>Resolved identity capture, or preview-notice data when no ancestor is identifiable.</returns>
    internal MiniMapIdentityCapture ResolveIdentity(MiniMapCaptureContext context)
    {
        for (Node? current = GetParent(); current is not null; current = current.GetParent())
        {
            if (current is IIdentifiable identifiable)
            {
                return MiniMapIdentityCapture.Resolved(identifiable.FullId);
            }
        }

        // No instantiated ancestor: at runtime this stays the MissingIdentity error (TR-16); in an editor-tool
        // context the informational notice names the nearest scripted ancestor, where identity resolves at runtime
        // (TR-7). Authored identity properties are never read to simulate identity.
        return context.EditorToolContext
            ? new MiniMapIdentityCapture(null, EditorToolContext: true, ScriptedAncestor: FindScriptedAncestor(context))
            : new MiniMapIdentityCapture(null, EditorToolContext: false, ScriptedAncestor: null);
    }

    private MiniMapScriptedAncestor? FindScriptedAncestor(MiniMapCaptureContext context)
    {
        // Mirrors the identity walk — the marker itself is never consulted — and is bounded by the scope root so the
        // notice never names nodes beyond the authored scene, such as the editor's own UI hierarchy (TR-16).
        for (Node? current = GetParent(); current is not null; current = current == context.ScopeRoot ? null : current.GetParent())
        {
            // Script presence is readable without the script running; non-instantiated C# ancestors qualify.
            if (current.Get("script").VariantType != Variant.Type.Nil)
            {
                return new MiniMapScriptedAncestor(current.Name, context.PathFor(current));
            }
        }

        return null;
    }
}
