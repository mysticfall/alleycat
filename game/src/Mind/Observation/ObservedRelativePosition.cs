using AlleyCat.Core;

namespace AlleyCat.Mind.Observation;

/// <summary>Retained observation of one subject's position relative to the observing character.</summary>
public sealed record ObservedRelativePosition : Observation
{
    /// <summary>Stable exact semantic key for relative-position observations.</summary>
    public const string TypeKeyValue = "vision.relative_position";

    /// <summary>
    /// Numerical-noise guard for comparing two sampled distances for equivalence: two distances closer than this
    /// tolerance are treated as equal float arithmetic noise. This is not perceptual policy — perceptual acuity,
    /// the smallest distance change an observer can notice, stays in the faculty's
    /// <c>RelativePositionPerception.MinimumDistanceChange</c> threshold.
    /// </summary>
    private const float DistanceEqualityTolerance = 1e-4f;

    /// <summary>Validates and creates a relative-position observation.</summary>
    public ObservedRelativePosition(
        string subjectId,
        float distance,
        RelativeDirection subjectDirection,
        RelativeDirection observerDirection)
    {
        SubjectId = ValidateSubjectId(subjectId);
        Distance = ValidateDistance(distance);
        SubjectDirection = subjectDirection;
        ObserverDirection = observerDirection;
    }

    /// <summary>Gets the canonical observed subject FullId.</summary>
    public string SubjectId
    {
        get;
    }

    /// <summary>Gets the distance between the observing character and the subject in world units.</summary>
    public float Distance
    {
        get;
    }

    /// <summary>Gets the direction of the subject relative to the observing character's facing.</summary>
    public RelativeDirection SubjectDirection
    {
        get;
    }

    /// <summary>Gets the direction of the observing character relative to the subject's facing.</summary>
    public RelativeDirection ObserverDirection
    {
        get;
    }

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <summary>
    /// Calculates this observation's scheduling importance. The returned value is provisional tuning and
    /// deliberately not normative; later tuning passes may replace it.
    /// </summary>
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return 0.1f;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The distance comparison uses <see cref="DistanceEqualityTolerance"/> purely as a float-noise guard; it is
    /// not perceptual policy. All other payload fields must match exactly.
    /// </remarks>
    public override bool IsSemanticallyEquivalentTo(Observation other)
        => other is ObservedRelativePosition position
            && string.Equals(SubjectId, position.SubjectId, StringComparison.Ordinal)
            && Math.Abs(Distance - position.Distance) <= DistanceEqualityTolerance
            && SubjectDirection == position.SubjectDirection
            && ObserverDirection == position.ObserverDirection;

    private static string ValidateSubjectId(string subjectId)
    {
        IdentityValidator.ValidateFullId(subjectId, nameof(subjectId));
        return subjectId;
    }

    private static float ValidateDistance(float distance)
    {
        return !float.IsFinite(distance) || distance < 0f
            ? throw new ArgumentOutOfRangeException(
                nameof(distance),
                distance,
                "Distance must be a finite, non-negative value.")
            : distance;
    }
}
