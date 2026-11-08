using System.Globalization;
using System.Security;
using System.Text;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Converts text to self-contained SVG glyph outline paths from the bundled quadratic TrueType Inter font.
/// Godot's SVG rasteriser does not support SVG <c>&lt;text&gt;</c>, so every rendered string — marker labels and
/// grid numbers — must become filled glyph geometry through this pipeline.
/// </summary>
public sealed class MiniMapGlyphs
{
    private const string FontPath = "res://assets/fonts/Inter/Inter_18pt-Medium.ttf";

    private static readonly MiniMapGlyphs _shared = new();

    private readonly FontFile _font = ResourceLoader.Load<FontFile>(FontPath)
        ?? throw new InvalidOperationException($"Bundled Inter font is unavailable; restore/import {FontPath}.");

    /// <summary>
    /// Measures the same unshaped glyph advances and ink extents used by <see cref="Write" />.
    /// </summary>
    /// <param name="text">Single-line text to measure.</param>
    /// <param name="size">Glyph pixel size.</param>
    /// <returns>Total advance width and ink height in pixels.</returns>
    public static Vector2 Measure(string text, int size = 16) => _shared.MeasureInstance(text, size);

    /// <summary>
    /// Converts single-line text to filled glyph geometry positioned in image pixels; never SVG text.
    /// </summary>
    /// <param name="text">Text to write.</param>
    /// <param name="x">Left pixel position of the first glyph.</param>
    /// <param name="baseline">Pixel baseline of the glyph row.</param>
    /// <param name="size">Glyph pixel size.</param>
    /// <param name="fill">Opaque <c>#RRGGBB</c> fill colour applied to every glyph path.</param>
    /// <returns>Self-contained SVG group of glyph outline paths.</returns>
    public static string Write(string text, float x, float baseline, int size, string fill) => _shared.WriteInstance(text, x, baseline, size, fill);

    private Vector2 MeasureInstance(string text, int size)
    {
        TextServer server = TextServerManager.GetPrimaryInterface();
        Rid font = FontRid();
        float advance = 0;
        float minimum = 0;
        float maximum = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            long glyph = Glyph(server, font, size, rune);
            Vector3[] points = server.FontGetGlyphContours(font, size, glyph)["points"].AsVector3Array();
            foreach (Vector3 point in points)
            {
                minimum = Math.Min(minimum, point.Y);
                maximum = Math.Max(maximum, point.Y);
            }

            advance += server.FontGetGlyphAdvance(font, size, glyph).X;
        }

        return new Vector2(advance, Math.Max(size, maximum - minimum));
    }

    private string WriteInstance(string text, float x, float baseline, int size, string fill)
    {
        TextServer server = TextServerManager.GetPrimaryInterface();
        Rid font = FontRid();
        var paths = new StringBuilder();
        _ = paths.Append($"<g fill=\"{fill}\" aria-label=\"{SecurityElement.Escape(text)}\">");
        foreach (Rune rune in text.EnumerateRunes())
        {
            long glyph = Glyph(server, font, size, rune);

            Godot.Collections.Dictionary outline = server.FontGetGlyphContours(font, size, glyph);
            Vector3[] points = outline["points"].AsVector3Array();
            int[] ends = outline["contours"].AsInt32Array();
            var path = new StringBuilder();
            int start = 0;
            foreach (int end in ends)
            {
                AppendContour(path, points[start..(end + 1)]);
                start = end + 1;
            }

            _ = paths.Append($"<path transform=\"translate({Number(x)} {Number(baseline)})\" d=\"{path}\"/>");
            x += server.FontGetGlyphAdvance(font, size, glyph).X;
        }

        _ = paths.Append("</g>");
        return paths.ToString();
    }

    private Rid FontRid() => _font.GetRids()[0];

    private static long Glyph(TextServer server, Rid font, int size, Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (Rune.IsControl(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            || rune.Value is >= 0x0590 and <= 0x08ff)
        {
            throw new InvalidOperationException($"Unsupported text shaping/control U+{rune.Value:X4}; use precomposed supported labels.");
        }

        long glyph = server.FontGetGlyphIndex(font, size, rune.Value, 0);
        return glyph != 0 ? glyph : throw new InvalidOperationException($"Unsupported glyph U+{rune.Value:X4}; choose a label supported by bundled Inter.");
    }

    private static void AppendContour(StringBuilder path, Vector3[] points)
    {
        if (points.Length == 0)
        {
            return;
        }

        // TrueType permits an off-curve start and consecutive quadratic controls.
        Vector3 first = points[0];
        Vector3 last = points[^1];
        Vector3 origin = first.Z == 1 ? first : last.Z == 1 ? last : (first + last) / 2;
        _ = path.Append($"M{Point(origin)} ");
        int index = first.Z == 1 ? 1 : 0;
        while (index < points.Length)
        {
            Vector3 current = points[index];
            if (current.Z == 1)
            {
                _ = path.Append($"L{Point(current)} ");
                index++;
            }
            else if (current.Z == 0)
            {
                Vector3 next = index + 1 < points.Length ? points[index + 1] : origin;
                Vector3 target = next.Z == 1 ? next : (current + next) / 2;
                _ = path.Append($"Q{Point(current)} {Point(target)} ");
                index += next.Z == 1 ? 2 : 1;
            }
            else
            {
                throw new InvalidOperationException("The bundled font must provide quadratic TrueType glyph contours.");
            }
        }

        _ = path.Append("Z ");
    }

    /// <summary>Invariant, shortest-roundtrip numeric formatting so identical state produces byte-identical SVG.</summary>
    internal static string Number(float number) => (number == 0 ? 0 : number).ToString("G9", CultureInfo.InvariantCulture);

    private static string Point(Vector3 point) => $"{Number(point.X)} {Number(point.Y)}";
}
