using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
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

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = string.Empty;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }
}
