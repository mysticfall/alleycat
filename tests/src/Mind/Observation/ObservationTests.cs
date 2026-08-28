using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Xunit;

namespace AlleyCat.Tests.Mind.Observation;

/// <summary>
/// Unit coverage for stable observation semantics and payload boundaries.
/// </summary>
public sealed class ObservationTests
{
    /// <summary>
    /// All speech perspectives share one stable exact semantic key.
    /// </summary>
    [Fact]
    public void ObservedSpeech_UsesUnifiedStableTypeKey()
    {
        var observation = new ObservedSpeech("char:character", "raw-voice", "Hello");

        Assert.Equal("speech.observed", observation.TypeKey);
        Assert.Equal("char:character", observation.ActorId);
        Assert.Equal("raw-voice", observation.VoiceId);
        Assert.Equal("Hello", observation.Content);
    }

    /// <summary>
    /// Importance uses exact actor-to-owner identity while unknown and external speech remain important.
    /// </summary>
    [Theory]
    [InlineData("char:owner", 0f)]
    [InlineData("owner", 1f)]
    [InlineData("char:other", 1f)]
    [InlineData(null, 1f)]
    public void ObservedSpeech_CalculateImportance_IsOwnerRelativeAndOrdinalExact(
        string? actorId,
        float expected)
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var observation = new ObservedSpeech(actorId, "private-device", "Hello");

        Assert.Equal(expected, observation.CalculateImportance(new ObservationContext(owner)));
        Assert.Equal("private-device", observation.VoiceId);
    }

    /// <summary>
    /// ObservedAt defaults to null for records created outside Mind ingestion.
    /// </summary>
    [Fact]
    public void ObservedAt_DefaultsToNull()
    {
        var observation = new ObservedSpeech("char:character", "raw-voice", "Hello");

        Assert.Null(observation.ObservedAt);
    }

    /// <summary>
    /// ObservedAt can be assigned through the object initialiser contract in game-time seconds.
    /// </summary>
    [Fact]
    public void ObservedAt_IsSettableThroughObjectInitialiser()
    {
        const double stamp = 128.5d;
        var observation = new ObservedSpeech("char:character", "raw-voice", "Hello")
        {
            ObservedAt = stamp
        };

        Assert.Equal(stamp, observation.ObservedAt);
    }

    /// <summary>
    /// ObservedAt survives with-cloning on a derived record.
    /// </summary>
    [Fact]
    public void ObservedAt_IsPreservedThroughWithCloning()
    {
        const double stamp = 128.5d;
        var observation = new ObservedSpeech("char:character", "raw-voice", "Hello")
        {
            ObservedAt = stamp
        };

        ObservedSpeech clone = observation with
        {
            Content = "Hi"
        };

        Assert.Equal("Hi", clone.Content);
        Assert.Equal(stamp, clone.ObservedAt);
    }

    /// <summary>Existing observations retain duplicates unless they explicitly opt into suppression.</summary>
    [Fact]
    public void DuplicateContract_DefaultsToAllowWithoutScopeOrSemanticEquivalence()
    {
        var first = new ObservedSpeech("char:character", "raw-voice", "Hello") { ObservedAt = 1d };
        ObservedSpeech second = first with
        {
            ObservedAt = 2d
        };

        Assert.Equal(ObservationDuplicatePolicy.Allow, first.DuplicatePolicy);
        Assert.Null(first.DuplicateScope);
        Assert.False(first.IsSemanticallyEquivalentTo(second));
    }

    /// <summary>Visual descriptions use canonical identity, stable importance, and timestamp-free semantics.</summary>
    [Fact]
    public void ObservedVisualDescription_UsesSpecifiedImportanceScopeAndOrdinalSemanticEquality()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var first = new ObservedVisualDescription("char:subject", "A red coat.") { ObservedAt = 1d };
        var same = new ObservedVisualDescription("char:subject", "A red coat.") { ObservedAt = 2d };
        var changed = new ObservedVisualDescription("char:subject", "A blue coat.");
        var otherSubject = new ObservedVisualDescription("char:other", "A red coat.");

        Assert.Equal("vision.description", first.TypeKey);
        Assert.Equal(1f, first.CalculateImportance(new ObservationContext(owner)));
        Assert.Equal(ObservationDuplicatePolicy.IgnoreEquivalent, first.DuplicatePolicy);
        Assert.Equal("char:subject", first.DuplicateScope);
        Assert.True(first.IsSemanticallyEquivalentTo(same));
        Assert.False(first.IsSemanticallyEquivalentTo(changed));
        Assert.False(first.IsSemanticallyEquivalentTo(otherSubject));
        _ = Assert.Throws<ArgumentException>(() => new ObservedVisualDescription("subject", "Invalid identity"));
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = string.Empty;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }
}
