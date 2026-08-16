namespace AlleyCat.Mind.Attention;

/// <summary>Validated immutable configuration consumed by one attention-policy operation.</summary>
public sealed record AttentionSettings
{
    private AttentionSettings(
        float maximum,
        float decayPerSecond,
        float retentionThreshold,
        float contextThreshold)
    {
        Maximum = maximum;
        DecayPerSecond = decayPerSecond;
        RetentionThreshold = retentionThreshold;
        ContextThreshold = contextThreshold;
    }

    /// <summary>Gets the maximum value of one attention entry.</summary>
    public float Maximum
    {
        get;
    }

    /// <summary>Gets attention removed per elapsed second.</summary>
    public float DecayPerSecond
    {
        get;
    }

    /// <summary>Gets the threshold below which entries are evicted.</summary>
    public float RetentionThreshold
    {
        get;
    }

    /// <summary>Gets the threshold at or above which entries enter foreground context.</summary>
    public float ContextThreshold
    {
        get;
    }

    internal static AttentionSettings Create(
        float maximum,
        float decayPerSecond,
        float retentionThreshold,
        float contextThreshold)
    {
        ValidateFinitePositive(maximum, nameof(maximum));
        ValidateFiniteNonNegative(decayPerSecond, nameof(decayPerSecond));
        ValidateFiniteNonNegative(retentionThreshold, nameof(retentionThreshold));
        ValidateFiniteNonNegative(contextThreshold, nameof(contextThreshold));
        return retentionThreshold > contextThreshold || contextThreshold > maximum
            ? throw new InvalidOperationException(
                $"Attention thresholds must satisfy retention <= context <= maximum, but found retention '{retentionThreshold}', context '{contextThreshold}', and maximum '{maximum}'.")
            : new AttentionSettings(
                maximum,
                decayPerSecond,
                retentionThreshold,
                contextThreshold);
    }

    internal static void ValidateContribution(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Attention contribution must be finite and in the inclusive range 0..1.");
        }
    }

    private static void ValidateFinitePositive(float value, string propertyName)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            throw new InvalidOperationException(
                $"{propertyName} must be finite and positive, but found '{value}'.");
        }
    }

    private static void ValidateFiniteNonNegative(float value, string propertyName)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new InvalidOperationException(
                $"{propertyName} must be finite and non-negative, but found '{value}'.");
        }
    }
}
