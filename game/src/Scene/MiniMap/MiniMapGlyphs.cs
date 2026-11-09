using System.Globalization;
using System.Security;
using System.Text;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>
/// Converts text to self-contained SVG glyph outline paths through the bundled fallback chain (SCN-002 TR-15):
/// Inter first — so Latin metrics are unchanged — then the bundled OFL-licensed Hangul-capable fallback. Each
/// rune resolves through the first chain font that supports it, so mixed-script labels render natively; a rune
/// no chain font covers throws, which callers turn into the whole-label <c>UnsupportedGlyph</c> omission.
/// Godot's SVG rasteriser does not support SVG <c>&lt;text&gt;</c>, so every rendered string — marker labels and
/// grid numbers — must become filled glyph geometry through this pipeline.
/// </summary>
public sealed class MiniMapGlyphs
{
    private const string InterFontPath = "res://assets/fonts/Inter/Inter_18pt-Medium.ttf";
    private const string HangulFontPath = "res://assets/fonts/NanumGothic/NanumGothic-Regular.ttf";

    private static readonly MiniMapGlyphs _shared = new();

    private readonly (FontFile Font, Rid Rid)[] _chain;

    private MiniMapGlyphs()
    {
        _chain =
        [
            (LoadFont(InterFontPath), default),
            (LoadFont(HangulFontPath), default),
        ];
        for (int index = 0; index < _chain.Length; index++)
        {
            _chain[index].Rid = _chain[index].Font.GetRids()[0];
        }
    }

    private static FontFile LoadFont(string path) => ResourceLoader.Load<FontFile>(path)
        ?? throw new InvalidOperationException($"Bundled font is unavailable; restore/import {path}.");

    /// <summary>
    /// Measures the same unshaped glyph advances and ink extents used by <see cref="Write" />, resolving each
    /// rune through the fallback chain.
    /// </summary>
    /// <param name="text">Single-line text to measure.</param>
    /// <param name="size">Glyph pixel size.</param>
    /// <returns>Total advance width and ink height in pixels.</returns>
    public static Vector2 Measure(string text, int size = 16) => _shared.MeasureInstance(text, size);

    /// <summary>
    /// Measures the axis-aligned ink bounds of the glyph outline points <see cref="Write" /> would render,
    /// relative to the pen origin: X grows right from the pen start, Y grows down from the baseline, so ink
    /// above the baseline is negative. Throws <see cref="InvalidOperationException" /> on runes no chain font
    /// covers, exactly like the write path.
    /// </summary>
    /// <param name="text">Single-line text to bound.</param>
    /// <param name="size">Glyph pixel size.</param>
    /// <returns>Ink bounds rectangle, or an empty rectangle when the text renders no glyph points.</returns>
    public static Rect2 InkBounds(string text, int size = 16) => _shared.InkBoundsInstance(text, size);

    /// <summary>
    /// Converts single-line text to filled glyph geometry positioned in image pixels; never SVG text. Each rune's
    /// glyph comes from the first chain font that supports it.
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
        float advance = 0;
        float minimum = 0;
        float maximum = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            (Rid font, long glyph) = Resolve(server, size, rune);
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

    private Rect2 InkBoundsInstance(string text, int size)
    {
        TextServer server = TextServerManager.GetPrimaryInterface();
        float advance = 0;
        float minimumX = 0;
        float maximumX = 0;
        float minimumY = 0;
        float maximumY = 0;
        bool any = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            (Rid font, long glyph) = Resolve(server, size, rune);
            Vector3[] points = server.FontGetGlyphContours(font, size, glyph)["points"].AsVector3Array();
            foreach (Vector3 point in points)
            {
                float x = advance + point.X;
                if (any)
                {
                    minimumX = Math.Min(minimumX, x);
                    maximumX = Math.Max(maximumX, x);
                    minimumY = Math.Min(minimumY, point.Y);
                    maximumY = Math.Max(maximumY, point.Y);
                }
                else
                {
                    minimumX = maximumX = x;
                    minimumY = maximumY = point.Y;
                    any = true;
                }
            }

            advance += server.FontGetGlyphAdvance(font, size, glyph).X;
        }

        return any ? new Rect2(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY) : new Rect2();
    }

    private string WriteInstance(string text, float x, float baseline, int size, string fill)
    {
        TextServer server = TextServerManager.GetPrimaryInterface();
        var paths = new StringBuilder();
        _ = paths.Append($"<g fill=\"{fill}\" aria-label=\"{SecurityElement.Escape(text)}\">");
        foreach (Rune rune in text.EnumerateRunes())
        {
            (Rid font, long glyph) = Resolve(server, size, rune);

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

    /// <summary>
    /// Resolves one rune through the fixed bundled fallback chain (TR-15): the first chain font that supports the
    /// rune supplies its glyph, so Latin keeps Inter metrics and Hangul renders from the bundled fallback.
    /// </summary>
    /// <param name="server">Active text server.</param>
    /// <param name="size">Glyph pixel size.</param>
    /// <param name="rune">Rune to resolve.</param>
    /// <returns>The supplying chain font's RID and the rune's glyph index in it.</returns>
    /// <exception cref="InvalidOperationException">The rune is a control/mark rune, or no chain font covers it.</exception>
    private (Rid Font, long Glyph) Resolve(TextServer server, int size, Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (Rune.IsControl(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            || rune.Value is >= 0x0590 and <= 0x08ff)
        {
            throw new InvalidOperationException($"Unsupported text shaping/control U+{rune.Value:X4}; use precomposed supported labels.");
        }

        foreach ((_, Rid font) in _chain)
        {
            long glyph = server.FontGetGlyphIndex(font, size, rune.Value, 0);
            if (glyph != 0)
            {
                return (font, glyph);
            }
        }

        throw new InvalidOperationException($"Unsupported glyph U+{rune.Value:X4}; no font in the bundled fallback chain covers it.");
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
