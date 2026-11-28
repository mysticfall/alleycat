using AlleyCat.Animations;

namespace AlleyCat.Mind.Perception;

/// <summary>
/// One observer's last observed catalogue entry per subject and category (ANIM-003 Mind Integration, Observation Of
/// Changes, items 2–4). A transition target counts as a change when it differs from the last observed entry, or when
/// nothing has been observed yet for that subject and category. The ledger records an entry only when the caller
/// delivers the observation, so undelivered events leave it unchanged.
/// </summary>
internal sealed class AnimationChangeLedger
{
    private readonly Dictionary<(string SubjectId, AnimationCategory Category), string> _lastObserved = [];

    /// <summary>Whether <paramref name="target" /> differs from the last observed entry for the subject and category.</summary>
    public bool IsChange(string subjectId, AnimationCategory category, AnimationEntry target)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(target);
        return !_lastObserved.TryGetValue((subjectId, category), out string? last)
            || !string.Equals(last, target.ID, StringComparison.Ordinal);
    }

    /// <summary>Records <paramref name="target" /> as the last observed entry after its observation was delivered.</summary>
    public void RecordDelivered(string subjectId, AnimationCategory category, AnimationEntry target)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(target);
        _lastObserved[(subjectId, category)] = target.ID;
    }

    /// <summary>The last observed entry id for a subject and category, if any.</summary>
    public string? GetLastObserved(string subjectId, AnimationCategory category)
        => _lastObserved.TryGetValue((subjectId, category), out string? last) ? last : null;
}
