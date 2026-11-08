namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// One minimap entity or primitive as a self-contained SVG fragment (SCN-002 TR-5). Implemented by live
/// <see cref="MiniMapMarker" />/<see cref="MiniMapPrimitive" /> nodes and by the immutable per-render snapshots the
/// renderer composes from. Fragments contain only paths, groups and fills: no <c>&lt;text&gt;</c> element and no
/// reference to external fonts, resources, scripts or network resources.
/// </summary>
public interface IMiniMapElement
{
    /// <summary>
    /// Builds the SVG fragment for this element in image space under the given projection.
    /// </summary>
    /// <param name="projection">World-to-image projection carrying the glyph pipeline and diagnostic sink.</param>
    /// <returns>Self-contained SVG fragment; empty when this element legitimately renders no geometry.</returns>
    string BuildSvgFragment(MiniMapProjection projection);
}
