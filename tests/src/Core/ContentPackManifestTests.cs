using AlleyCat.Core.Content;
using Xunit;

namespace AlleyCat.Tests.Core;

/// <summary>
/// Tests the Godot-free spoken-language normalisation and validation owned by the per-pack manifest contract.
/// </summary>
public sealed class ContentPackManifestTests
{
    private const string PackId = "lang-pack";
    private const string ManifestPath = "res://content/lang-pack/manifest.tres";

    /// <summary>
    /// The backward-compatible default must stay English and the supported set must stay evidence-gated to
    /// English and Korean. The resource default itself is covered at the Godot boundary by the integration
    /// fixtures, because a <see cref="Godot.Resource" /> cannot be instantiated in the Godot-free test host.
    /// </summary>
    [Fact]
    public void SupportedSet_IsEnglishDefault_AndEnglishKoreanSupported()
    {
        Assert.Equal("en", ContentPackManifest.DefaultLanguage);
        Assert.Equal(["en", "ko"], ContentPackManifest.SupportedLanguages);
    }

    /// <summary>
    /// Null, omitted, and blank declared values must resolve to the English default rather than failing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t ")]
    public void ResolveDeclaredLanguage_BlankValue_ResolvesEnglishDefault(string? declared) => Assert.Equal("en", ContentPackManifest.ResolveDeclaredLanguage(declared, PackId, ManifestPath));

    /// <summary>
    /// Declared values are trimmed and matched case-insensitively to their canonical lowercase code.
    /// </summary>
    [Theory]
    [InlineData("en", "en")]
    [InlineData(" EN ", "en")]
    [InlineData("ko", "ko")]
    [InlineData(" Ko ", "ko")]
    [InlineData("KO", "ko")]
    public void ResolveDeclaredLanguage_SupportedCode_TrimsAndLowercases(string declared, string expected) => Assert.Equal(expected, ContentPackManifest.ResolveDeclaredLanguage(declared, PackId, ManifestPath));

    /// <summary>
    /// Regional tags map to the spoken-language code of their primary sublanguage, including mixed case.
    /// </summary>
    [Theory]
    [InlineData("en-GB", "en")]
    [InlineData("ko-KR", "ko")]
    [InlineData(" EN-us ", "en")]
    [InlineData("ko-kr", "ko")]
    public void ResolveDeclaredLanguage_RegionalTag_MapsToPrimarySublanguage(string declared, string expected) => Assert.Equal(expected, ContentPackManifest.ResolveDeclaredLanguage(declared, PackId, ManifestPath));

    /// <summary>
    /// A declared primary sublanguage outside the supported set must fail with an actionable message naming the
    /// pack, the manifest path, the declared value, and the supported set, instead of silently remapping.
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("ja-JP")]
    [InlineData("French")]
    public void ResolveDeclaredLanguage_UnsupportedLanguage_ThrowsActionableError(string declared)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ContentPackManifest.ResolveDeclaredLanguage(declared, PackId, ManifestPath));

        Assert.Contains(PackId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ManifestPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(declared, exception.Message, StringComparison.Ordinal);
        Assert.Contains("en, ko", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unsupported-language failure must name the trimmed declared value, not the derived sublanguage alone.
    /// </summary>
    [Fact]
    public void ResolveDeclaredLanguage_UnsupportedRegionalTag_NamesTrimmedDeclaredValue()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ContentPackManifest.ResolveDeclaredLanguage("  fr-CA  ", PackId, ManifestPath));

        Assert.Contains("fr-CA", exception.Message, StringComparison.Ordinal);
    }
}
