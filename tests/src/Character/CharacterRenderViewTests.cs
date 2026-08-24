using System.Reflection;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Vision;
using Xunit;

namespace AlleyCat.Tests.Character;

/// <summary>
/// Unit coverage for the curated character render view exposed to session prompt rendering (AI-003 TR-30).
/// </summary>
public sealed class CharacterRenderViewTests
{
    /// <summary>The view wraps its character and reports that character's exact canonical authored identity.</summary>
    [Theory]
    [InlineData("case_sensitive_identity")]
    [InlineData("char_with_underscores")]
    public void Constructor_WrapsSuppliedCharacter_AndExposesItsExactCanonicalFullId(string id)
    {
        ICharacter character = new FakeCharacter
        {
            Id = id,
        };

        var view = new CharacterRenderView(character);

        Assert.Equal($"char:{id}", view.FullId);
        Assert.Equal(character.FullId, view.FullId);
    }

    /// <summary>
    /// The curated surface is exactly <c>FullId</c> and is immutable: no other instance member reaches template
    /// rendering through Fluid's unsafe member-access strategy (AI-003 TR-30).
    /// </summary>
    [Fact]
    public void ViewSurface_ExposesExactlyAReadOnlyFullIdMember()
    {
        PropertyInfo[] properties = typeof(CharacterRenderView)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        PropertyInfo fullId = Assert.Single(properties);

        Assert.Equal(nameof(CharacterRenderView.FullId), fullId.Name);
        Assert.Equal(typeof(string), fullId.PropertyType);
        Assert.True(fullId.GetMethod?.IsPublic ?? false);
        Assert.Null(fullId.SetMethod);

        FieldInfo[] fields = typeof(CharacterRenderView)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Empty(fields);
    }

    /// <summary>A null wrapped character cannot produce a render view.</summary>
    [Fact]
    public void Constructor_WhenCharacterIsNull_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => new CharacterRenderView(null!));

    private sealed class FakeCharacter : ICharacter
    {
        public string Id
        {
            get; set;
        } = "fake_character";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }
}
