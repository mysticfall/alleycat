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

    /// <summary>Malformed local IDs are rejected rather than changed to fit the Unicode grammar.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Ally")]
    [InlineData("ally-name")]
    [InlineData("ally__name")]
    [InlineData("_ally")]
    public void ValidateId_RejectsMalformedLocalId(string id)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => IdentityValidator.ValidateId(id, "identity"));

        Assert.Contains("Unicode letter", exception.Message);
        Assert.Equal("identity", exception.ParamName);
    }

    /// <summary>Valid type and local IDs form the exact canonical FullId.</summary>
    [Fact]
    public void Validate_AcceptsTypedIdentityAndFullIdUsesExactContract()
    {
        var identity = new FakeIdentifiable("loc", "interrogation_room");

        IdentityValidator.Validate(identity, "identity");

        Assert.Equal("loc:interrogation_room", identity.FullId);
    }

    /// <summary>Canonical FullIds validate the ASCII type, Unicode local ID and exactly one separator.</summary>
    [Theory]
    [InlineData("char:ally")]
    [InlineData("loc:interrogation_room")]
    [InlineData("char:강주영")]
    [InlineData("voice:강주영")]
    public void ValidateFullId_AcceptsCanonicalIdentity(string fullId)
        => IdentityValidator.ValidateFullId(fullId, "identity");

    /// <summary>Malformed FullIds cannot become storage paths or lore references.</summary>
    [Theory]
    [InlineData("ally")]
    [InlineData("char:Ally")]
    [InlineData("char:ally:other")]
    [InlineData("char:../ally")]
    [InlineData("char:강주영\n")]
    [InlineData("char\n:강주영")]
    [InlineData("인물:강주영")]
    [InlineData("char:강주영:다른이")]
    [InlineData("char:")]
    [InlineData(":강주영")]
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

    /// <summary>CORE-009 TR-2: first letter, later digit segments, script/scalar and mark compatibility.</summary>
    [Theory]
    [InlineData("강주영")]
    [InlineData("東京_１")]
    [InlineData("жук")]
    [InlineData("élise")]
    [InlineData("e\u0301\u0327")]
    [InlineData("a_1")]
    [InlineData("npc_1")]
    [InlineData("강주영_١2３")]
    [InlineData("a_1\u0301")]
    [InlineData("किरण")]
    [InlineData("a\u20DD")]
    [InlineData("ʰello")]
    [InlineData("\U00010428_1")]
    [InlineData("\U00020000")]
    public void ValidateId_AcceptsUnicodeGrammarWithoutChangingIdentity(string id)
    {
        var identity = new FakeIdentifiable("char", id);
        IdentityValidator.Validate(identity, "identity");
        Assert.Equal(id, identity.Id);
        Assert.Equal($"char:{id}", identity.FullId);
    }

    /// <summary>CORE-009 TR-13: reject cased capitals, unsafe characters and broken segment/base structure.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1a")]
    [InlineData("１a")]
    [InlineData("Élise")]
    [InlineData("강주영A")]
    [InlineData("\u01C5uro")]
    [InlineData("\u03D2")]
    [InlineData("\U00010400")]
    [InlineData("\u0301a")]
    [InlineData("a_\u0301b")]
    [InlineData("a_")]
    [InlineData("a__1")]
    [InlineData("a\n")]
    [InlineData("a\r\n")]
    [InlineData(" a")]
    [InlineData("a b")]
    [InlineData("a\0b")]
    [InlineData("a\u200Db")]
    [InlineData("a\u202Eb")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a.b")]
    [InlineData("a:b")]
    [InlineData("a²")]
    [InlineData("a😀")]
    [InlineData("a\uD800")]
    [InlineData("a\uDC00")]
    public void ValidateId_RejectsInvalidUnicodeGrammar(string? id)
        => _ = Assert.Throws<ArgumentException>(() => IdentityValidator.ValidateId(id, "identity"));

    /// <summary>Unicode local IDs never broaden the technical Type namespace.</summary>
    [Theory]
    [InlineData("char")]
    [InlineData("loc")]
    [InlineData("voice")]
    [InlineData("custom_type_1")]
    public void ValidateType_AcceptsAsciiLowerSnakeCase(string type)
        => IdentityValidator.ValidateType(type, "identity");

    /// <summary>Technical types keep their ASCII grammar and reject trailing newlines.</summary>
    [Theory]
    [InlineData("강주영")]
    [InlineData("é")]
    [InlineData("char\n")]
    [InlineData("Char")]
    [InlineData("1char")]
    [InlineData("char_")]
    [InlineData("char__type")]
    public void ValidateType_RejectsAnythingOutsideAsciiGrammar(string type)
        => _ = Assert.Throws<ArgumentException>(() => IdentityValidator.ValidateType(type, "identity"));

    /// <summary>CORE-009 TR-13: canonical equivalence cannot replace exact Type:Id composition.</summary>
    [Fact]
    public void Validate_RejectsCanonicallyEquivalentButOrdinallyDifferentFullId()
    {
        var identity = new FakeIdentifiable("char", "é", "char:e\u0301");
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => IdentityValidator.Validate(identity, "identity"));
        Assert.Contains("exactly match", exception.Message);
    }

    private sealed class FakeIdentifiable(string type, string id, string? fullIdOverride = null) : IIdentifiable
    {
        public string Id { get; set; } = id;

        public string Type => type;

        public string FullId => fullIdOverride ?? $"{Type}:{Id}";
    }
}
