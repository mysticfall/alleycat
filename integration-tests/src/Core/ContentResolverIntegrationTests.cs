using AlleyCat.Core.Content;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Core;

/// <summary>
/// Best-effort runtime coverage for <see cref="ContentResolver"/> selection behaviour.
/// </summary>
public sealed class ContentResolverIntegrationTests
{
    private const string FixtureManifestRoot = "res://assets/testing/content/pack-manifests/";

    /// <summary>
    /// Ensures the resolver constructs and applies the Godot-free selection logic.
    /// </summary>
    [Fact]
    public void ResolveStartScenePath_ReturnsFallback_WhenIntegrationTest()
    {
        ContentResolver resolver = new();

        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: true,
            skipContentPack: false,
            sceneExists: _ => true,
            fallbackStartScenePath: "res://assets/scenes/empty.tscn");

        Assert.Equal("res://assets/scenes/empty.tscn", result);
    }

    /// <summary>
    /// Ensures a resolver running inside an integration-test process resolves the built-in English content context
    /// instead of the manifest default pack, and caches it for subsequent calls.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_ReturnsBuiltInEnglishContext_AndCachesIt_WhenIntegrationTest()
    {
        ContentResolver resolver = new();

        ContentContext first = resolver.GetCurrentContentContext();
        ContentContext second = resolver.GetCurrentContentContext();

        Assert.Equal(ContentContext.Default, first);
        Assert.Same(first, second);
        Assert.Equal("en", first.Language);
    }

    /// <summary>
    /// A real per-pack manifest declaring Korean must load through the Godot resource boundary and resolve the
    /// normalised Korean code.
    /// </summary>
    [Fact]
    public void LoadPackLanguage_KoreanManifest_ResolvesKo()
    {
        string language = ContentResolver.LoadPackLanguage(
            "korean-pack",
            FixtureManifestRoot + "korean.tres");

        Assert.Equal("ko", language);
    }

    /// <summary>
    /// Regional and case variants declared in a real manifest must normalise to their primary language code.
    /// </summary>
    [Fact]
    public void LoadPackLanguage_RegionalManifest_TrimsAndMapsToPrimarySublanguage()
    {
        string language = ContentResolver.LoadPackLanguage(
            "regional-pack",
            FixtureManifestRoot + "regional.tres");

        Assert.Equal("en", language);
    }

    /// <summary>
    /// A real manifest with a blank language value must resolve the English default as a normal startup path.
    /// </summary>
    [Fact]
    public void LoadPackLanguage_BlankManifest_ResolvesEnglishDefault()
    {
        string language = ContentResolver.LoadPackLanguage(
            "blank-pack",
            FixtureManifestRoot + "blank.tres");

        Assert.Equal("en", language);
    }

    /// <summary>
    /// A missing per-pack manifest must resolve English without emitting Godot missing-resource errors.
    /// </summary>
    [Fact]
    public void LoadPackLanguage_MissingManifest_ResolvesEnglishSilently()
    {
        string language = ContentResolver.LoadPackLanguage(
            "missing-pack",
            FixtureManifestRoot + "does-not-exist.tres");

        Assert.Equal("en", language);
    }

    /// <summary>
    /// A manifest declaring an unsupported language must fail with an actionable message naming the pack, the
    /// manifest path, the declared value, and the supported set, instead of silently resolving another language.
    /// </summary>
    [Fact]
    public void LoadPackLanguage_UnsupportedLanguage_ThrowsActionableError()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ContentResolver.LoadPackLanguage("unsupported-pack", FixtureManifestRoot + "unsupported.tres"));

        Assert.Contains("unsupported-pack", exception.Message, StringComparison.Ordinal);
        Assert.Contains(FixtureManifestRoot + "unsupported.tres", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fr", exception.Message, StringComparison.Ordinal);
        Assert.Contains("en, ko", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest resource that exists but carries a different script must fail with an actionable message naming
    /// the pack and the manifest path.
    /// </summary>
    [Fact]
    public void LoadPackLanguage_WrongScriptManifest_ThrowsActionableError()
    {
        string manifestPath = FixtureManifestRoot + "wrong-script.tres";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ContentResolver.LoadPackLanguage("wrong-script-pack", manifestPath));

        Assert.Contains("wrong-script-pack", exception.Message, StringComparison.Ordinal);
        Assert.Contains(manifestPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ContentPackManifest), exception.Message, StringComparison.Ordinal);
        Assert.Contains("en, ko", exception.Message, StringComparison.Ordinal);
        Assert.Contains("declared Language value is unavailable", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The game's DI provider must expose the content resolver as a singleton whose integration-test context
    /// resolves English built-in content, so speech consumers can resolve the effective language from it.
    /// </summary>
    [Fact]
    public void GameProvider_ResolvesContentResolverSingleton_WithEnglishBypassContext()
    {
        IContentResolver first = Game.Instance.GetRequiredService<IContentResolver>();
        IContentResolver second = Game.Instance.GetRequiredService<IContentResolver>();

        Assert.Same(first, second);
        ContentContext context = first.GetCurrentContentContext();
        Assert.Equal(ContentContext.Default, context);
        Assert.Equal("en", context.Language);
    }
}
