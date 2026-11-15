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
    private static readonly IReadOnlyDictionary<string, string> _noLoreDescriptions = new Dictionary<string, string>();

    /// <summary>Creates a validated request-scoped scene-status snapshot.</summary>
    public SceneStatusBuildContext(
        ICharacter character,
        ISceneContext scene,
        AttentionSnapshot attention,
        IReadOnlyList<AcceptedObservationEntry> retainedLog,
        IReadOnlyList<AcceptedObservationEntry> eventTimeline,
        double timestamp,
        IReadOnlyDictionary<string, string>? loreDescriptions = null)
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
        LoreDescriptions = loreDescriptions ?? _noLoreDescriptions;
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

    /// <summary>
    /// Authored lore descriptions keyed by canonical full ID, resolved asynchronously before projection for the
    /// attended-character summaries (playtest R1): characters summarise from lore, not observed visual cues. Empty
    /// when not resolved — projectors then fall back to the bare identity honestly.
    /// </summary>
    public IReadOnlyDictionary<string, string> LoreDescriptions
    {
        get;
    }

    /// <summary>Stable game-time timestamp for every root projected from this context.</summary>
    public double Timestamp
    {
        get;
    }

    /// <summary>Creates one derived snapshot carrying the resolved lore descriptions.</summary>
    public SceneStatusBuildContext WithLoreDescriptions(IReadOnlyDictionary<string, string> loreDescriptions)
        => new(Character, Scene, Attention, RetainedLog, EventTimeline, Timestamp, loreDescriptions);
}
