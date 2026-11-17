namespace AlleyCat.Motion;

/// <summary>Elapsed-time intended source state; inference and displayed poses cannot advance this clock.</summary>
public sealed class SourcePhaseClock
{
    /// <summary>Sampling rate of the supported SOMA source interface.</summary>
    public const int FramesPerSecond = 30;
    /// <summary>Immutable request selection; availability never implies cyclic coverage or realised contact.</summary>
    public readonly record struct Selection(long Revision, string Key, double SourceFrame, double FutureFrame,
        int WindowStart, bool Available)
    {
        /// <summary>Elapsed cycle count; zero for undeclared finite content.</summary>
        public long CycleIndex
        {
            get; init;
        }
        /// <summary>Current in-cycle source phase; SourceFrame itself never wraps.</summary>
        public double InCycleFrame
        {
            get; init;
        }
    }

    private long _revision;
    private string _key = "";
    private int _first, _last;
    private double _activated, _stopped, _lastTime;
    private bool _active;
    private int? _experimentalPeriod;
    internal double InitialPhaseFrames
    {
        get; private set;
    }

    /// <summary>Explicit fresh initialisation for a selected admitted range; no implicit phase transfer.</summary>
    public void Activate(string key, int first, int last, double now, int? experimentalPeriod = null, double initialPhaseFrames = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (experimentalPeriod is { } period ? period <= 0 || (long)last - first + 1 != period : first > last - 3)
        {
            throw new ArgumentOutOfRangeException(nameof(last));
        }
        if (!double.IsFinite(initialPhaseFrames) || initialPhaseFrames < 0 ||
            (experimentalPeriod is { } length ? initialPhaseFrames >= length : initialPhaseFrames != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(initialPhaseFrames));
        }
        CheckTime(now);
        _key = key;
        _experimentalPeriod = experimentalPeriod;
        _first = first;
        _last = last;
        InitialPhaseFrames = initialPhaseFrames;
        _activated = _stopped = now;
        _active = true;
        _revision++;
    }

    /// <summary>Source rate 1. Sample-ahead does not commit future state; non-cyclic exhaustion remains visible.</summary>
    public Selection Select(double now, double horizon)
    {
        CheckTime(now);
        if (!double.IsFinite(horizon) || horizon < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(horizon));
        }
        double source = _first + InitialPhaseFrames + (((_active ? now : _stopped) - _activated) * FramesPerSecond);
        double future = source + (horizon * FramesPerSecond);
        bool representable = future is >= int.MinValue and < int.MaxValue;
        int start = representable ? (int)Math.Floor(Math.BitIncrement(future)) : 0;
        long cycle = _experimentalPeriod is { } p ? (long)Math.Floor((source - _first) / p) : 0;
        return new Selection(_revision, _key, source, future, start,
            _active && representable && start >= _first && (_experimentalPeriod.HasValue || start <= _last - 3))
        {
            CycleIndex = cycle,
            InCycleFrame = _experimentalPeriod is { } length ? source - (cycle * length) : source
        };
    }

    /// <summary>Immediately freeze abandoned source intent and invalidate its request snapshots.</summary>
    public void Stop(double now)
    {
        CheckTime(now);
        if (_active)
        {
            _stopped = now;
            _active = false;
            _revision++;
        }
    }

    /// <summary>Ownership only; worker cancellation/result ownership remains independently required.</summary>
    public bool IsCurrent(Selection selection) => _active && selection.Revision == _revision && selection.Key == _key;

    private void CheckTime(double now)
    {
        if (!double.IsFinite(now) || now < _lastTime)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Elapsed simulation time must be finite and monotonic.");
        }
        _lastTime = now;
    }
}
