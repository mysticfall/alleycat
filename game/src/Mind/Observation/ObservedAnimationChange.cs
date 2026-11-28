using AlleyCat.Animations;
using AlleyCat.Character;
using AlleyCat.Core;

namespace AlleyCat.Mind.Observation;

/// <summary>
/// A focused NPC was seen changing its stance or gait (ANIM-003 Mind Integration, Observation Of Changes). It
/// carries the subject's canonical identity and the new entry's visible description only, never the entry id or
/// tags.
/// </summary>
public sealed record ObservedAnimationChange : Observation
{
    /// <summary>Stable exact semantic key for observed stance and gait changes.</summary>
    public const string TypeKeyValue = "animation.change";

    /// <summary>Validates and creates an observed stance or gait change.</summary>
    public ObservedAnimationChange(string subjectId, AnimationCategory category, string description)
    {
        IdentityValidator.ValidateFullId(subjectId, nameof(subjectId));
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown animation category.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        SubjectId = subjectId;
        Category = category;
        Description = description.Trim();
    }

    /// <summary>Gets the changed subject's canonical FullId.</summary>
    public string SubjectId
    {
        get;
    }

    /// <summary>Gets whether the stance (idle) or the gait (locomotion) changed.</summary>
    public AnimationCategory Category
    {
        get;
    }

    /// <summary>Gets the new entry's external, visible account.</summary>
    public string Description
    {
        get;
    }

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <summary>
    /// Calculates this observation's scheduling importance. The returned value is provisional tuning and
    /// deliberately not normative.
    /// </summary>
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return 0.2f;
    }

    /// <inheritdoc />
    public override bool IsSemanticallyEquivalentTo(Observation other)
        => other is ObservedAnimationChange change
            && string.Equals(SubjectId, change.SubjectId, StringComparison.Ordinal)
            && Category == change.Category
            && string.Equals(Description, change.Description, StringComparison.Ordinal);

    /// <inheritdoc />
    protected override string RenderBody(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return $"Saw that `{SubjectId}` now {Description.TrimEnd('.')}.";
    }
}
