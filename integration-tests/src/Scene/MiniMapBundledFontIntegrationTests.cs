using AlleyCat.Scene.MiniMap;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Scene;

/// <summary>
/// TR-15 bundled-font contract at runtime: the fallback chain head Inter keeps its Latin coverage without Hangul
/// (so the fallback is genuinely exercised), and the bundled OFL Hangul fallback loads as an imported resource
/// and supplies every dissent-identity Hangul rune with quadratic TrueType contours through the exact TextServer
/// API <see cref="MiniMapGlyphs" /> uses — the CFF-flavoured-OTF failure mode this guards against. Headless-safe:
/// pure TextServer queries, no renderer.
/// </summary>
public sealed class MiniMapBundledFontIntegrationTests
{
    private const string InterFontPath = "res://assets/fonts/Inter/Inter_18pt-Medium.ttf";

    private const string HangulFontPath = "res://assets/fonts/NanumGothic/NanumGothic-Regular.ttf";

    private static readonly int[] _hangul = [0xAC15, 0xC8FC, 0xC601, 0xC774, 0xB9CC, 0xBCF5, 0xBC15, 0xC18C, 0xD76C, 0xBBFC, 0xC218];

    /// <summary>
    /// Verifies the chain head and the bundled Hangul fallback through the imported resources: Inter covers Latin
    /// but no Hangul, while NanumGothic covers every dissent-identity rune with quadratic contours.
    /// </summary>
    [Headless]
    [Fact]
    public void BundledFallbackChain_InterLacksHangulAndBundledFallbackSuppliesQuadraticContours()
    {
        TextServer server = TextServerManager.GetPrimaryInterface();

        FontFile inter = ResourceLoader.Load<FontFile>(InterFontPath)
            ?? throw new InvalidOperationException($"Imported Inter font is unavailable at {InterFontPath}.");
        Rid interRid = inter.GetRids()[0];
        Assert.True(server.FontGetGlyphIndex(interRid, 16, 'A', 0) != 0, "Inter must cover Latin 'A'.");
        Assert.True(server.FontGetGlyphIndex(interRid, 16, 0xAC15, 0) == 0,
            "Inter must lack Hangul so the fallback chain is genuinely exercised.");

        FontFile hangul = ResourceLoader.Load<FontFile>(HangulFontPath)
            ?? throw new InvalidOperationException($"Imported Hangul fallback font is unavailable at {HangulFontPath}.");
        Rid hangulRid = hangul.GetRids()[0];
        foreach (int code in _hangul)
        {
            long glyph = server.FontGetGlyphIndex(hangulRid, 16, code, 0);
            Assert.True(glyph != 0, $"The bundled fallback must cover U+{code:X4}.");
            Godot.Collections.Dictionary outline = server.FontGetGlyphContours(hangulRid, 16, glyph);
            Vector3[] points = outline["points"].AsVector3Array();
            int[] contours = outline["contours"].AsInt32Array();
            Assert.True(points.Length > 0 && points.All(point => point.Z is 0 or 1) && contours.Length > 0,
                $"U+{code:X4} glyph {glyph} contours={contours.Length} points={points.Length}: the bundled fallback "
                    + "must supply quadratic TrueType contours through the glyph pipeline.");
        }

        // A script no chain font covers stays uncovered, keeping the whole-label omission path honest (TR-15).
        Assert.True(server.FontGetGlyphIndex(hangulRid, 16, 0x1F600, 0) == 0,
            "The bundled fallback must not cover emoji, which exercises the UnsupportedGlyph path.");
    }
}
