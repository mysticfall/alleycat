using AlleyCat.Core;
using Xunit;

namespace AlleyCat.Tests.Core;

/// <summary>
/// Unit coverage for canonical identifiable identity validation.
/// </summary>
public sealed class IdentityValidatorTests
{
    /// <summary>Lower snake_case identifiers are accepted.</summary>
    [Theory]
    [InlineData("ally")]
    [InlineData("interrogation_room")]
    [InlineData("a1_b2")]
    public void ValidateId_AcceptsLowerSnakeCase(string id)
        => IdentityValidator.ValidateId(id, "identity");

    /// <summary>Identifiers outside the lower snake_case contract are rejected.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Ally")]
    [InlineData("ally-name")]
    [InlineData("ally__name")]
    [InlineData("_ally")]
    public void ValidateId_RejectsNonLowerSnakeCase(string id)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => IdentityValidator.ValidateId(id, "identity"));

        Assert.Contains("lower snake_case", exception.Message);
    }

    /// <summary>Valid type and local IDs form the exact canonical FullId.</summary>
    [Fact]
    public void Validate_AcceptsTypedIdentityAndFullIdUsesExactContract()
    {
        var identity = new FakeIdentifiable("loc", "interrogation_room");

        IdentityValidator.Validate(identity, "identity");

        Assert.Equal("loc:interrogation_room", identity.FullId);
    }

    /// <summary>Canonical FullIds validate both lower snake_case segments and exactly one separator.</summary>
    [Theory]
    [InlineData("char:ally")]
    [InlineData("loc:interrogation_room")]
    public void ValidateFullId_AcceptsCanonicalIdentity(string fullId)
        => IdentityValidator.ValidateFullId(fullId, "identity");

    /// <summary>Malformed FullIds cannot become storage paths or lore references.</summary>
    [Theory]
    [InlineData("ally")]
    [InlineData("char:Ally")]
    [InlineData("char:ally:other")]
    [InlineData("char:../ally")]
    public void ValidateFullId_RejectsMalformedIdentity(string fullId)
        => _ = Assert.Throws<ArgumentException>(() => IdentityValidator.ValidateFullId(fullId, "identity"));

    /// <summary>Types outside the lower snake_case contract are rejected.</summary>
    [Fact]
    public void Validate_RejectsInvalidType()
    {
        var identity = new FakeIdentifiable("Character", "ally");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => IdentityValidator.Validate(identity, "identity"));

        Assert.Contains("Type", exception.Message);
    }

    /// <summary>A canonical-looking FullId cannot disagree with the object's Type and ID.</summary>
    [Fact]
    public void Validate_RejectsCanonicalLookingFullIdThatDoesNotMatchTypeAndId()
    {
        var identity = new FakeIdentifiable("char", "ally", "char:other_character");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => IdentityValidator.Validate(identity, "identity"));

        Assert.Equal("identity", exception.ParamName);
        Assert.Contains("exactly match", exception.Message);
        Assert.Contains("char:other_character", exception.Message);
    }

    private sealed class FakeIdentifiable(string type, string id, string? fullIdOverride = null) : IIdentifiable
    {
        public string Id { get; set; } = id;

        public string Type => type;

        public string FullId => fullIdOverride ?? $"{Type}:{Id}";
    }
}
