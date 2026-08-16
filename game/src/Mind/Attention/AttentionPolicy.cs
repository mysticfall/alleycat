using AlleyCat.Core;

namespace AlleyCat.Mind.Attention;

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
