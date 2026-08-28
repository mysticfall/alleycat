using AlleyCat.Core;

namespace AlleyCat.Mind.Observation;

/// <summary>Durable focused description of one canonically identified visual subject.</summary>
public sealed record ObservedVisualDescription : Observation
{
    /// <summary>Stable exact semantic key for focused visual descriptions.</summary>
    public const string TypeKeyValue = "vision.description";

    /// <summary>Validates and creates a focused visual description.</summary>
    public ObservedVisualDescription(string subjectId, string description)
    {
        SubjectId = ValidateSubjectId(subjectId);
        Description = ValidateDescription(description);
    }

    /// <summary>Gets the canonical visual subject FullId.</summary>
    public string SubjectId
    {
        get;
    }

    /// <summary>Gets the description rendered from the focused cue.</summary>
    public string Description
    {
        get;
    }

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <inheritdoc />
    public override ObservationDuplicatePolicy DuplicatePolicy => ObservationDuplicatePolicy.IgnoreEquivalent;

    /// <inheritdoc />
    public override string DuplicateScope => SubjectId;

    /// <inheritdoc />
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return 1f;
    }

    /// <inheritdoc />
    public override bool IsSemanticallyEquivalentTo(Observation other)
        => other is ObservedVisualDescription visual
            && string.Equals(SubjectId, visual.SubjectId, StringComparison.Ordinal)
            && string.Equals(Description, visual.Description, StringComparison.Ordinal);

    private static string ValidateSubjectId(string subjectId)
    {
        IdentityValidator.ValidateFullId(subjectId, nameof(subjectId));
        return subjectId;
    }

    private static string ValidateDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description;
    }
}
