using System.Collections.ObjectModel;
using System.Globalization;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Time;
using AlleyCat.Mind.Observation;
using Godot;

namespace AlleyCat.Mind.AI.SceneStatus;

/// <summary>Projects current attention and retained visual evidence for characters still present in the fresh scene.</summary>
[GlobalClass]
public sealed partial class AttendedCharacterSceneStatusProjector : SceneStatusProjector
{
    /// <summary>Stable binding ID for the initial attended-character status projection.</summary>
    public const string ProjectorIDValue = "attended-characters";

    /// <inheritdoc />
    public override Type ProjectionType => typeof(AttendedCharactersSceneStatus);

    /// <inheritdoc />
    public override ISceneStatusProjection Project(SceneStatusBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<AttendedCharacterSceneStatus> attendedCharacters = [];
        foreach (string fullID in context.Attention.Values.Keys)
        {
            if (context.Scene.Find(fullID) is not ICharacter character)
            {
                continue;
            }

            IdentityValidator.Validate(character, nameof(context));
            if (!string.Equals(character.FullId, fullID, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Fresh scene resolved attended character '{fullID}' with mismatched FullId '{character.FullId}'.");
            }

            AttendedRelativePositionSceneStatus? relativePosition = null;
            double? relativePositionObservedAt = null;
            foreach (AcceptedObservationEntry entry in context.RetainedLog)
            {
                if (!entry.IsRetained)
                {
                    continue;
                }

                if (entry.Payload is ObservedRelativePosition position
                    && string.Equals(position.SubjectId, fullID, StringComparison.Ordinal))
                {
                    relativePosition = new AttendedRelativePositionSceneStatus(
                        position.Distance,
                        position.SubjectDirection,
                        position.ObserverDirection);
                    relativePositionObservedAt = entry.ObservedAt;
                }
            }

            // The character summary comes from authored lore (playtest R1), never the observed visual-cue
            // description: cue-level detail lives in the gaze cue options, and a missing lore entry renders the
            // bare identity honestly.
            _ = context.LoreDescriptions.TryGetValue(fullID, out string? description);

            attendedCharacters.Add(
                new AttendedCharacterSceneStatus(
                    character.FullId,
                    relativePosition,
                    description,
                    relativePositionObservedAt,
                    GetAgeSeconds(context.Timestamp, relativePositionObservedAt)));
        }

        return new AttendedCharactersSceneStatus(attendedCharacters, context.Timestamp);
    }

    private static double? GetAgeSeconds(double timestamp, double? observedAt)
        => observedAt is { } evidenceTimestamp ? Math.Max(0d, timestamp - evidenceTimestamp) : null;
}

/// <summary>Immutable root rendered by the attended-characters current-scene-status section.</summary>
public sealed record AttendedCharactersSceneStatus(
    IReadOnlyList<AttendedCharacterSceneStatus> AttendedCharacters,
    double Timestamp) : ISceneStatusProjection
{
    /// <summary>Creates an immutable root with deterministic projector-order character entries.</summary>
    public AttendedCharactersSceneStatus(IEnumerable<AttendedCharacterSceneStatus> attendedCharacters, double timestamp)
        : this(
            new ReadOnlyCollection<AttendedCharacterSceneStatus>([.. attendedCharacters ?? throw new ArgumentNullException(nameof(attendedCharacters))]),
            ValidateTimestamp(timestamp))
    {
    }

    /// <summary>
    /// Snapshot game time in invariant <c>F1</c> formatting with a seconds suffix — for example <c>125.0s</c> —
    /// consistent with the event-time suffix, for model-facing rendering of the current game time.
    /// </summary>
    public string CurrentGameTime => Timestamp.ToString("F1", CultureInfo.InvariantCulture) + "s";

    private static double ValidateTimestamp(double timestamp)
        => double.IsFinite(timestamp) && timestamp >= 0d
            ? timestamp
            : throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp, "Status timestamp must be finite and non-negative.");
}

/// <summary>Immutable status for one currently attended scene character.</summary>
public sealed record AttendedCharacterSceneStatus
{
    /// <summary>Creates immutable attended-character status with validated stable identity.</summary>
    public AttendedCharacterSceneStatus(
        string fullId,
        AttendedRelativePositionSceneStatus? relativePosition,
        string? description,
        double? relativePositionObservedAt,
        double? relativePositionAgeSeconds)
    {
        IdentityValidator.ValidateFullId(fullId, nameof(fullId));
        FullId = fullId;
        RelativePosition = relativePosition;
        Description = description;
        RelativePositionObservedAt = relativePositionObservedAt;
        RelativePositionAgeSeconds = relativePositionAgeSeconds;
    }

    /// <summary>Stable exact FullId of the currently attended character.</summary>
    public string FullId
    {
        get;
    }

    /// <summary>Current retained relative-position evidence, when still available.</summary>
    public AttendedRelativePositionSceneStatus? RelativePosition
    {
        get;
    }

    /// <summary>
    /// The character's authored lore description, or null when none is available: the summary line then renders
    /// the bare backticked identity, never fabricated text.
    /// </summary>
    public string? Description
    {
        get;
    }

    /// <summary>Game-time timestamp of <see cref="RelativePosition" /> evidence.</summary>
    public double? RelativePositionObservedAt
    {
        get;
    }

    /// <summary>Age at the status snapshot of <see cref="RelativePosition" /> evidence.</summary>
    public double? RelativePositionAgeSeconds
    {
        get;
    }

    /// <summary>
    /// Model-facing relative phrasing of <see cref="RelativePositionAgeSeconds" /> — for example
    /// <c>2 min 30 sec ago</c> — so the current-scene status states how long ago the position was observed
    /// without leaving the model any arithmetic; null when no age is available.
    /// </summary>
    public string? RelativePositionObservedAgo
        => RelativePositionAgeSeconds is { } ageSeconds ? RelativeGameTime.FormatAgo(ageSeconds) : null;
}

/// <summary>Immutable relative-position evidence retained for one attended character.</summary>
public sealed record AttendedRelativePositionSceneStatus(
    float Distance,
    RelativeDirection SubjectDirection,
    RelativeDirection ObserverDirection);
