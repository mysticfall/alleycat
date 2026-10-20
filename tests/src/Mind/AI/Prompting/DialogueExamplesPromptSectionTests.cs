using System.Runtime.CompilerServices;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.AI.Prompting;

/// <summary>
/// Unit coverage for the pure path-resolution and identity logic behind the dialogue examples section: the
/// observer <c>FullId</c> maps to a colon-free perspective path under the active content root, for the default
/// root and content packs alike, and an invalid identity fails clearly at the usage boundary (AI-012 TR-1, TR-6).
/// </summary>
public sealed class DialogueExamplesPromptSectionTests
{
    /// <summary>Token substitution is exact and never evaluates other authored syntax.</summary>
    [Fact]
    public void SubstituteCharacterToken_ChangesOnlyExactSanctionedToken()
    {
        const string text = "{{ character.FullId }}: {{character.FullId}} {{ character.Id }} {% broken {{ character.FullId }}";
        string result = DialogueExamplesContent.SubstituteCharacterToken(text, "char:test");
        Assert.Equal("char:test: {{character.FullId}} {{ character.Id }} {% broken char:test", result);
    }

    /// <summary>
    /// The default content root resolves <c>char:vadim</c> to the perspective dialogue file with no colon in the
    /// path below the resource scheme (AI-012 TR-1).
    /// </summary>
    [Fact]
    public void Resolve_DefaultRoot_MapsObserverFullIdToColonFreePerspectivePath()
    {
        string path = DialogueExamplesPaths.Resolve(ContentContext.Default, "char:vadim", "Test Section");

        Assert.Equal("res://lore/perspectives/char/vadim/dialogue.md", path);
        Assert.DoesNotContain(':', path["res://".Length..]);
    }

    /// <summary>
    /// A content pack root resolves the same observer to the pack's own perspective dialogue file, so pack
    /// examples behave identically to default-root examples (AI-012 TR-1, TR-9).
    /// </summary>
    [Fact]
    public void Resolve_ContentPackRoot_MapsObserverFullIdToPackPerspectivePath()
    {
        string path = DialogueExamplesPaths.Resolve(
            ContentContext.ForPack("myst"),
            "char:vadim",
            "Test Section");

        Assert.Equal("res://content/myst/lore/perspectives/char/vadim/dialogue.md", path);
    }

    /// <summary>A root path without a trailing slash still resolves with exactly one separator.</summary>
    [Fact]
    public void Resolve_RootWithoutTrailingSlash_InsertsSingleSeparator()
    {
        string path = DialogueExamplesPaths.Resolve(
            new ContentContext("custom", "res://tests/custom-fixture"),
            "char:test",
            "Test Section");

        Assert.Equal("res://tests/custom-fixture/lore/perspectives/char/test/dialogue.md", path);
    }

    /// <summary>
    /// An invalid observer identity fails with a section-naming error at the usage boundary, never as a raw
    /// identity failure, and only when the section actually resolves its path (AI-012 TR-6).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vadim")]
    [InlineData("char:")]
    [InlineData(":vadim")]
    [InlineData("char:vadim:extra")]
    [InlineData("Char:vadim")]
    [InlineData("char:Vadim")]
    public void Resolve_InvalidObserverFullId_FailsClearlyAtUsageBoundary(string observerFullId)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DialogueExamplesPaths.Resolve(ContentContext.Default, observerFullId, "Test Section"));

        Assert.Contains("Test Section", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observer ID", exception.Message, StringComparison.Ordinal);
        _ = Assert.IsType<ArgumentException>(exception.InnerException);
    }

    /// <summary>
    /// The section validates identity only when used: building content for an invalid owner fails with the
    /// section-naming error before any file access (AI-012 TR-6).
    /// </summary>
    [Fact]
    public async Task GetContentAsync_WithInvalidOwnerFullId_FailsClearlyWhenSectionIsUsed()
    {
        DialogueExamplesPromptSection section = CreateSectionWithoutGodotRuntime();
        FakeCharacter owner = new("owner", fullIdOverride: string.Empty);
        var buildContext = new PromptSectionBuildContext(
            new EmptyServiceProvider(),
            new FakeSceneContext([owner]),
            owner);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => section.GetContentAsync(buildContext));

        Assert.Contains("DialogueExamplesPromptSection", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observer ID", exception.Message, StringComparison.Ordinal);
        _ = Assert.IsType<ArgumentException>(exception.InnerException);
    }

    private static DialogueExamplesPromptSection CreateSectionWithoutGodotRuntime()
        => (DialogueExamplesPromptSection)RuntimeHelpers.GetUninitializedObject(typeof(DialogueExamplesPromptSection));

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed record FakeSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
        {
            IdentityValidator.ValidateFullId(fullId, nameof(fullId));
            return Characters.FirstOrDefault(
                character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));
        }

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException(
                $"Current scene does not contain identifiable object '{fullId}'.");
    }

    private sealed class FakeCharacter(string id, string? fullIdOverride = null) : ICharacter
    {
        public string Id { get; set; } = id;

        public string Type => "char";

        public string FullId => fullIdOverride ?? $"{Type}:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
