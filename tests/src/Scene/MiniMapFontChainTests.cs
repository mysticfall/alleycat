using Xunit;

namespace AlleyCat.Tests.Scene;

/// <summary>
/// Unit coverage for the fixed TR-15 bundled-font contract: the fallback-chain head and the OFL-licensed
/// Hangul-capable fallback font plus its licence file live under <c>game/assets/fonts/</c>. The Inter-first
/// behavioural order (Latin metrics unchanged, Hangul from the fallback) is covered by the integration suites.
/// </summary>
public sealed class MiniMapFontChainTests
{
    /// <summary>The fallback-chain head Inter and the bundled Hangul fallback with its OFL licence exist under game/assets/fonts (TR-15).</summary>
    [Fact]
    public void BundledFallbackChain_FontsAndLicenceExistUnderAssetsFonts()
    {
        Assert.True(File.Exists(RepositoryPath.Get("game", "assets", "fonts", "Inter", "Inter_18pt-Medium.ttf")),
            "The fallback-chain head Inter_18pt-Medium.ttf must stay bundled.");
        Assert.True(File.Exists(RepositoryPath.Get("game", "assets", "fonts", "NanumGothic", "NanumGothic-Regular.ttf")),
            "The bundled Hangul-capable fallback font must stay under game/assets/fonts/NanumGothic/.");
        Assert.True(File.Exists(RepositoryPath.Get("game", "assets", "fonts", "NanumGothic", "OFL.txt")),
            "The fallback font's OFL licence must ship beside it.");
    }
}
