using AlleyCat.Animations;
using AlleyCat.Core.Content;
using Xunit;

namespace AlleyCat.Tests.Animations;

/// <summary>
/// ANIM-003 pack format: schema, path resolution and every validator rejection class (pack-format.md Validator 2).
/// </summary>
public sealed class AnimationPackLoaderTests
{
    private const string ManifestPath = "res://data/animations/test/pack.json";

    private static readonly ContentContext _fixtureContext = new("fixture", "res://tests/animation/catalogue/fixture/");

    private static FakePackResources Resources()
    {
        var resources = new FakePackResources();
        _ = resources.Files.Add("res://data/animations/test/idle/calm.tres");
        _ = resources.Files.Add("res://data/sets/walk/set.json");
        _ = resources.Files.Add("res://tests/animation/catalogue/fixture/idle/calm.tres");
        return resources;
    }

    private static string Manifest(string entries, string header = "\"schema\": \"anim003_pack_v1\", \"id\": \"test\"")
        => $"{{ {header}, \"entries\": [ {entries} ] }}";

    private const string IdleEntry =
                             /*lang=json,strict*/
                             "{ \"id\": \"calm\", \"category\": \"idle\", \"tags\": [\"calm\"], \"description\": \"stands calmly\", \"clip\": \"data/animations/test/idle/calm.tres\" }";

    private const string LocomotionEntry =
                             /*lang=json,strict*/
                             "{ \"id\": \"walk\", \"category\": \"locomotion\", \"tags\": [\"easy\"], \"description\": \"walks easily\", \"set\": \"data/sets/walk/set.json\" }";

    private static AnimationPackDiagnostic SingleFailure(string json, FakePackResources? resources = null, ContentContext? context = null)
    {
        (AnimationPackManifest? manifest, IReadOnlyList<AnimationPackDiagnostic> diagnostics)
            = AnimationPackLoader.Validate(json, ManifestPath, context ?? ContentContext.Default, resources ?? Resources());
        Assert.Null(manifest);
        return Assert.Single(diagnostics);
    }

    /// <summary>A valid pack loads with entries in manifest order and paths resolved against the default root.</summary>
    [Fact]
    public void Validate_ValidPack_ResolvesEntriesInManifestOrder()
    {
        (AnimationPackManifest? manifest, IReadOnlyList<AnimationPackDiagnostic> diagnostics)
            = AnimationPackLoader.Validate(Manifest(IdleEntry + "," + LocomotionEntry), ManifestPath, ContentContext.Default, Resources());

        Assert.Empty(diagnostics);
        Assert.NotNull(manifest);
        Assert.Equal("test", manifest.ID);
        Assert.Collection(manifest.Entries,
            idle =>
            {
                Assert.Equal(("calm", AnimationCategory.Idle, "test"), (idle.ID, idle.Category, idle.PackID));
                Assert.Equal("res://data/animations/test/idle/calm.tres", idle.ResourcePath);
                Assert.Equal(["calm"], idle.Tags);
                Assert.Equal("stands calmly", idle.Description);
            },
            locomotion =>
            {
                Assert.Equal(("walk", AnimationCategory.Locomotion), (locomotion.ID, locomotion.Category));
                Assert.Equal("res://data/sets/walk/set.json", locomotion.ResourcePath);
            });
    }

    /// <summary>An injected content-pack context roots every relative path at that context.</summary>
    [Fact]
    public void Validate_InjectedContext_ResolvesAgainstInjectedRoot()
    {
        const string entry = /*lang=json,strict*/ "{ \"id\": \"calm\", \"category\": \"idle\", \"tags\": [\"calm\"], \"description\": \"stands calmly\", \"clip\": \"idle/calm.tres\" }";
        AnimationPackManifest manifest = AnimationPackLoader.Load(ManifestPath, _fixtureContext, WithText(Manifest(entry)));

        Assert.Same(_fixtureContext, manifest.Context);
        Assert.Equal("res://tests/animation/catalogue/fixture/idle/calm.tres", Assert.Single(manifest.Entries).ResourcePath);

        static FakePackResources WithText(string json)
        {
            FakePackResources resources = Resources();
            resources.Texts[ManifestPath] = json;
            return resources;
        }
    }

    /// <summary>Production context derivation follows the manifest location.</summary>
    [Theory]
    [InlineData("res://assets/animations/packs/default/pack.json", "default", "res://")]
    [InlineData("res://tests/animation/catalogue/extension/pack.json", "default", "res://")]
    [InlineData("res://content/noir/data/animations/combat/pack.json", "noir", "res://content/noir/")]
    public void DeriveContext_FollowsManifestLocation(string manifest, string contentID, string root)
    {
        ContentContext context = AnimationPackLoader.DeriveContext(manifest);

        Assert.Equal(contentID, context.ContentID);
        Assert.Equal(root, context.RootPath);
    }

    /// <summary>Paths that are scheme-qualified, absolute or escape the root are rejected.</summary>
    [Theory]
    [InlineData("res://data/animations/test/idle/calm.tres")]
    [InlineData("/data/animations/test/idle/calm.tres")]
    [InlineData("C:/data/calm.tres")]
    [InlineData("data/../../calm.tres")]
    [InlineData("../calm.tres")]
    public void Validate_RejectsNonRelativePaths(string path)
    {
        AnimationPackDiagnostic diagnostic = SingleFailure(Manifest(IdleEntry.Replace("data/animations/test/idle/calm.tres", path, StringComparison.Ordinal)));

        Assert.Equal(("test", "calm", "clip", path), (diagnostic.Pack, diagnostic.Entry, diagnostic.Field, diagnostic.Path));
    }

