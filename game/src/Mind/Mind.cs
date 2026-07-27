using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Core.Logging;
using AlleyCat.Mind.Observation;
using Godot;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind;

/// <summary>
/// Abstract base for NPC mind-like components that can receive player voice events.
/// </summary>
[GlobalClass]
public abstract partial class Mind : Node, IVoiceListener
{
    private static readonly TimeSpan _defaultMaxObservationWait = TimeSpan.FromSeconds(10);

    private readonly Lock _observationStateLock = new();
    private readonly Lock _deferredGodotActionsLock = new();
    private readonly List<AgentObservation> _observationTimeline = [];
    private readonly Queue<PendingObservation> _pendingObservations = [];
    private readonly CancellationTokenSource _nodeLifetimeCancellation = new();
    private Godot.Timer? _schedulingTimer;
    private float _cumulativeObservationImportance;
    private double? _firstPendingObservationTimestamp;
    private double? _lastTurnCompletionTimestamp;
    private CancellationTokenSource? _activeTurnCancellation;
    private bool _schedulingEvaluationQueued;
    private bool _isProcessingObservations;
    private bool _interruptionRequested;
    private bool _immediateReplacementPending;
    private int _nodeLifetimeEnded;
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Enabled setter controls scheduling.")]
    private bool _enabled = true;

    internal Func<CancellationToken, Task>? ObservationBatchClaimedHookForTesting
    {
        get;
        set;
    }

