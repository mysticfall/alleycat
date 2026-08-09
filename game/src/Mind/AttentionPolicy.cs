using AlleyCat.Core;

namespace AlleyCat.Mind;

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

/// <summary>
/// Identity-only attention storage and salience mechanics owned by one Mind.
/// </summary>
internal sealed class AttentionPolicy(Func<double> clock)
{
    private readonly Dictionary<string, float> _entries = new(StringComparer.Ordinal);
    private Func<double> _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private double? _lastTimestamp;

    public void ApplyElapsedDecay(AttentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = ApplyElapsedDecayCore(settings);
    }

    public void Reinforce(string fullID, float contribution, AttentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IdentityValidator.ValidateFullId(fullID, nameof(fullID));
        AttentionSettings.ValidateContribution(contribution, nameof(contribution));

        float current = _entries.GetValueOrDefault(fullID);
        float reinforced = current + ((settings.Maximum - current) * contribution);
        if (reinforced < settings.RetentionThreshold)
        {
            _ = _entries.Remove(fullID);
        }
        else
        {
            _entries[fullID] = reinforced;
        }
    }

    public float GetValue(string fullID, AttentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IdentityValidator.ValidateFullId(fullID, nameof(fullID));
        _ = ApplyElapsedDecayCore(settings);
        return _entries.GetValueOrDefault(fullID);
    }

    public AttentionSnapshot GetSnapshot(AttentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double timestamp = ApplyElapsedDecayCore(settings);
        Dictionary<string, float> values = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, float> entry in _entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            values.Add(entry.Key, entry.Value);
        }

        return new AttentionSnapshot(timestamp, values);
    }

    public IReadOnlyList<string> GetContextEligibleIDs(AttentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = ApplyElapsedDecayCore(settings);
        return [.. _entries
            .Where(entry => entry.Value >= settings.ContextThreshold)
            .Select(static entry => entry.Key)
            .Order(StringComparer.Ordinal)];
    }

    public void SetClock(Func<double> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lastTimestamp = null;
    }

    private double ApplyElapsedDecayCore(AttentionSettings settings)
    {
        double now = _clock();
        if (!double.IsFinite(now))
        {
            throw new InvalidOperationException($"Attention clock returned non-finite timestamp '{now}'.");
        }

        double effectiveNow = _lastTimestamp is { } previous ? Math.Max(now, previous) : now;
        double elapsed = _lastTimestamp is { } last ? effectiveNow - last : 0d;
        _lastTimestamp = effectiveNow;
        if (_entries.Count == 0)
        {
            return effectiveNow;
        }

        float decay = (float)(elapsed * settings.DecayPerSecond);
        foreach (string fullID in _entries.Keys.ToArray())
        {
            float value = Math.Max(0f, _entries[fullID] - decay);
            if (value < settings.RetentionThreshold)
            {
                _ = _entries.Remove(fullID);
            }
            else
            {
                _entries[fullID] = value;
            }
        }

        return effectiveNow;
    }
}
