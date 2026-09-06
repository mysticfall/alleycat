using System.Collections.ObjectModel;
using AlleyCat.Character;
using AlleyCat.Core;
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
            string? visualDescription = null;
            double? relativePositionObservedAt = null;
            double? visualDescriptionObservedAt = null;
            foreach (AcceptedObservationEntry entry in context.RetainedLog)
            {
                if (!entry.IsRetained)
                {
                    continue;
                }

                switch (entry.Payload)
                {
                    case ObservedRelativePosition position when string.Equals(position.SubjectId, fullID, StringComparison.Ordinal):
                        relativePosition = new AttendedRelativePositionSceneStatus(
                            position.Distance,
                            position.SubjectDirection,
                            position.ObserverDirection);
                        relativePositionObservedAt = entry.ObservedAt;
                        break;
                    case ObservedVisualDescription description when string.Equals(description.SubjectId, fullID, StringComparison.Ordinal):
                        visualDescription = description.Description;
                        visualDescriptionObservedAt = entry.ObservedAt;
                        break;
                    default:
                        break;
                }
            }

            attendedCharacters.Add(
                new AttendedCharacterSceneStatus(
                    character.FullId,
                    relativePosition,
                    visualDescription,
                    relativePositionObservedAt,
                    visualDescriptionObservedAt,
                    GetAgeSeconds(context.Timestamp, relativePositionObservedAt),
                    GetAgeSeconds(context.Timestamp, visualDescriptionObservedAt)));
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
        string? visualDescription,
        double? relativePositionObservedAt,
        double? visualDescriptionObservedAt,
        double? relativePositionAgeSeconds,
        double? visualDescriptionAgeSeconds)
    {
        IdentityValidator.ValidateFullId(fullId, nameof(fullId));
        FullId = fullId;
        RelativePosition = relativePosition;
        VisualDescription = visualDescription;
        RelativePositionObservedAt = relativePositionObservedAt;
        VisualDescriptionObservedAt = visualDescriptionObservedAt;
        RelativePositionAgeSeconds = relativePositionAgeSeconds;
        VisualDescriptionAgeSeconds = visualDescriptionAgeSeconds;
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

    /// <summary>Current retained visual description, when available.</summary>
    public string? VisualDescription
    {
        get;
    }

    /// <summary>Game-time timestamp of <see cref="RelativePosition" /> evidence.</summary>
    public double? RelativePositionObservedAt
    {
        get;
    }

    /// <summary>Game-time timestamp of <see cref="VisualDescription" /> evidence.</summary>
    public double? VisualDescriptionObservedAt
    {
        get;
    }

    /// <summary>Age at the status snapshot of <see cref="RelativePosition" /> evidence.</summary>
    public double? RelativePositionAgeSeconds
    {
        get;
    }

    /// <summary>Age at the status snapshot of <see cref="VisualDescription" /> evidence.</summary>
    public double? VisualDescriptionAgeSeconds
    {
        get;
    }
}

/// <summary>Immutable relative-position evidence retained for one attended character.</summary>
public sealed record AttendedRelativePositionSceneStatus(
    float Distance,
    RelativeDirection SubjectDirection,
    RelativeDirection ObserverDirection);
