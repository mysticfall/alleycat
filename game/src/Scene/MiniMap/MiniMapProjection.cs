using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// World-to-image projection shared by every fragment in one render: the world X/Z viewport, the uniform
/// pixels-per-metre scale, and the glyph pipeline. <c>+X</c> maps right and <c>+Z</c> maps down; the map never rotates.
/// Also carries the render's diagnostics so fragment builders can report transient skips.
/// </summary>
/// <param name="viewport">World X/Z rectangle mapped to the image.</param>
/// <param name="scale">Uniform pixels-per-metre scale.</param>
/// <param name="imageSize">Output image size in pixels.</param>
/// <param name="diagnostics">Diagnostic sink shared by the whole render.</param>
public sealed class MiniMapProjection(Rect2 viewport, float scale, Vector2I imageSize, List<MiniMapDiagnostic> diagnostics)
{
    /// <summary>World X/Z rectangle mapped to the image.</summary>
    public Rect2 Viewport => viewport;

    /// <summary>Uniform pixels-per-metre scale.</summary>
    public float Scale => scale;

    /// <summary>Output image size in pixels.</summary>
    public Vector2I ImageSize => imageSize;

    /// <summary>Projects an absolute world X/Z position to image pixels; <c>+X</c> right, <c>+Z</c> down.</summary>
    /// <param name="world">World X/Z position.</param>
    /// <returns>Image pixel position.</returns>
    public Vector2 WorldToPixel(Vector2 world) => (world - viewport.Position) * scale;

    /// <summary>
    /// Writes single-line text as filled glyph paths at image-pixel coordinates. Returns an empty fragment when the
    /// text is empty or whitespace; callers decide placement. Unsupported glyphs are reported as a diagnostic and the
    /// text is omitted rather than silently emptied.
    /// </summary>
    /// <param name="text">Text to write.</param>
    /// <param name="pixel">Anchor pixel position; glyphs are horizontally centred on it.</param>
    /// <param name="size">Glyph pixel size.</param>
    /// <param name="fill">Opaque <c>#RRGGBB</c> fill colour.</param>
    /// <param name="reference">Diagnostic reference naming the element when glyphs are unsupported.</param>
    /// <returns>Self-contained glyph-path group, or an empty string.</returns>
    public string WriteText(string text, Vector2 pixel, int size, string fill, string reference)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            Vector2 measured = MiniMapGlyphs.Measure(text, size);
            float x = pixel.X - (measured.X / 2);
            float baseline = pixel.Y + (size * 0.36f);
            return MiniMapGlyphs.Write(text, x, baseline, size, fill);
        }
        catch (InvalidOperationException exception)
        {
            Report(new MiniMapDiagnostic("UnsupportedGlyph", reference, exception.Message));
            return string.Empty;
        }
    }

    /// <summary>Records one diagnostic for this render; diagnostics never change the SVG bytes.</summary>
    /// <param name="diagnostic">Diagnostic to record.</param>
    public void Report(MiniMapDiagnostic diagnostic) => diagnostics.Add(diagnostic);

    /// <summary>Snapshot of the diagnostics collected so far, deterministically ordered.</summary>
    /// <returns>Ordered diagnostics.</returns>
    public ImmutableArray<MiniMapDiagnostic> CollectDiagnostics() =>
        [.. diagnostics
            .OrderBy(item => item.Reference, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)];
}
