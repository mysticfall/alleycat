using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>Requests work on an authored interval.</summary>
[GlobalClass]
public partial class IntervalContextWorkerTrigger : ContextWorkerTrigger
{
    private Godot.Timer? _timer;

    /// <summary>Interval between requests.</summary>
    [Export(PropertyHint.Range, "0.01,3600,0.01")]
    public float IntervalSeconds { get; set; } = 60f;

    /// <summary>Delay before the first request; defaults to zero.</summary>
    [Export(PropertyHint.Range, "0,3600,0.01")]
    public float InitialDelaySeconds
    {
        get; set;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_timer is not null)
        {
            _timer.Timeout -= OnTimeout;
            _timer.Stop();
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        if (!float.IsFinite(IntervalSeconds) || IntervalSeconds <= 0f
            || !float.IsFinite(InitialDelaySeconds) || InitialDelaySeconds < 0f)
        {
            throw new InvalidOperationException("Interval trigger requires a positive finite interval and non-negative finite initial delay.");
        }

        _timer ??= CreateTimer();
        _timer.WaitTime = Math.Max(InitialDelaySeconds, 0.001f);
        _timer.Start();
    }

    private Godot.Timer CreateTimer()
    {
        Godot.Timer timer = new()
        {
            Name = "ContextWorkerIntervalTimer",
            OneShot = true
        };
        timer.Timeout += OnTimeout;
        AddChild(timer);
        return timer;
    }

    private void OnTimeout()
    {
        RequestRun();
        _timer!.WaitTime = Math.Max(IntervalSeconds, 0.001f);
        _timer.Start();
    }
}
