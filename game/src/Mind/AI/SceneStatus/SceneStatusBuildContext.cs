using System.Collections.ObjectModel;
using AlleyCat.Character;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;

namespace AlleyCat.Mind.AI.SceneStatus;

/// <summary>
/// Immutable input captured for one current-scene-status projection and render.
/// </summary>
public sealed class SceneStatusBuildContext
{
    /// <summary>Creates a validated request-scoped scene-status snapshot.</summary>
    public SceneStatusBuildContext(
        ICharacter character,
        ISceneContext scene,
        AttentionSnapshot attention,
        IReadOnlyList<AcceptedObservationEntry> retainedLog,
        IReadOnlyList<AcceptedObservationEntry> eventTimeline,
        double timestamp)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(attention);
        ArgumentNullException.ThrowIfNull(retainedLog);
        ArgumentNullException.ThrowIfNull(eventTimeline);
        if (!double.IsFinite(timestamp) || timestamp < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                timestamp,
                "Scene-status timestamp must be finite and non-negative.");
        }

        Character = character;
        Scene = scene;
        Attention = attention;
        RetainedLog = new ReadOnlyCollection<AcceptedObservationEntry>([.. retainedLog]);
        EventTimeline = new ReadOnlyCollection<AcceptedObservationEntry>([.. eventTimeline]);
        Timestamp = timestamp;
    }

    /// <summary>Character whose Mind owns this status.</summary>
    public ICharacter Character
    {
        get;
    }

    /// <summary>Fresh scene membership captured for this logical request.</summary>
    public ISceneContext Scene
    {
        get;
    }

    /// <summary>Immutable current attention snapshot keyed by canonical FullId.</summary>
    public AttentionSnapshot Attention
    {
        get;
    }

    /// <summary>Immutable active retained-evidence snapshot in acceptance order.</summary>
    public IReadOnlyList<AcceptedObservationEntry> RetainedLog
    {
        get;
    }

    /// <summary>Immutable persistent event-timeline snapshot in acceptance order.</summary>
    public IReadOnlyList<AcceptedObservationEntry> EventTimeline
    {
        get;
    }

    /// <summary>Stable game-time timestamp for every root projected from this context.</summary>
    public double Timestamp
    {
        get;
    }
}
