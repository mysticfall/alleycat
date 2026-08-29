using AlleyCat.Core;
using AlleyCat.Mind.Attention;

namespace AlleyCat.Mind.Observation;

/// <summary>Transient awareness of one currently visible visual subject.</summary>
public sealed record ObservedVisualPresence : Observation
{
    /// <summary>Fixed semantic attention contribution applied to the observed subject.</summary>
    private const float Contribution = 0.25f;

    /// <summary>Stable exact semantic key for transient visual presence.</summary>
    public const string TypeKeyValue = "vision.presence";

    /// <summary>Validates and creates a transient visual-presence observation.</summary>
    public ObservedVisualPresence(string subjectId)
    {
        SubjectId = ValidateSubjectId(subjectId);
    }

    /// <summary>Gets the canonical visual subject FullId.</summary>
    public string SubjectId
    {
        get;
    }

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <inheritdoc />
    public override ObservationRetention Retention => ObservationRetention.Transient;

    /// <summary>
    /// Calculates this observation's scheduling importance. Transient observations bypass importance and prompt
    /// history entirely, so this nominal value never influences scheduling or rendering.
    /// </summary>
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return 0f;
    }

    /// <inheritdoc />
    public override IReadOnlyList<AttentionEffect> GetAttentionEffects(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new AttentionEffect(SubjectId, Contribution)];
    }

    private static string ValidateSubjectId(string subjectId)
    {
        IdentityValidator.ValidateFullId(subjectId, nameof(subjectId));
        return subjectId;
    }
}
