using System.Collections.ObjectModel;

namespace AlleyCat.Mind.Perception;

/// <summary>One ordered identity-only attention adjustment.</summary>
public sealed record AttentionEffect(string SubjectFullId, float Contribution);

/// <summary>Immutable ordered output of one perception faculty.</summary>
public sealed class PerceptionResult
{
    /// <inheritdoc/>
    public PerceptionResult(IEnumerable<AttentionEffect> attentionEffects, IEnumerable<Observation.Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(attentionEffects);
        ArgumentNullException.ThrowIfNull(observations);
        AttentionEffects = new ReadOnlyCollection<AttentionEffect>([.. attentionEffects]);
        Observations = new ReadOnlyCollection<Observation.Observation>([.. observations]);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AttentionEffect> AttentionEffects
    {
        get;
    }

    /// <inheritdoc/>
    public IReadOnlyList<Observation.Observation> Observations
    {
        get;
    }
}
