namespace AlleyCat.Models;

/// <summary>
/// v1 policy: fair round-robin across consumers with pending items and FIFO within one consumer. The consumer served
/// least recently goes next, so every consumer with pending items is served before any consumer receives a second
/// turn, and a consumer that resubmits immediately after each completion cannot starve the others.
/// </summary>
public sealed class RoundRobinSchedulingPolicy : IModelSchedulingPolicy
{
    private const int PruneThreshold = 32;

    private readonly Dictionary<ModelConsumerId, long> _lastServed = [];
    private readonly List<ModelConsumerId> _pruned = [];
    private long _turn;

    /// <inheritdoc />
    public int SelectNext(IReadOnlyList<ModelWorkItem> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Count == 0)
        {
            throw new ArgumentException("At least one pending item is required.", nameof(pending));
        }

        int selected = 0;
        long selectedServed = long.MaxValue;
        for (int i = 0; i < pending.Count; i++)
        {
            // Never-served consumers count as turn 0. Strict comparison keeps the earliest item of the chosen
            // consumer (FIFO) and breaks ties between consumers by submission order.
            long served = _lastServed.GetValueOrDefault(pending[i].Consumer);
            if (served < selectedServed)
            {
                selected = i;
                selectedServed = served;
            }
        }

        _lastServed[pending[selected].Consumer] = ++_turn;
        PruneOlderThan(selectedServed);
        return selected;
    }

    // Consumers served before every pending consumer would win the next turn anyway, so forgetting them keeps the
    // table bounded by released consumers without changing fairness.
    private void PruneOlderThan(long turn)
    {
        if (_lastServed.Count <= PruneThreshold)
        {
            return;
        }

        foreach ((ModelConsumerId consumer, long served) in _lastServed)
        {
            if (served < turn)
            {
                _pruned.Add(consumer);
            }
        }

        foreach (ModelConsumerId consumer in _pruned)
        {
            _ = _lastServed.Remove(consumer);
        }

        _pruned.Clear();
    }
}