    /// <summary>Malformed JSON fails with a manifest-level diagnostic.</summary>
    [Fact]
    public void Validate_RejectsMalformedJson()
    {
        AnimationPackDiagnostic diagnostic = SingleFailure("{ \"schema\": ");

        Assert.Equal(ManifestPath, diagnostic.Path);
        Assert.Contains("malformed JSON", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>The wrong schema identifier is rejected.</summary>
    [Fact]
    public void Validate_RejectsWrongSchema()
    {
        AnimationPackDiagnostic diagnostic = SingleFailure(Manifest(IdleEntry, "\"schema\": \"anim003_pack_v2\", \"id\": \"test\""));

        Assert.Equal(("test", "schema"), (diagnostic.Pack, diagnostic.Field));
    }

    /// <summary>Unknown top-level and entry fields are rejected.</summary>
    [Fact]
    public void Validate_RejectsUnknownFields()
    {
        Assert.Equal("author", SingleFailure(Manifest(IdleEntry, "\"schema\": \"anim003_pack_v1\", \"id\": \"test\", \"author\": \"x\"")).Field);
        Assert.Equal("set", SingleFailure(Manifest(IdleEntry.Replace("}", ", \"set\": \"data/sets/walk/set.json\" }", StringComparison.Ordinal))).Field);
    }

    /// <summary>Missing or blank required fields, empty tags and a bad category are rejected.</summary>
    [Theory]
    [InlineData("\"description\": \"stands calmly\"", "\"description\": \"  \"", "description")]
    [InlineData("\"id\": \"calm\"", "\"id\": \"\"", "id")]
    [InlineData("\"tags\": [\"calm\"]", "\"tags\": []", "tags")]
    [InlineData("\"category\": \"idle\"", "\"category\": \"gesture\"", "category")]
    [InlineData(", \"clip\": \"data/animations/test/idle/calm.tres\"", "", "clip")]
    public void Validate_RejectsMissingOrInvalidRequiredFields(string original, string replacement, string field)
    {
        AnimationPackDiagnostic diagnostic = SingleFailure(Manifest(IdleEntry.Replace(original, replacement, StringComparison.Ordinal)));

        Assert.Equal(field, diagnostic.Field);
    }

    /// <summary>Duplicate ids within a category are rejected; the same id in another category is allowed.</summary>
    [Fact]
    public void Validate_RejectsDuplicateIDsWithinCategory()
    {
        AnimationPackDiagnostic diagnostic = SingleFailure(Manifest(IdleEntry + "," + IdleEntry));
        Assert.Equal(("calm", "id"), (diagnostic.Entry, diagnostic.Field));

        string sameIDOtherCategory = LocomotionEntry.Replace("\"walk\"", "\"calm\"", StringComparison.Ordinal);
        (AnimationPackManifest? manifest, _) = AnimationPackLoader.Validate(
            Manifest(IdleEntry + "," + sameIDOtherCategory), ManifestPath, ContentContext.Default, Resources());
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Entries.Count);
    }

    /// <summary>Missing files, non-Animation clips and sets the ANIM-002 loader rejects fail with the path named.</summary>
    [Fact]
    public void Validate_RejectsUnresolvableResources()
    {
        FakePackResources missing = Resources();
        _ = missing.Files.Remove("res://data/animations/test/idle/calm.tres");
        Assert.Contains("does not exist", SingleFailure(Manifest(IdleEntry), missing).Message, StringComparison.Ordinal);

        FakePackResources notAnimation = Resources();
        notAnimation.ClipErrors["res://data/animations/test/idle/calm.tres"] = "not an Animation";
        AnimationPackDiagnostic clip = SingleFailure(Manifest(IdleEntry), notAnimation);
        Assert.Equal(("clip", "data/animations/test/idle/calm.tres", "not an Animation"), (clip.Field, clip.Path, clip.Message));

        FakePackResources rejectedSet = Resources();
        rejectedSet.SetErrors["res://data/sets/walk/set.json"] = "Unreviewed source set.";
        AnimationPackDiagnostic set = SingleFailure(Manifest(LocomotionEntry), rejectedSet);
        Assert.Equal(("test", "walk", "set", "data/sets/walk/set.json"), (set.Pack, set.Entry, set.Field, set.Path));
    }

    /// <summary>Load throws with every diagnostic in the message.</summary>
    [Fact]
    public void Load_InvalidPack_ThrowsWithDiagnostics()
    {
        FakePackResources resources = Resources();
        resources.Texts[ManifestPath] = Manifest(IdleEntry, "\"schema\": \"nope\", \"id\": \"test\"");

        AnimationPackValidationException error = Assert.Throws<AnimationPackValidationException>(
            () => AnimationPackLoader.Load(ManifestPath, ContentContext.Default, resources));

        Assert.Equal("schema", Assert.Single(error.Diagnostics).Field);
        Assert.Contains("Animation pack 'test'", error.Message, StringComparison.Ordinal);
    }
}
