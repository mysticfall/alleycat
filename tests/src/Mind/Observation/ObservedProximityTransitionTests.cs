using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Observation;

/// <summary>Unit coverage for the dedicated, watch-identity-free proximity transition observation.</summary>
public sealed class ObservedProximityTransitionTests
{
    /// <summary>Transitions retain condition-owned scheduling policy without a runtime watch identity.</summary>
    [Fact]
    public void Constructor_RendersCanonicalEnteredTextWithoutWatchIdentity()
    {
        var observation = new ObservedProximityTransition(
            "char:subject",
            ProximityTransition.Entered,
            1.25f,
            importance: 2.5f,
            requiresFreshTurn: true)
        {
            ObservedAt = 42d,
        };

        Assert.Equal(ObservedProximityTransition.TypeKeyValue, observation.TypeKey);
        Assert.Equal("char:subject entered the proximity condition at 1.25 m. (at 42.0s game time)", observation.Render(new FakeCharacter()));
        Assert.Equal(2.5f, observation.CalculateImportance(new ObservationContext(new FakeCharacter())));
        Assert.True(observation.RequiresFreshTurn(new ObservationContext(new FakeCharacter())));
        Assert.Null(typeof(ObservedProximityTransition).GetProperty("WatchId"));
    }

    /// <summary>Condition data rejects invalid subject IDs, distances, and scheduling policy values.</summary>
    [Fact]
    public void Constructor_RejectsInvalidConditionData()
    {
        _ = Assert.Throws<ArgumentException>(() => new ObservedProximityTransition(
            "item:subject", ProximityTransition.Left, 1f, 1f, requiresFreshTurn: false));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObservedProximityTransition(
            "char:subject", ProximityTransition.Left, float.NaN, 1f, requiresFreshTurn: false));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ObservedProximityTransition(
            "char:subject", ProximityTransition.Left, 1f, -1f, requiresFreshTurn: false));
    }

    private sealed class FakeCharacter : ICharacter
    {
        public string Id { get; set; } = "owner";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; } = Transform3D.Identity;
    }
}
