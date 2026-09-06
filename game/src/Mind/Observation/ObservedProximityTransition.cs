using System.Globalization;
using AlleyCat.Character;
using AlleyCat.Core;

namespace AlleyCat.Mind.Observation;

/// <summary>Semantic state transition emitted by a proximity watch.</summary>
public enum ProximityTransition
{
    /// <summary>The watched subject entered the configured inclusive distance.</summary>
    Entered,

    /// <summary>The watched subject left the configured inclusive distance.</summary>
    Left,
}

/// <summary>Persistent semantic event emitted when a subject enters or leaves a watched proximity.</summary>
public sealed record ObservedProximityTransition : Observation
{
    /// <summary>Stable exact semantic key for proximity-transition events.</summary>
    public const string TypeKeyValue = "watch.proximity_transition";

    /// <summary>Creates a validated, condition-owned proximity transition.</summary>
    public ObservedProximityTransition(
        string subjectId,
        ProximityTransition transition,
        float distance,
        float importance,
        bool requiresFreshTurn)
    {
        IdentityValidator.ValidateFullId(subjectId, nameof(subjectId));
        SubjectId = subjectId.StartsWith("char:", StringComparison.Ordinal)
            ? subjectId
            : throw new ArgumentException("Proximity watch subjects must use an exact character FullId.", nameof(subjectId));
        Distance = !float.IsFinite(distance) || distance < 0f
            ? throw new ArgumentOutOfRangeException(nameof(distance), distance, "Distance must be finite and non-negative.")
            : distance;
        Importance = !float.IsFinite(importance) || importance < 0f
            ? throw new ArgumentOutOfRangeException(nameof(importance), importance, "Importance must be finite and non-negative.")
            : importance;
        Transition = transition;
        RequiresImmediateReconsideration = requiresFreshTurn;
    }

    /// <summary>Exact watched character FullId.</summary>
    public string SubjectId
    {
        get;
    }

    /// <summary>Condition-specific entered or left transition.</summary>
    public ProximityTransition Transition
    {
        get;
    }

    /// <summary>Distance at which the transition was observed.</summary>
    public float Distance
    {
        get;
    }

    /// <summary>Condition-owned scheduling importance.</summary>
    public float Importance
    {
        get;
    }

    /// <summary>Whether this transition requires immediate reconsideration.</summary>
    public bool RequiresImmediateReconsideration
    {
        get;
    }

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <inheritdoc />
    protected override string RenderBody(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        string transition = Transition.ToString().ToLowerInvariant();
        return $"{SubjectId} {transition} the proximity condition at "
            + $"{Distance.ToString("0.###", CultureInfo.InvariantCulture)} m.";
    }

    /// <inheritdoc />
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Importance;
    }

    /// <inheritdoc />
    public override bool RequiresFreshTurn(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RequiresImmediateReconsideration;
    }
}
