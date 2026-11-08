using System.Collections.Immutable;
using System.Text;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Immutable captured shape geometry for one render (SCN-002 TR-11): the primitive's local outline projected to
/// world X/Z through its world transform. Composition maps it into image space; a footprint that collapsed to zero
/// area from a live pose — for example a pitched marker — is skipped with a diagnostic and the render continues
/// (TR-16).
/// </summary>
/// <param name="MarkerPath">Owning marker node path, scope-relative (TR-16).</param>
/// <param name="PrimitivePath">Primitive node path, scope-relative (TR-16).</param>
/// <param name="Fill">Opaque <c>#RRGGBB</c> fill.</param>
/// <param name="Polygon">World X/Z polygon points in metres.</param>
public sealed record MiniMapShapeSnapshot(
    string MarkerPath,
    string PrimitivePath,
    string Fill,
    ImmutableArray<Vector2> Polygon) : IMiniMapElement
{
    /// <inheritdoc />
    public string BuildSvgFragment(MiniMapProjection projection)
    {
        if (Math.Abs(MiniMapMath.Area(Polygon)) < MiniMapMath.DegenerateAreaSquareMetres)
        {
            projection.Report(new MiniMapDiagnostic("DegenerateProjection", MarkerPath,
                $"Primitive '{PrimitivePath}' projected to a zero-area footprint for this pose; skipped for this render."));
            return string.Empty;
        }

        var path = new StringBuilder();
        _ = path.Append(string.Join(" ", Polygon.Select(projection.WorldToPixel)
            .Select(pixel => $"{MiniMapGlyphs.Number(pixel.X)},{MiniMapGlyphs.Number(pixel.Y)}")));
        return $"<polygon points=\"{path}\" fill=\"{Fill}\"/>";
    }
}

/// <summary>
/// Nearest ancestor node of a marker bearing an attached script (SCN-002 TR-7): where an identity label's text will
/// resolve at runtime. Captured only for the informational editor-preview notice — the ancestor's script does not run
/// in the editor, and its authored identity properties are never read to simulate identity.
/// </summary>
/// <param name="Name">Ancestor node name.</param>
/// <param name="Path">Scope-relative ancestor node path (TR-16).</param>
public sealed record MiniMapScriptedAncestor(string Name, string Path)
{
    /// <summary>Author-facing description: the quoted name, plus its scope-relative path when the two differ.</summary>
    /// <returns>Description for the preview-notice message.</returns>
    public string Describe() => Name == Path ? $"'{Name}'" : $"'{Name}' ({Path})";
}

/// <summary>
/// Immutable captured label for one render: the anchor resolved through the primitive's world transform and the text
/// resolved at capture time (configured text, or the marker ancestry's <c>FullId</c>). An identity flavour that found
/// no identifiable ancestor is omitted for that render with a diagnostic naming the marker by scope-relative node
/// path; the render continues (TR-16). Outside editor-tool contexts the diagnostic is the runtime
/// <c>MissingIdentity</c> error; in an editor-tool context (TR-7) it is the informational
/// <c>IdentityResolvesAtRuntime</c> notice explaining that the ancestor's script does not run in the editor, so the
/// label will show its identity in play mode. Empty or whitespace-only text legitimately renders no glyphs.
/// </summary>
/// <param name="MarkerPath">Owning marker node path, scope-relative (TR-16).</param>
/// <param name="PrimitivePath">Primitive node path, scope-relative (TR-16).</param>
/// <param name="Fill">Opaque <c>#RRGGBB</c> fill.</param>
/// <param name="Anchor">World X/Z anchor position in metres.</param>
/// <param name="Text">Resolved text; <c>null</c> when an identity flavour found no ancestor.</param>
/// <param name="TextSizePixels">Glyph size in image pixels.</param>
/// <param name="EditorToolContext">True when captured in an editor-tool context: an omitted identity label reports the
/// informational <c>IdentityResolvesAtRuntime</c> notice instead of the runtime <c>MissingIdentity</c> error.</param>
/// <param name="ScriptedAncestor">Nearest scripted ancestor named by the notice, when one exists in scope.</param>
public sealed record MiniMapLabelSnapshot(
    string MarkerPath,
    string PrimitivePath,
    string Fill,
    Vector2 Anchor,
    string? Text,
    int TextSizePixels,
    bool EditorToolContext = false,
    MiniMapScriptedAncestor? ScriptedAncestor = null) : IMiniMapElement
{
    /// <inheritdoc />
    public string BuildSvgFragment(MiniMapProjection projection)
    {
        if (Text is null)
        {
            projection.Report(EditorToolContext ? ResolveAtRuntime() : MissingIdentity());
            return string.Empty;
        }

        return projection.WriteText(Text, projection.WorldToPixel(Anchor), TextSizePixels, Fill, MarkerPath);
    }

    private MiniMapDiagnostic MissingIdentity() => new("MissingIdentity", MarkerPath,
        $"Identity label '{PrimitivePath}' has no IIdentifiable ancestor of its marker; omitted for this render.");

    private MiniMapDiagnostic ResolveAtRuntime() => new("IdentityResolvesAtRuntime", MarkerPath,
        ScriptedAncestor is { } ancestor
            ? $"Identity label '{PrimitivePath}' on marker '{MarkerPath}' is omitted from this editor preview: its "
                + $"identity resolves at runtime from {ancestor.Describe()}, whose script does not run in the editor. "
                + "The label will show its identity in play mode."
            : $"Identity label '{PrimitivePath}' on marker '{MarkerPath}' is omitted from this editor preview because "
                + "its identity resolves at runtime, not in the editor. The label will show its identity in play mode.");
}

/// <summary>
/// Immutable captured marker for one render (SCN-002 TR-11): scene node path, authored draw-order ordinal, world
/// origin and the ordered primitive element snapshots. Its fragment composes its primitives' fragments in authored
/// collection order (TR-5); painting order between markers is decided by the renderer (TR-12).
/// </summary>
/// <param name="NodePath">Marker node path, scope-relative to the renderer's scope root (TR-16); also the TR-12
/// ordinal tie-break key, preserved consistent per render because every marker of one render shares the scope.</param>
/// <param name="DrawOrder">Authored draw-order ordinal.</param>
/// <param name="Origin">World X/Z origin in metres.</param>
/// <param name="Primitives">Primitive snapshots in authored collection order.</param>
public sealed record MiniMapMarkerSnapshot(
    string NodePath,
    int DrawOrder,
    Vector2 Origin,
    ImmutableArray<IMiniMapElement> Primitives) : IMiniMapElement
{
    /// <inheritdoc />
    public string BuildSvgFragment(MiniMapProjection projection) =>
        string.Concat(Primitives.Select(primitive => primitive.BuildSvgFragment(projection)));
}
