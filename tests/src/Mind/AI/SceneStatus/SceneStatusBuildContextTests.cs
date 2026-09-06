using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Tests.Mind.AI.SceneStatus;

/// <summary>Unit coverage for immutable request-scoped scene-status build input.</summary>
public sealed class SceneStatusBuildContextTests
{
    /// <summary>Build contexts copy retained and timeline membership instead of retaining mutable source collections.</summary>
    [Fact]
    public void Constructor_CopiesRetainedAndTimelineSnapshotsWithOneStableTimestamp()
    {
        FakeCharacter owner = new("owner");
        AcceptedObservationEntry retained = Entry(1, 42d, new ObservedVisualDescription(owner.FullId, "A yellow coat."));
        AcceptedObservationEntry eventEntry = Entry(2, 43d, new ObservedVisualDescription(owner.FullId, "An updated yellow coat."));
        List<AcceptedObservationEntry> retainedSource = [retained];
        List<AcceptedObservationEntry> timelineSource = [eventEntry];
        AttentionSnapshot attention = new(50d, new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [owner.FullId] = 0.5f,
        });

        SceneStatusBuildContext context = new(
            owner,
            new FakeScene([owner]),
            attention,
            retainedSource,
            timelineSource,
            timestamp: 50d);
        retainedSource.Clear();
        timelineSource.Clear();

        Assert.Same(owner, context.Character);
        Assert.Same(attention, context.Attention);
        Assert.Equal(50d, context.Timestamp);
        Assert.Equal(retained, Assert.Single(context.RetainedLog));
        Assert.Equal(eventEntry, Assert.Single(context.EventTimeline));
        _ = Assert.Throws<NotSupportedException>(
            () => ((IList<AcceptedObservationEntry>)context.RetainedLog).Add(retained));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SceneStatusBuildContext(owner, new FakeScene([owner]), attention, [], [], double.NaN));
    }

    private static AcceptedObservationEntry Entry(long sequenceID, double observedAt, AgentObservation observation)
        => new(
            sequenceID,
            observedAt,
            observation with
            {
                ObservedAt = observedAt,
            },
            new ObservationSchedulingMetadata(0.1f, RequiresFreshTurn: false),
            IsRetained: true);

    private sealed class FakeCharacter(string id) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; } = Transform3D.Identity;
    }

    private sealed class FakeScene(IReadOnlyCollection<ICharacter> characters) : ISceneContext
    {
        public IReadOnlyCollection<ICharacter> Characters => characters;

        public ICharacter Player => Characters.First();

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.SingleOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException($"Missing '{fullId}'.");
    }
}