    /// <summary>
    /// Enables player speech handling and observation processing.
    /// </summary>
    [ExportGroup("Settings")]
    [Export]
    public bool Enabled
    {
        get
        {
            lock (_observationStateLock)
            {
                return _enabled;
            }
        }
        set
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            bool hasPendingObservations;
            lock (_observationStateLock)
            {
                if (IsNodeLifetimeEnded)
                {
                    return;
                }

                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                hasPendingObservations = _pendingObservations.Count > 0;
            }

            if (!value)
            {
                StopSchedulingTimer();
                return;
            }

            if (hasPendingObservations)
            {
                QueueSchedulingEvaluation();
            }
        }
    }

    /// <summary>
    /// Maximum time queued observations can wait before processing when their cumulative importance stays below threshold.
    /// </summary>
    [ExportGroup("Runtime")]
    [Export(PropertyHint.Range, "0.05,120,0.05")]
    public float MaxObservationWaitSeconds { get; set; } = (float)_defaultMaxObservationWait.TotalSeconds;

    /// <summary>
    /// Cumulative observation importance that triggers immediate processing.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,100,0.01")]
    public float ObservationImportanceThreshold { get; set; } = 1f;

    /// <summary>
    /// Minimum delay after one turn completes before the next queued turn may start.
    /// </summary>
    [Export(PropertyHint.Range, "0,5,0.05")]
    public float MinimumTurnIntervalSeconds
    {
        get; set;
    }

    /// <summary>
    /// Enables individual high-importance observations to pre-empt an active turn.
    /// </summary>
    [ExportGroup("Interruption")]
    [Export]
    public bool HighImportanceInterruptionEnabled
    {
        get; set;
    }

    /// <summary>
    /// Individual observation importance required to pre-empt an active turn.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,100,0.01")]
    public float HighImportanceInterruptionThreshold { get; set; } = 1f;

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
    public override void _EnterTree()
    {
        if (IsNodeLifetimeEnded)
        {
            _ = CallDeferred(nameof(RejectEndedLifetimeReentry));
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        AddToGroup(IVoiceListener.GroupName);
        _ = EnsureSchedulingTimer();
        if (HasPendingObservations && Enabled)
        {
            QueueSchedulingEvaluation();
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (Interlocked.Exchange(ref _nodeLifetimeEnded, 1) != 0)
        {
            return;
        }

        lock (_observationStateLock)
        {
            _enabled = false;
        }

        StopSchedulingTimer();
        if (_schedulingTimer is { } schedulingTimer)
        {
            schedulingTimer.Timeout -= OnSchedulingTimerTimeout;
        }

        _nodeLifetimeCancellation.Cancel();
        OnNodeLifetimeEnding();
        RemoveFromGroup(IVoiceListener.GroupName);
    }

    /// <summary>
    /// Allows derived minds to settle owned asynchronous work when this node leaves the scene tree.
    /// </summary>
    protected virtual void OnNodeLifetimeEnding()
    {
    }

    /// <summary>
    /// Indicates whether this mind has begun its irreversible exit from the scene tree.
    /// </summary>
    protected bool IsNodeLifetimeEnded => Volatile.Read(ref _nodeLifetimeEnded) != 0;

    /// <summary>
    /// Cancellation token bounded by this node's scene-tree lifetime.
    /// </summary>
    protected CancellationToken NodeLifetimeCancellationToken => _nodeLifetimeCancellation.Token;

    /// <inheritdoc />
    public abstract void ReceiveVoice(string speech, IVoice source);

    /// <summary>
    /// Returns whether an incoming voice event is eligible for this mind.
    /// </summary>
    protected bool ShouldHandleVoice(string speech, IVoice source)
        => Enabled
            && !string.IsNullOrWhiteSpace(speech)
            && !ReferenceEquals(source, Voice);

    /// <summary>
    /// Appends an observation to the timeline and pending importance queue.
    /// </summary>
    protected MindScheduleDecision Observe(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (IsNodeLifetimeEnded)
        {
            return new MindScheduleDecision(false, false);
        }

        ICharacter character = ResolveOwningCharacter();
        var context = new ObservationContext(character);
        float importance = CalculateAndValidateImportance(observation, context);

        return CommitObservations([new PendingObservation(observation, importance)]);
    }

    /// <summary>
    /// Resolves the character that owns this subjective Mind boundary.
    /// </summary>
    protected virtual ICharacter ResolveOwningCharacter()
    {
        for (Node? current = GetParent(); current is not null; current = current.GetParent())
        {
            if (current is ICharacter character)
            {
                return character;
            }
        }

        throw new InvalidOperationException(
            $"Mind node '{Name}' requires an ancestor that implements {typeof(ICharacter).FullName}.");
    }

    internal ICharacter OwningCharacter => ResolveOwningCharacter();

    /// <summary>
    /// Atomically ingests an ordered tool-result observation batch after owning-actor stamping.
    /// </summary>
    internal void IngestToolObservations(IReadOnlyList<AgentObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0)
        {
            return;
        }

        NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
        ICharacter character = ResolveOwningCharacter();
        var context = new ObservationContext(character);
        var pending = new PendingObservation[observations.Count];
        for (int index = 0; index < observations.Count; index++)
        {
            AgentObservation observation = observations[index]
                ?? throw new ArgumentException($"Tool observation at index {index} cannot be null.", nameof(observations));
            AgentObservation stampedObservation = observation is ObservedAction action
                ? action with
                {
                    ActorId = character.FullId
                }
                : observation;
            float importance = CalculateAndValidateImportance(stampedObservation, context);
            pending[index] = new PendingObservation(stampedObservation, importance);
        }

        NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
        _ = CommitObservations(pending, throwWhenLifetimeEnded: true);
    }

    private MindScheduleDecision CommitObservations(
        IReadOnlyList<PendingObservation> observations,
        bool throwWhenLifetimeEnded = false)
    {
        bool shouldEvaluateScheduling;
        bool shouldProcessImmediately;
        CancellationTokenSource? interruptionCancellation = null;

        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded)
            {
                return throwWhenLifetimeEnded
                    ? throw new OperationCanceledException(NodeLifetimeCancellationToken)
                    : new MindScheduleDecision(false, false);
            }

            bool wasPendingQueueEmpty = _pendingObservations.Count == 0;
            bool wasBelowThreshold = _cumulativeObservationImportance < EffectiveObservationImportanceThreshold;
            foreach (PendingObservation pendingObservation in observations)
            {
                _observationTimeline.Add(pendingObservation.Observation);
                _pendingObservations.Enqueue(pendingObservation);
                _cumulativeObservationImportance += pendingObservation.Importance;

                if (_enabled
                    && HighImportanceInterruptionEnabled
                    && _isProcessingObservations
                    && !_interruptionRequested
                    && pendingObservation.Importance >= EffectiveHighImportanceInterruptionThreshold)
                {
                    _interruptionRequested = true;
                    _immediateReplacementPending = true;
                    interruptionCancellation = _activeTurnCancellation;
                }
            }

            if (wasPendingQueueEmpty && observations.Count > 0)
            {
                _firstPendingObservationTimestamp = GetTimestamp();
            }

            if (!_enabled)
            {
                shouldEvaluateScheduling = false;
                shouldProcessImmediately = false;
            }
            else
            {
                bool thresholdReached = _cumulativeObservationImportance >= EffectiveObservationImportanceThreshold;
                shouldEvaluateScheduling = !_isProcessingObservations
                    && (wasPendingQueueEmpty || (wasBelowThreshold && thresholdReached));
                shouldProcessImmediately = shouldEvaluateScheduling && IsEligibleAt(GetTimestamp());
            }
        }

        if (interruptionCancellation is not null)
        {
            try
            {
                interruptionCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Natural completion won the race after pre-emption was committed.
            }
        }

        if (shouldEvaluateScheduling)
        {
            QueueSchedulingEvaluation();
        }

        foreach (PendingObservation observation in observations)
        {
            OnObservationIngested(observation.Observation);
        }

        return new MindScheduleDecision(shouldProcessImmediately, shouldEvaluateScheduling && !shouldProcessImmediately);
    }

    /// <summary>
    /// Notifies derived minds after a successfully committed observation without affecting foreground scheduling.
    /// </summary>
    protected virtual void OnObservationIngested(AgentObservation observation)
    {
    }

    /// <summary>
    /// Gets an atomic, top-level read-only copy of the complete node-lifetime observation timeline membership and order.
    /// Observation records are passed directly under the producer immutability convention.
    /// </summary>
    protected IReadOnlyList<AgentObservation> GetObservationTimelineSnapshot()
    {
        lock (_observationStateLock)
        {
            return new ReadOnlyCollection<AgentObservation>([.. _observationTimeline]);
        }
    }

    /// <summary>
    /// Processes a non-empty batch of queued observations.
    /// </summary>
    protected abstract Task ProcessObservationsAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timelineSnapshot,
        CancellationToken cancellationToken);

    /// <summary>Processes a foreground batch and reports whether it genuinely completed successfully.</summary>
    protected virtual async Task<bool> ProcessForegroundObservationsAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timelineSnapshot,
        CancellationToken cancellationToken)
    {
        await ProcessObservationsAsync(observations, timelineSnapshot, cancellationToken);
        return true;
    }

    /// <summary>
    /// Indicates whether queued observations are waiting for processing.
    /// </summary>
    protected bool HasPendingObservations
    {
        get
        {
            lock (_observationStateLock)
            {
                return _pendingObservations.Count > 0;
            }
        }
    }

    private TimeSpan MaxObservationWait
        => TimeSpan.FromSeconds(Math.Max(MaxObservationWaitSeconds, 0.05f));

    private float EffectiveObservationImportanceThreshold
        => Math.Max(ObservationImportanceThreshold, 0.01f);

    private float EffectiveHighImportanceInterruptionThreshold
        => Math.Max(HighImportanceInterruptionThreshold, 0.01f);

    private TimeSpan MinimumTurnInterval
        => TimeSpan.FromSeconds(Math.Max(MinimumTurnIntervalSeconds, 0f));

    private Godot.Timer EnsureSchedulingTimer()
    {
        if (_schedulingTimer is not null)
        {
            return _schedulingTimer;
        }

        Godot.Timer timer = new()
        {
            Name = "MindSchedulingTimer",
            OneShot = true,
            Autostart = false,
            WaitTime = MaxObservationWait.TotalSeconds,
        };

        timer.Timeout += OnSchedulingTimerTimeout;
        AddChild(timer);
        _schedulingTimer = timer;

        return timer;
    }

    private void QueueSchedulingEvaluation()
    {
        if (IsNodeLifetimeEnded || !IsInsideTree())
        {
            return;
        }

        lock (_deferredGodotActionsLock)
        {
            if (IsNodeLifetimeEnded || _schedulingEvaluationQueued)
            {
                return;
            }

            _schedulingEvaluationQueued = true;
        }

        _ = CallDeferred(nameof(EvaluateSchedulingDeferred));
    }

    private void EvaluateSchedulingDeferred()
    {
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        lock (_deferredGodotActionsLock)
        {
            _schedulingEvaluationQueued = false;
        }

        double delaySeconds;
        lock (_observationStateLock)
        {
            if (!_enabled || _isProcessingObservations || _pendingObservations.Count == 0)
            {
                _schedulingTimer?.Stop();
                return;
            }

            delaySeconds = GetEligibleTimestamp() - GetTimestamp();
        }

        Godot.Timer timer = EnsureSchedulingTimer();
        timer.Stop();

        if (delaySeconds <= 0d)
        {
            _ = ProcessObservationCycleAsync();
            return;
        }

        timer.WaitTime = Math.Max(delaySeconds, 0.001d);
        timer.Start();
    }

    private void StopSchedulingTimer()
    {
        lock (_deferredGodotActionsLock)
        {
            _schedulingEvaluationQueued = false;
        }

        _schedulingTimer?.Stop();
    }

    private void RejectEndedLifetimeReentry()
    {
        if (IsNodeLifetimeEnded && GetParent() is { } parent)
        {
            parent.RemoveChild(this);
        }
    }

    private void OnSchedulingTimerTimeout() => _ = ProcessObservationCycleAsync();

    private async Task ProcessObservationCycleAsync()
    {
        try
        {
            _ = await ProcessPendingObservationsAsync();
        }
        catch (OperationCanceledException) when (IsNodeLifetimeEnded)
        {
        }
        catch (Exception ex)
        {
            if (GameLoggerResolver.TryResolve(out ILogger<Mind>? logger) && logger is not null)
            {
                logger.LogError(ex, "Mind observation processing failed.");
            }
        }
        finally
        {
            QueueSchedulingEvaluation();
        }
    }

    private async Task<bool> ProcessPendingObservationsAsync(CancellationToken cancellationToken = default)
    {
        AgentObservation[] observations;
        IReadOnlyList<AgentObservation> timelineSnapshot;
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            NodeLifetimeCancellationToken);
        CancellationToken processingToken = processingCancellation.Token;

        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded
                || !_enabled
                || _isProcessingObservations
                || _pendingObservations.Count == 0
                || !IsEligibleAt(GetTimestamp()))
            {
                return false;
            }

            _isProcessingObservations = true;
            _activeTurnCancellation = processingCancellation;
            _interruptionRequested = false;
            _immediateReplacementPending = false;
            observations = [.. _pendingObservations.Select(entry => entry.Observation)];
            timelineSnapshot = new ReadOnlyCollection<AgentObservation>([.. _observationTimeline]);
            _pendingObservations.Clear();
            _cumulativeObservationImportance = 0f;
            _firstPendingObservationTimestamp = null;
        }

        try
        {
            if (ObservationBatchClaimedHookForTesting is { } batchClaimedHook)
            {
                await batchClaimedHook(processingToken);
            }

            try
            {
                bool completedSuccessfully = await ProcessForegroundObservationsAsync(
                    observations,
                    timelineSnapshot,
                    processingToken);
                if (completedSuccessfully && !processingToken.IsCancellationRequested && !IsNodeLifetimeEnded)
                {
                    OnForegroundTurnSettled();
                }
            }
            catch (OperationCanceledException) when (IsExpectedInterruption(processingCancellation, cancellationToken))
            {
            }

            return true;
        }
        finally
        {
            lock (_observationStateLock)
            {
                _isProcessingObservations = false;
                if (ReferenceEquals(_activeTurnCancellation, processingCancellation))
                {
                    _activeTurnCancellation = null;
                }

                _interruptionRequested = false;
                if (!IsNodeLifetimeEnded)
                {
                    _lastTurnCompletionTimestamp = GetTimestamp();
                }
            }
        }
    }

    /// <summary>
    /// Notifies derived minds after a foreground processing cycle settles successfully.
    /// </summary>
    protected virtual void OnForegroundTurnSettled()
    {
    }

    private bool IsEligibleAt(double timestamp) => timestamp >= GetEligibleTimestamp();

    private double GetEligibleTimestamp()
    {
        if (_immediateReplacementPending)
        {
            return double.NegativeInfinity;
        }

        double intervalEligibleTimestamp = _lastTurnCompletionTimestamp is { } completionTimestamp
            ? completionTimestamp + MinimumTurnInterval.TotalSeconds
            : double.NegativeInfinity;

        if (_cumulativeObservationImportance >= EffectiveObservationImportanceThreshold)
        {
            return intervalEligibleTimestamp;
        }

        double waitEligibleTimestamp = (_firstPendingObservationTimestamp ?? GetTimestamp())
            + MaxObservationWait.TotalSeconds;
        return Math.Max(waitEligibleTimestamp, intervalEligibleTimestamp);
    }

    private static double GetTimestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private bool IsExpectedInterruption(
        CancellationTokenSource processingCancellation,
        CancellationToken callerCancellation)
    {
        lock (_observationStateLock)
        {
            return _interruptionRequested
                && ReferenceEquals(_activeTurnCancellation, processingCancellation)
                && !IsNodeLifetimeEnded
                && !NodeLifetimeCancellationToken.IsCancellationRequested
                && !callerCancellation.IsCancellationRequested;
        }
    }

    private static float CalculateAndValidateImportance(
        AgentObservation observation,
        ObservationContext context)
    {
        float importance = observation.CalculateImportance(context);
        return !float.IsFinite(importance) || importance < 0f
            ? throw new InvalidOperationException(
                $"Observation '{observation.GetType().FullName}' calculated invalid importance '{importance}'.")
            : importance;
    }

    private readonly record struct PendingObservation(AgentObservation Observation, float Importance);

    /// <summary>
    /// Result of queueing an observation into the base Mind processing cycle.
    /// </summary>
    protected readonly record struct MindScheduleDecision(
        bool ShouldProcessImmediately,
        bool ShouldEnsureIntervalScheduled);
}
