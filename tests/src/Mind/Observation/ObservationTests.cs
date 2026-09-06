using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;
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
        var observation = new ObservedSpeech("char:character", "Hello");

        Assert.Equal("speech.observed", observation.TypeKey);
        Assert.Equal("char:character", observation.ActorId);
        Assert.Equal("Hello", observation.Content);
    }

    /// <summary>Canonical rendering is type-owned, actor-relative, and never exposes speech transport metadata.</summary>
    [Fact]
    public void Render_UsesSafeFallbackAndActorRelativeSpeech()
    {
        FakeCharacter owner = new()
        {
            Id = "owner",
        };
        var fallback = new DefaultObservation
        {
            ObservedAt = 10.25d,
        };
        var self = new ObservedSpeech("char:owner", "Hello");
        var recognised = new ObservedSpeech("char:other", "Hi");
        var unknown = new ObservedSpeech(null, "Who is there?");

        Assert.Equal("((Received test.default event.)) (at 10.2s game time)", fallback.Render(owner));
        Assert.Equal("I said: Hello", self.Render(owner));
        Assert.Equal("Heard char:other say: Hi", recognised.Render(owner));
        string unknownText = unknown.Render(owner);
        Assert.Equal("Heard an unknown speaker say: Who is there?", unknownText);
        Assert.DoesNotContain("VoiceId", unknownText, StringComparison.Ordinal);
    }

    /// <summary>Speech payloads expose semantic fields only; private transport is absent from their public API.</summary>
    [Fact]
    public void ObservedSpeech_DoesNotExposeTransportMetadata()
    {
        string[] formerMembers = ["VoiceId", "SpeechGroupID", "SegmentIndex", "Continued", "CommitIdentity"];

        Assert.All(formerMembers, member => Assert.Null(typeof(ObservedSpeech).GetProperty(member)));
        Assert.NotNull(typeof(ObservedSpeech).GetProperty(nameof(ObservedSpeech.ActorId)));
        Assert.NotNull(typeof(ObservedSpeech).GetProperty(nameof(ObservedSpeech.Content)));
    }

    /// <summary>
    /// Commit identity compares components ordinally and order-sensitively, so only the same tuple matches
    /// (AI-001 TR-49).
    /// </summary>
    [Fact]
    public void ObservationCommitIdentity_ComparesOrdinalOrderedComponents()
    {
        ObservationCommitIdentity first = new("voice-1", "group-1", 2);
        ObservationCommitIdentity same = new("voice-1", "group-1", 2);

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, new ObservationCommitIdentity("voice-1", "group-2", 2));
        Assert.NotEqual(first, new ObservationCommitIdentity("voice-1", "group-1", 3));
        Assert.NotEqual(first, new ObservationCommitIdentity("voice-1", "Group-1", 2));
        Assert.NotEqual(first, new ObservationCommitIdentity("group-1", "voice-1", 2));
        Assert.NotEqual(first, new ObservationCommitIdentity("voice-1", "group-1"));
    }

    /// <summary>Commit identity copies its supplied components, so later mutation cannot rewrite a committed identity.</summary>
    [Fact]
    public void ObservationCommitIdentity_ClonesSuppliedComponents()
    {
        object?[] components = ["voice-1", "group-1", 0];
        ObservationCommitIdentity identity = new(components);
        components[0] = "mutated";

        Assert.Equal(["voice-1", "group-1", 0], identity.Components);
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
        var observation = new ObservedSpeech(actorId, "Hello");

        Assert.Equal(expected, observation.CalculateImportance(new ObservationContext(owner)));
    }

    /// <summary>
    /// Observations never force a fresh turn unless their type explicitly overrides freshness.
    /// </summary>
    [Fact]
    public void Observation_RequiresFreshTurn_DefaultsToFalse()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        ObservationContext context = new(owner);
        var plain = new DefaultObservation();
        var presence = new ObservedVisualPresence("char:subject");
        var description = new ObservedVisualDescription("char:subject", "A red coat.");

        Assert.False(plain.RequiresFreshTurn(context));
        Assert.False(presence.RequiresFreshTurn(context));
        Assert.False(description.RequiresFreshTurn(context));
    }

    /// <summary>
    /// Freshness uses exact ordinal actor-to-owner identity, so only exact self speech avoids a fresh turn.
    /// </summary>
    [Theory]
    [InlineData("char:owner", false)]
    [InlineData("char:other", true)]
    [InlineData(null, true)]
    [InlineData("owner", true)]
    [InlineData("char:owner:extra", true)]
    public void ObservedSpeech_RequiresFreshTurn_IsOwnerRelativeAndOrdinalExact(
        string? actorId,
        bool expected)
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        ObservationContext context = new(owner);
        var observation = new ObservedSpeech(actorId, "Hello");

        Assert.Equal(expected, observation.RequiresFreshTurn(context));
        Assert.Equal(expected ? 1f : 0f, observation.CalculateImportance(context));
    }

    /// <summary>
    /// Owner identity matching is case-sensitive, so the same ID differing only in case requires a fresh turn.
    /// </summary>
    [Theory]
    [InlineData("CHAR:owner")]
    [InlineData("char:Owner")]
    public void ObservedSpeech_RequiresFreshTurn_IsCaseSensitive(string actorId)
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var observation = new ObservedSpeech(actorId, "Hello");

        Assert.True(observation.RequiresFreshTurn(new ObservationContext(owner)));
    }

    /// <summary>
    /// ObservedAt defaults to null for records created outside Mind ingestion.
    /// </summary>
    [Fact]
    public void ObservedAt_DefaultsToNull()
    {
        var observation = new ObservedSpeech("char:character", "Hello");

        Assert.Null(observation.ObservedAt);
    }

    /// <summary>
    /// ObservedAt can be assigned through the object initialiser contract in game-time seconds.
    /// </summary>
    [Fact]
    public void ObservedAt_IsSettableThroughObjectInitialiser()
    {
        const double stamp = 128.5d;
        var observation = new ObservedSpeech("char:character", "Hello")
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
        var observation = new ObservedSpeech("char:character", "Hello")
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

    /// <summary>Ordinary observations are accepted unless an external lifetime policy says otherwise.</summary>
    [Fact]
    public void Observation_DefaultsToAcceptedRetention()
    {
        var first = new ObservedSpeech("char:character", "Hello") { ObservedAt = 1d };
        ObservedSpeech second = first with
        {
            ObservedAt = 2d
        };

        Assert.False(first.IsAttentionOnly);
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
        var observation = new ObservedSpeech("char:speaker", "Hello");

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
        var self = new ObservedSpeech("char:owner", "Hello");
        var unknown = new ObservedSpeech(null, "Hello");

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
        Assert.True(presence.IsAttentionOnly);
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

    /// <summary>Base observations default to accepted retention with no attention effects.</summary>
    [Fact]
    public void Observation_DefaultsToDurableRetentionAndEmptyAttention()
    {
        FakeCharacter owner = new()
        {
            Id = "owner"
        };
        var observation = new DefaultObservation();

        Assert.False(observation.IsAttentionOnly);
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
        Assert.Equal(0.1f, first.CalculateImportance(new ObservationContext(owner)));
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

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }
}
