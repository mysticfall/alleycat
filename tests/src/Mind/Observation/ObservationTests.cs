using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
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
        var observation = new ObservedSpeech("Character", "raw-voice", "Hello");

        Assert.Equal("speech.observed", observation.TypeKey);
        Assert.Equal("Character", observation.ActorId);
        Assert.Equal("raw-voice", observation.VoiceId);
        Assert.Equal("Hello", observation.Content);
    }

    /// <summary>
    /// Importance uses exact actor-to-owner identity while unknown and external speech remain important.
    /// </summary>
    [Theory]
    [InlineData("Owner.Mixed-Case", 0f)]
    [InlineData("owner.mixed-case", 1f)]
    [InlineData("Other", 1f)]
    [InlineData(null, 1f)]
    public void ObservedSpeech_CalculateImportance_IsOwnerRelativeAndOrdinalExact(
        string? actorId,
        float expected)
    {
        FakeCharacter owner = new()
        {
            Id = "Owner.Mixed-Case"
        };
        var observation = new ObservedSpeech(actorId, "private-device", "Hello");

        Assert.Equal(expected, observation.CalculateImportance(new ObservationContext(owner)));
        Assert.Equal("private-device", observation.VoiceId);
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = string.Empty;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, ICharacter? observer)
            => new Dictionary<string, object?>();
    }
}
