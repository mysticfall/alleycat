using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>Label text flavour (SCN-002 TR-7).</summary>
public enum MiniMapLabelFlavour
{
    /// <summary>Displays the authored <see cref="MiniMapLabel.Text" /> property.</summary>
    Configured = 0,

    /// <summary>
    /// Displays the canonical <c>FullId</c> of the nearest <c>IIdentifiable</c> ancestor of the owning marker,
    /// resolved at capture time; the marker node itself is not consulted.
    /// </summary>
    Identity = 1
}

/// <summary>
/// Label primitive (SCN-002 TR-7). The authored local offset from the marker origin is the node's authored position
/// under the marker (map up is −Z). Text renders as filled glyph paths in image pixels at the authored
/// <see cref="TextSize" />, so labels stay legible at every map scale. Empty or whitespace-only configured text
/// renders no glyphs and is legal. Identity text with no identifiable ancestor, and glyphs the bundled Inter font
/// does not cover — non-Latin FullIds are legitimate — are diagnosed and omitted, never invented or transliterated.
/// There is no automatic placement, collision avoidance or truncation.
/// </summary>
[Tool]
[GlobalClass]
public partial class MiniMapLabel : MiniMapPrimitive
{
    /// <summary>Maximum supported glyph size in pixels; authored sizes beyond this fail loudly.</summary>
    internal const int MaximumTextSize = 512;

    /// <summary>Which text this label displays.</summary>
    [ExportGroup("Text")]
    [Export]
    public MiniMapLabelFlavour Flavour
    {
        get;
        set;
    }

    /// <summary>Displayed text when <see cref="Flavour" /> is <see cref="MiniMapLabelFlavour.Configured" />.</summary>
    [Export]
    public string Text
    {
        get;
        set;
    } = string.Empty;

    /// <summary>Glyph size in image pixels; must be finite and positive.</summary>
    [Export]
    public int TextSize
    {
        get;
        set;
    } = 16;

    /// <inheritdoc />
    protected override IMiniMapElement CaptureValidated(
        string markerPath,
        string primitivePath,
        MiniMapIdentityCapture identity,
        MiniMapMarkerOverride? markerOverride,
        Transform3D worldTransform)
    {
        if (TextSize is <= 0 or > MaximumTextSize)
        {
            throw new MiniMapRenderException("InvalidSize", markerPath,
                $"Primitive '{primitivePath}': label TextSize must be positive and at most {MaximumTextSize} pixels; authored TextSize={TextSize}.");
        }

        // Flavour decides the default text; a TR-19 LabelText override wins over both flavours. Only markers with a
        // resolved FullId can match an override, so an overridden label never reaches the MissingIdentity branch.
        string? text = markerOverride?.LabelText ?? (Flavour == MiniMapLabelFlavour.Identity ? identity.FullId : Text);
        Vector3 origin = worldTransform.Origin;
        return new MiniMapLabelSnapshot(markerPath, primitivePath, Fill(markerOverride), new Vector2(origin.X, origin.Z), text, TextSize,
            identity.EditorToolContext, identity.ScriptedAncestor);
    }

    /// <inheritdoc />
    protected override Vector2[] GetLocalOutline()
    {
        // Labels have no world footprint; gizmos show a small anchor diamond at the authored offset.
        const float Half = 0.25f;
        return [new(0, -Half), new(Half, 0), new(0, Half), new(-Half, 0)];
    }
}
