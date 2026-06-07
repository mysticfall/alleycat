using System.Diagnostics.CodeAnalysis;
using AlleyCat.Body.Voice;
using Godot;
using AgentObservation = AlleyCat.AI.Observation.Observation;

namespace AlleyCat.AI;

/// <summary>
/// Abstract base for NPC mind-like components that can receive player voice events.
/// </summary>
[GlobalClass]
public abstract partial class Mind : Node, IVoiceListener
{
    private static readonly TimeSpan _defaultMaxObservationWait = TimeSpan.FromSeconds(10);

    private readonly Lock _observationStateLock = new();
    private readonly Lock _deferredGodotActionsLock = new();
    private readonly Queue<AgentObservation> _observations = [];
    private Godot.Timer? _observationTimer;
    private float _cumulativeObservationWeight;
    private bool _observationTimerStartQueued;
    private bool _isProcessingObservations;
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Enabled setter controls scheduling.")]
    private bool _enabled = true;

    /// <summary>
    /// Enables player speech handling and observation processing.
    /// </summary>
    [ExportGroup("Settings")]
    [Export]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;

            if (!_enabled)
            {
                StopObservationTimer();
                return;
            }

            if (HasPendingObservations)
            {
                EnsureObservationTimerScheduled();
            }
        }
    }

    /// <summary>
    /// Maximum time queued observations can wait before processing when their cumulative weight stays below threshold.
    /// </summary>
    [ExportGroup("Runtime")]
    [Export(PropertyHint.Range, "0.05,120,0.05")]
    public float MaxObservationWaitSeconds { get; set; } = (float)_defaultMaxObservationWait.TotalSeconds;

    /// <summary>
    /// Cumulative observation weight that triggers immediate processing.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,100,0.01")]
    public float ObservationWeightThreshold { get; set; } = 1f;

    /// <summary>
    /// Voice ID accepted as player speech input.
    /// </summary>
    [ExportGroup("Input")]
    [Export]
    public string PlayerVoiceId { get; set; } = "player";

    /// <summary>
    /// NPC voice used for spoken output when a derived mind can speak.
    /// </summary>
    [ExportGroup("Output")]
    [Export]
    public Voice? Voice
    {
        get;
        set;
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        AddToGroup(IVoiceListener.GroupName);
        _ = EnsureObservationTimer();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_observationTimer is { } observationTimer)
        {
            observationTimer.Timeout -= OnObservationTimerTimeout;
        }

        RemoveFromGroup(IVoiceListener.GroupName);
    }

    /// <inheritdoc />
    public abstract void ReceiveVoice(string speech, IVoice source);

    /// <summary>
    /// Returns whether an incoming voice event is eligible for this mind.
    /// </summary>
    protected bool ShouldHandleVoice(string speech, IVoice source)
        => _enabled
            && !string.IsNullOrWhiteSpace(speech)
            && !ReferenceEquals(source, Voice)
            && string.Equals(source.Id, PlayerVoiceId, StringComparison.Ordinal);

    /// <summary>
    /// Queues an observation and schedules processing according to cumulative weight and maximum wait settings.
    /// </summary>
    protected MindScheduleDecision Observe(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool shouldProcessImmediately;

        lock (_observationStateLock)
        {
            _observations.Enqueue(observation);
            _cumulativeObservationWeight += observation.Weight;

            if (!_enabled)
            {
                return new MindScheduleDecision(false, false);
            }

            shouldProcessImmediately = _cumulativeObservationWeight >= EffectiveObservationWeightThreshold;
        }

        if (shouldProcessImmediately)
        {
            _ = ProcessObservationCycleAsync();
        }
        else
        {
            EnsureObservationTimerScheduled();
        }

        return new MindScheduleDecision(shouldProcessImmediately, !shouldProcessImmediately);
    }

    /// <summary>
    /// Processes a non-empty batch of queued observations.
    /// </summary>
    protected abstract Task ProcessObservationsAsync(
        IReadOnlyList<AgentObservation> observations,
        CancellationToken cancellationToken);

    /// <summary>
    /// Indicates whether queued observations are waiting for processing.
    /// </summary>
    protected bool HasPendingObservations
    {
        get
        {
            lock (_observationStateLock)
            {
                return _observations.Count > 0;
            }
        }
    }

    private TimeSpan MaxObservationWait
        => TimeSpan.FromSeconds(Math.Max(MaxObservationWaitSeconds, 0.05f));

    private float EffectiveObservationWeightThreshold
        => Math.Max(ObservationWeightThreshold, 0.01f);

    private Godot.Timer EnsureObservationTimer()
    {
        if (_observationTimer is not null)
        {
            return _observationTimer;
        }

        Godot.Timer timer = new()
        {
            Name = "MaxObservationWaitTimer",
            OneShot = true,
            Autostart = false,
            WaitTime = MaxObservationWait.TotalSeconds,
        };

        timer.Timeout += OnObservationTimerTimeout;
        AddChild(timer);
        _observationTimer = timer;

        return timer;
    }

    private void EnsureObservationTimerScheduled()
    {
        if (!_enabled || !IsInsideTree())
        {
            return;
        }

        lock (_deferredGodotActionsLock)
        {
            if (_observationTimerStartQueued)
            {
                return;
            }

            _observationTimerStartQueued = true;
        }

        _ = CallDeferred(nameof(StartObservationTimerDeferred));
    }

    private void StartObservationTimerDeferred()
    {
        lock (_deferredGodotActionsLock)
        {
            _observationTimerStartQueued = false;
        }

        if (!_enabled || !HasPendingObservations)
        {
            return;
        }

        Godot.Timer timer = EnsureObservationTimer();
        timer.WaitTime = MaxObservationWait.TotalSeconds;
        timer.Start();
    }

    private void StopObservationTimer()
    {
        lock (_deferredGodotActionsLock)
        {
            _observationTimerStartQueued = false;
        }

        _observationTimer?.Stop();
    }

    private void OnObservationTimerTimeout() => _ = ProcessObservationCycleAsync();

    private async Task ProcessObservationCycleAsync()
    {
        try
        {
            _ = await ProcessPendingObservationsAsync();

            if (_enabled && HasPendingObservations)
            {
                EnsureObservationTimerScheduled();
            }
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
        }
    }

    private async Task<bool> ProcessPendingObservationsAsync(CancellationToken cancellationToken = default)
    {
        AgentObservation[] observations;

        lock (_observationStateLock)
        {
            if (!_enabled || _isProcessingObservations || _observations.Count == 0)
            {
                return false;
            }

            _isProcessingObservations = true;
            observations = [.. _observations];
            _observations.Clear();
            _cumulativeObservationWeight = 0f;
        }

        try
        {
            await ProcessObservationsAsync(observations, cancellationToken);
            return true;
        }
        finally
        {
            lock (_observationStateLock)
            {
                _isProcessingObservations = false;
            }
        }
    }

    /// <summary>
    /// Result of queueing an observation into the base Mind processing cycle.
    /// </summary>
    protected readonly record struct MindScheduleDecision(
        bool ShouldProcessImmediately,
        bool ShouldEnsureIntervalScheduled);
}
