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
        Assert.Contains("declared metadata is unavailable", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A missing per-pack manifest must resolve the conventional start scene without Godot missing-resource
    /// errors.
    /// </summary>
    [Fact]
    public void LoadPackStartScenePath_MissingManifest_ResolvesConventionalStartScene()
    {
        string path = ContentResolver.LoadPackStartScenePath(
            "missing-pack",
            FixtureManifestRoot + "does-not-exist.tres");

        Assert.Equal("res://content/missing-pack/start.tscn", path);
    }

    /// <summary>
    /// A real manifest with a blank start-scene declaration must resolve the conventional start scene as a
    /// normal startup path.
    /// </summary>
    [Fact]
    public void LoadPackStartScenePath_BlankDeclaration_ResolvesConventionalStartScene()
    {
        string path = ContentResolver.LoadPackStartScenePath(
            "start-scene-blank-pack",
            FixtureManifestRoot + "start-scene-blank.tres");

        Assert.Equal("res://content/start-scene-blank-pack/start.tscn", path);
    }

    /// <summary>
    /// A real pack-relative declaration must resolve against the pack root through the Godot resource boundary.
    /// </summary>
    [Fact]
    public void LoadPackStartScenePath_RelativeDeclaration_ResolvesAgainstPackRoot()
    {
        string path = ContentResolver.LoadPackStartScenePath(
            "start-scene-relative-pack",
            FixtureManifestRoot + "start-scene-relative.tres");

        Assert.Equal("res://content/start-scene-relative-pack/scenes/intro.tscn", path);
    }

    /// <summary>
    /// A real <c>res://</c>-prefixed declaration must be used exactly as written.
    /// </summary>
    [Fact]
    public void LoadPackStartScenePath_AbsoluteDeclaration_IsUsedAsIs()
    {
        string path = ContentResolver.LoadPackStartScenePath(
            "start-scene-absolute-pack",
            FixtureManifestRoot + "start-scene-absolute.tres");

        Assert.Equal("res://content/shared/intro.tscn", path);
    }

    /// <summary>
    /// A real declaration with outer whitespace must be trimmed before resolving against the pack root.
    /// </summary>
    [Fact]
    public void LoadPackStartScenePath_TrimmedDeclaration_TrimsBeforeResolving()
    {
        string path = ContentResolver.LoadPackStartScenePath(
            "start-scene-trim-pack",
            FixtureManifestRoot + "start-scene-trim.tres");

        Assert.Equal("res://content/start-scene-trim-pack/scenes/trimmed.tscn", path);
    }

    /// <summary>
    /// A start-scene read of a manifest resource that exists but carries a different script must fail with the
    /// shared actionable message naming the pack and the manifest path.
    /// </summary>
    [Fact]
    public void LoadPackStartScenePath_WrongScriptManifest_ThrowsActionableError()
    {
        string manifestPath = FixtureManifestRoot + "wrong-script.tres";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ContentResolver.LoadPackStartScenePath("wrong-script-pack", manifestPath));

        Assert.Contains("wrong-script-pack", exception.Message, StringComparison.Ordinal);
        Assert.Contains(manifestPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ContentPackManifest), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Selection must resolve a requested pack's declared start scene through the real manifest resource and
    /// probe exactly the declared path, mirroring the explicit-request shape used by speech selection.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_DeclaredStartScene_ResolvesThroughRealManifest()
    {
        const string declaredPath = "res://content/start-scene-relative-pack/scenes/intro.tscn";

        string result = ContentResolver.SelectStartScenePath(
            "start-scene-relative-pack",
            null,
            isIntegrationTest: false,
            skipContentPack: false,
            sceneExists: path => path == declaredPath,
            fallbackStartScenePath: "res://assets/scenes/empty.tscn",
            resolvePackStartScene: pack => ContentResolver.LoadPackStartScenePath(
                pack,
                FixtureManifestRoot + "start-scene-relative.tres"));

        Assert.Equal(declaredPath, result);
    }

    /// <summary>
    /// A default pack candidate whose manifest exists but cannot be loaded must fail selection even when the
    /// scene probe would succeed, because the manifest is read before the probe.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_UnloadableDefaultManifest_ThrowsEvenWhenSceneExists()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ContentResolver.SelectStartScenePath(
                null,
                "wrong-script-pack",
                isIntegrationTest: false,
                skipContentPack: false,
                sceneExists: _ => true,
                fallbackStartScenePath: "res://assets/scenes/empty.tscn",
                resolvePackStartScene: pack => ContentResolver.LoadPackStartScenePath(
                    pack,
                    FixtureManifestRoot + "wrong-script.tres")));

        Assert.Contains("wrong-script-pack", exception.Message, StringComparison.Ordinal);
        Assert.Contains(FixtureManifestRoot + "wrong-script.tres", exception.Message, StringComparison.Ordinal);
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
