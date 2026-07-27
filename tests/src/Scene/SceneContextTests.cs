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
