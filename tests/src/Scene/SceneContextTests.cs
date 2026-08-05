using AlleyCat.Body.Eyes;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Scene;
using Xunit;

namespace AlleyCat.Tests.SceneSystem;

/// <summary>
/// Unit coverage for scene identity registration boundaries.
/// </summary>
public sealed class SceneContextTests
{
    /// <summary>Valid lower snake_case character IDs are registered with their canonical FullId.</summary>
    [Fact]
    public void Constructor_AcceptsCharacterLowerSnakeCaseIdAndExposesFullId()
    {
        var character = new FakeCharacter("ally");

        SceneContext context = new([character]);

        Assert.Same(character, Assert.Single(context.Characters));
        Assert.Equal("char:ally", character.FullId);
    }

    /// <summary>Invalid character IDs are rejected before scene exposure.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Ally")]
    [InlineData("ally-name")]
    public void Constructor_RejectsInvalidCharacterId(string id)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new SceneContext([new FakeCharacter(id)]));

        Assert.Contains("invalid identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lower snake_case", exception.Message);
    }

    /// <summary>Invalid character types are rejected before scene exposure.</summary>
    [Fact]
    public void Constructor_RejectsInvalidCharacterType()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new SceneContext([new FakeCharacter("ally", "Character")]));

        Assert.Contains("invalid identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Type", exception.Message);
    }

    /// <summary>Custom character implementations cannot register a canonical-looking but inconsistent FullId.</summary>
    [Fact]
    public void Constructor_RejectsCanonicalLookingFullIdThatDoesNotMatchTypeAndId()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new SceneContext([new FakeCharacter("ally", fullIdOverride: "char:other_character")]));

        Assert.Contains("invalid identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly match", exception.Message);
        _ = Assert.IsType<ArgumentException>(exception.InnerException);
    }

    /// <summary>Exact duplicate character identities remain invalid.</summary>
    [Fact]
    public void Constructor_RetainsDuplicateIdentityValidation()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new SceneContext([new FakeCharacter("ally"), new FakeCharacter("ally")]));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("char:ally", exception.Message);
    }

    /// <summary>Canonical character FullIds resolve to their live snapshot members using ordinal matching.</summary>
    [Fact]
    public void FindAndResolve_WhenCanonicalCharacterFullIdMatches_ReturnLiveCharacter()
    {
        var character = new FakeCharacter("ally");
        SceneContext context = new([character]);

        Assert.Same(character, context.Find("char:ally"));
        Assert.Same(character, context.Resolve("char:ally"));
        Assert.Null(context.Find("char:other_ally"));
    }

    /// <summary>Lookup membership is fixed while a member's identity remains live.</summary>
    [Fact]
    public void Find_PreservesSnapshotMembershipWithLiveIdentity()
    {
        var character = new FakeCharacter("ally");
        SceneContext context = new([character]);

        character.Id = "renamed_ally";

        Assert.Null(context.Find("char:ally"));
        Assert.Same(character, context.Find("char:renamed_ally"));
    }

    /// <summary>Unmapped and absent canonical types are not scene members.</summary>
    [Fact]
    public void FindAndResolve_WhenIdentityIsAbsentOrTypeIsUnmapped_ReturnsNullOrThrows()
    {
        SceneContext context = new([new FakeCharacter("ally")]);

        Assert.Null(context.Find("char:missing"));
        Assert.Null(context.Find("loc:interrogation_room"));
        _ = Assert.Throws<InvalidOperationException>(() => context.Resolve("char:missing"));
        _ = Assert.Throws<InvalidOperationException>(() => context.Resolve("loc:interrogation_room"));
    }

    /// <summary>Lookup only accepts canonical CORE-009 FullId input.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ally")]
    [InlineData("char:Ally")]
    public void FindAndResolve_WhenFullIdIsNotCanonical_ThrowsArgumentException(string fullId)
    {
        SceneContext context = new([new FakeCharacter("ally")]);

        _ = Assert.Throws<ArgumentException>(() => context.Find(fullId));
        _ = Assert.Throws<ArgumentException>(() => context.Resolve(fullId));
    }

    private sealed class FakeCharacter(string id, string type = "char", string? fullIdOverride = null) : ICharacter
    {
        public string Id { get; set; } = id;

        string IIdentifiable.Type => type;

        public string FullId => fullIdOverride ?? $"{type}:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
