using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Attention;
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

    /// <summary>Recognised non-self speech contributes one fixed attention effect on its actor.</summary>
    [Fact]
    public void ObservedSpeech_GetAttentionEffects_RecognisedActorContributesFixedAttention()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var observation = new ObservedSpeech("char:speaker", "speaker-voice", "Hello");

        AttentionEffect effect = Assert.Single(observation.GetAttentionEffects(new ObservationContext(owner)));

        Assert.Equal("char:speaker", effect.SubjectFullId);
        Assert.Equal(0.5f, effect.Contribution);
    }

    /// <summary>Self speech and speech from an unknown speaker contribute no attention effects.</summary>
    [Fact]
    public void ObservedSpeech_GetAttentionEffects_SelfAndUnknownSpeakersContributeNone()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var self = new ObservedSpeech("char:owner", "private-device", "Hello");
        var unknown = new ObservedSpeech(null, "private-device", "Hello");

        Assert.Empty(self.GetAttentionEffects(new ObservationContext(owner)));
        Assert.Empty(unknown.GetAttentionEffects(new ObservationContext(owner)));
    }

    /// <summary>Visual presence is transient and contributes one fixed attention effect on its subject.</summary>
    [Fact]
    public void ObservedVisualPresence_IsTransientAndContributesFixedSubjectAttention()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var presence = new ObservedVisualPresence("char:subject");

        Assert.Equal("char:subject", presence.SubjectId);
        Assert.Equal("vision.presence", presence.TypeKey);
        Assert.Equal(ObservationRetention.Transient, presence.Retention);
        AttentionEffect effect = Assert.Single(presence.GetAttentionEffects(new ObservationContext(owner)));
        Assert.Equal("char:subject", effect.SubjectFullId);
        Assert.Equal(0.25f, effect.Contribution);
    }

    /// <summary>Visual-presence subject identity must be a canonical <c>Type:Id</c> FullId.</summary>
    [Theory]
    [InlineData("subject")]
    [InlineData("char:")]
    [InlineData(":subject")]
    [InlineData("char:subject:extra")]
    [InlineData("CHAR:subject")]
    [InlineData(" ")]
    public void ObservedVisualPresence_RejectsNonCanonicalSubjectId(string subjectId)
        => Assert.Throws<ArgumentException>(() => new ObservedVisualPresence(subjectId));

    /// <summary>Base observations default to durable retention with no attention effects.</summary>
    [Fact]
    public void Observation_DefaultsToDurableRetentionAndEmptyAttention()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var observation = new DefaultObservation();

        Assert.Equal(ObservationRetention.Durable, observation.Retention);
        Assert.Empty(observation.GetAttentionEffects(new ObservationContext(owner)));
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

    /// <summary>Minimal observation leaving every optional contract member at its base default.</summary>
    private sealed record DefaultObservation : AlleyCat.Mind.Observation.Observation
    {
        public override string TypeKey => "test.default";

        public override float CalculateImportance(ObservationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return 0f;
        }
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = string.Empty;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }
}
