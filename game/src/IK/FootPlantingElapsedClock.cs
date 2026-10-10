namespace AlleyCat.IK;

/// <summary>Retains actual physics intervals independently of the planting commit stage.</summary>
internal sealed class FootPlantingElapsedClock
{
    private ulong _observedTick;
    private ulong _committedTick;
    private double _observedDelta;
    private double _pending;
    private bool _hasObservation;
    private bool _hasCommit;
    private string? _failure;

    public void Observe(ulong tick, double delta)
    {
        EnsureValid();
        if (!double.IsFinite(delta) || delta < 0d)
        {
            Fail("Foot planting received an invalid physics interval.");
        }

        if (_hasObservation && tick == _observedTick)
        {
            if (delta != _observedDelta)
            {
                Fail("Foot planting received conflicting physics intervals for the same tick.");
            }

            return;
        }

        ulong previous = _hasObservation ? _observedTick : _committedTick;
        if ((_hasObservation || _hasCommit) && (tick < previous || (_hasCommit && tick > previous + 1)))
        {
            Fail("Foot planting lost physics interval history; an explicit rig rebind is required.");
        }

        _hasObservation = true;
        _observedTick = tick;
        _observedDelta = delta;
        if (_hasCommit && tick > _committedTick)
        {
            _pending += delta;
        }
    }

    public double ForCommit(ulong tick)
    {
        EnsureValid();
        if (!_hasCommit)
        {
            return 0d;
        }

        if (!_hasObservation || _observedTick != tick || tick <= _committedTick)
        {
            Fail("Foot planting has no admitted physics interval for this commit.");
        }

        return _pending;
    }

    public double Peek(ulong tick, double currentDelta)
    {
        EnsureValid();
        if (!_hasCommit)
        {
            return 0d;
        }

        ulong latest = _hasObservation ? _observedTick : _committedTick;
        return tick == latest ? _pending
            : tick != latest + 1 || !double.IsFinite(currentDelta) || currentDelta < 0d
            ? throw new InvalidOperationException("Foot planting cannot preview unobserved physics history.")
            : _pending + currentDelta;
    }

    public void Acknowledge(ulong tick)
    {
        _ = ForCommit(tick);
        _committedTick = tick;
        _hasCommit = true;
        _pending = 0d;
    }

    public void ResetBinding()
    {
        _observedTick = 0;
        _committedTick = 0;
        _observedDelta = 0d;
        _pending = 0d;
        _hasObservation = false;
        _hasCommit = false;
        _failure = null;
    }

    private void EnsureValid()
    {
        if (_failure is not null)
        {
            throw new InvalidOperationException(_failure);
        }
    }

    private void Fail(string message)
    {
        _failure = message;
        throw new InvalidOperationException(message);
    }
}
