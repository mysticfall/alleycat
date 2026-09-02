using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Core.Time;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind;

/// <summary>
/// Abstract base for NPC mind-like components that serially interpret percepts, commit faculty-emitted observations
/// as independent atomic units, maintain an ordered observation timeline, and accumulate notable observations for
/// delivery to the NPC's agent session.
/// </summary>
[GlobalClass]
public abstract partial class Mind : Node
{
    /// <summary>Reason one observation wait completed (AI-002 TR-33).</summary>
    internal enum ObservationWaitWake
    {
        /// <summary>The requested wait duration elapsed without a qualifying wake.</summary>
        QuietExpiry,

        /// <summary>An attended speaker finished speaking (AI-001 TR-34).</summary>
        AttendedSpeakerFinished,

        /// <summary>Accumulated importance reached the configured threshold (AI-001 TR-6).</summary>
        ThresholdCrossed,

        /// <summary>
        /// A fresh observation upgraded the complete accumulation regardless of cumulative importance (AI-001 TR-43).
        /// </summary>
        FreshObservation,
    }

    /// <summary>Delivery urgency of one pending observation window (AI-001 TR-44).</summary>
    internal enum ObservationDeliveryUrgency
    {
        /// <summary>Threshold-qualified ordinary delivery that never cancels session work.</summary>
        Ordinary = 0,

        /// <summary>Fresh-turn urgency that bypasses the importance threshold and replaces stale reasoning.</summary>
        Fresh = 1,
    }

    /// <summary>
    /// Post-commit delivery signal (AI-001 TR-44): the pending window's delivery urgency and whether an active wait
    /// owns the delivery instead of the signalled runtime.
    /// </summary>
    /// <param name="Urgency">Delivery urgency of the newly deliverable window; fresh dominates ordinary.</param>
    /// <param name="WaitOwned">Whether an active wait was woken to deliver the window through its own result.</param>
    internal readonly record struct ObservationDeliverySignal(ObservationDeliveryUrgency Urgency, bool WaitOwned);

    /// <summary>
    /// Registered active wait woken through its normal completion mechanism by attended-speaker-finished cues,
    /// threshold crossings, or fresh observations — never by cancelling the wait's token.
    /// </summary>
    private sealed class ActiveWait
    {
        public ActiveWait()
        {
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<ObservationWaitWake> Completion
        {
            get;
        }

        public bool Settled
        {
            get; private set;
        }

        public bool TryWake(ObservationWaitWake wake)
        {
            lock (this)
            {
                if (Settled)
                {
                    return false;
                }

                Settled = true;
            }

            return Completion.TrySetResult(wake);
        }
    }

    private static readonly TimeSpan _defaultMaxObservationWait = TimeSpan.FromSeconds(10);

    private readonly Lock _observationStateLock = new();
    private readonly Lock _perceptionQueueLock = new();
    private readonly Lock _deferredGodotActionsLock = new();
    private readonly Lock _speechVoiceSubscriptionLock = new();
    private readonly List<AgentObservation> _observationTimeline = [];
    private readonly List<PendingObservation> _notableAccumulation = [];
    private readonly CancellationTokenSource _nodeLifetimeCancellation = new();
    private readonly AttentionPolicy _attention = new(GetStopwatchSeconds);
    private readonly Queue<PerceptionWork> _perceptionQueue = [];
    private readonly Dictionary<ISense, Action<IPercept>> _senseHandlers = [];
    private readonly List<IPerception> _observedFaculties = [];
    private readonly HashSet<IVoice> _subscribedSpeechVoices = [];
    private readonly Dictionary<IVoice, ICharacter?> _speechVoiceOwners = [];
    private readonly ConcurrentQueue<IVoice> _speechStartNotifications = new();
    private ISense[] _senses = [];
    private IReadOnlyDictionary<Type, IPerception[]> _perceptionBindings = new Dictionary<Type, IPerception[]>();
    private IComponentProjectionNotifier? _componentProjectionNotifier;
    private Func<ISceneContext> _sceneContextLoader = LoadCurrentSceneContext;
    private Func<IGameClock> _gameClockLoader = LoadDefaultGameClock;
    private ActiveWait? _activeWait;
    private TaskCompletionSource _attendedSpeakerPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ICharacter? _cachedOwningCharacter;
    private float _cumulativeNotableImportance;
    private bool _notablePending;
    private bool _freshUrgencyPending;
    private bool _speechSubscriptionEvaluationQueued;
    private int _nodeLifetimeEnded;
    private bool _perceptionDrainRunning;
    private Task _perceptionDrainTask = Task.CompletedTask;
    private Action? _beforePerceptionEnqueueForTesting;
    private ILogger<Mind>? _logger;
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Enabled setter controls delivery.")]
    private bool _enabled = true;

    /// <summary>
    /// Occurs after a committed batch raises the pending accumulation's delivery urgency (AI-001 TR-44): carries
    /// delivery urgency — ordinary threshold-qualified delivery versus fresh-turn urgency — and whether an active
    /// wait owns the delivery instead of the signalled runtime (AI-002 TR-41).
    /// </summary>
    internal event Action<ObservationDeliverySignal>? ObservationDeliverySignalled;

    /// <summary>
    /// Occurs for textless external automatic-segment lifecycle transitions. These notifications intentionally bypass
    /// perception, observation, attention, timeline, delivery, and wait processing.
    /// </summary>
    internal event Action<SpeechSegmentLifecycleNotification>? SpeechSegmentLifecycleNotified;

    /// <summary>
    /// Enables stimulus intake, timeline ingestion, and notable-observation delivery.
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

            lock (_observationStateLock)
            {
                if (IsNodeLifetimeEnded || _enabled == value)
                {
                    return;
                }

                _enabled = value;
                if (value && (_notablePending || _freshUrgencyPending))
                {
                    // Delivery resumes for the preserved accumulation — including any retained fresh urgency
                    // (AI-001 TR-5): a held deliverable window wakes an active wait. When no wait is active the
                    // window stays held for the next wait call or delivery claim.
                    _ = _activeWait?.TryWake(
                        _freshUrgencyPending ? ObservationWaitWake.FreshObservation : ObservationWaitWake.ThresholdCrossed);
                }
            }
        }
    }

    /// <summary>
    /// Maximum time a single <c>wait</c> call can stay below the importance threshold before quiet expiry.
    /// </summary>
    [ExportGroup("Runtime")]
    [Export(PropertyHint.Range, "0.05,120,0.05")]
    public float MaxObservationWaitSeconds { get; set; } = (float)_defaultMaxObservationWait.TotalSeconds;

    /// <summary>
    /// Cumulative observation importance that makes the accumulation window notable.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,100,0.01")]
    public float ObservationImportanceThreshold { get; set; } = 1f;

    /// <summary>Maximum value of one attention entry.</summary>
    [ExportGroup("Attention")]
    [Export(PropertyHint.Range, "0.01,100,0.01,or_greater")]
    public float AttentionMaximum { get; set; } = 1f;

    /// <summary>Attention removed per elapsed second.</summary>
    [Export(PropertyHint.Range, "0,10,0.01,or_greater")]
    public float AttentionDecayPerSecond { get; set; } = 0.1f;

    /// <summary>Entries strictly below this value are forgotten.</summary>
    [Export(PropertyHint.Range, "0,100,0.01,or_greater")]
    public float AttentionRetentionThreshold { get; set; } = 0.05f;

    /// <summary>Entries at or above this separate value enter foreground context.</summary>
    [Export(PropertyHint.Range, "0,100,0.01,or_greater")]
    public float AttentionContextThreshold { get; set; } = 0.25f;

    /// <inheritdoc />
    public override void _EnterTree()
    {
        if (IsNodeLifetimeEnded)
        {
            _ = CallDeferred(nameof(RejectEndedLifetimeReentry));
            return;
        }

        Volatile.Write(ref _cachedOwningCharacter, ResolveOwningCharacter());
        _logger = GameLoggerResolver.ResolveRequired<Mind>();
        SubscribeToComponentProjectionRefreshes();
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        Volatile.Write(ref _cachedOwningCharacter, ResolveOwningCharacter());
        SubscribeToComponentProjectionRefreshes();
        if (_componentProjectionNotifier is null || _componentProjectionNotifier.HasComponentProjection)
        {
            ActivatePerceptions();
        }
        RefreshSpeechVoiceSubscriptions();
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

        UnsubscribeFromComponentProjectionRefreshes();
        UnsubscribeFromSenses();
        UnsubscribeFromFaculties();
        UnsubscribeFromSpeechVoices();

        // One irreversible lifetime boundary: cancels active waits, session activity, and cue subscriptions so no
        // deferred callback accesses Mind services after exit (AI-001 TR-18).
        _nodeLifetimeCancellation.Cancel();
        Volatile.Write(ref _perceptionBindings, new Dictionary<Type, IPerception[]>());
        lock (_perceptionQueueLock)
        {
            _perceptionQueue.Clear();
        }
        OnNodeLifetimeEnding();
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
    /// Indicates whether this mind has begun its irreversible exit from the scene tree.
    /// </summary>
    internal bool HasNodeLifetimeEnded => IsNodeLifetimeEnded;

    /// <summary>
    /// Cancellation token bounded by this node's scene-tree lifetime.
    /// </summary>
    protected CancellationToken NodeLifetimeCancellationToken => _nodeLifetimeCancellation.Token;

    private void OnPerceived(IPercept percept, IPerception[] faculties)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(faculties);
        if (IsNodeLifetimeEnded || !Enabled)
        {
            return;
        }

        Volatile.Read(ref _beforePerceptionEnqueueForTesting)?.Invoke();
        EnqueuePerceptionWork(new QueuedPercept(percept, faculties));
    }

    /// <summary>
    /// Trivial faculty observation intake (AI-006 TR-22/23): validates and enqueues one observation work item without
    /// interpreting, committing, or blocking the emitting faculty.
    /// </summary>
    private void OnFacultyObserved(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (IsNodeLifetimeEnded || !Enabled)
        {
            return;
        }

        EnqueuePerceptionWork(new QueuedObservation(observation));
    }

    /// <summary>
    /// Enqueues one serial perception-work item and starts the drain worker when idle, preserving enqueue order across
    /// percepts and observations (AI-001 TR-29, AI-006 TR-34).
    /// </summary>
    private void EnqueuePerceptionWork(PerceptionWork work)
    {
        lock (_perceptionQueueLock)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            _perceptionQueue.Enqueue(work);
            if (!_perceptionDrainRunning)
            {
                _perceptionDrainRunning = true;
                _perceptionDrainTask = DrainPerceptionsAsync();
            }
        }
    }

    private async Task DrainPerceptionsAsync()
    {
        await Task.Yield();
        while (true)
        {
            PerceptionWork queued;
            lock (_perceptionQueueLock)
            {
                if (IsNodeLifetimeEnded)
                {
                    SettlePerceptionDrainUnderLock();
                    return;
                }

                if (_perceptionQueue.Count == 0)
                {
                    _perceptionDrainRunning = false;
                    return;
                }

                queued = _perceptionQueue.Dequeue();
            }

            try
            {
                await ProcessPerceptionWorkAsync(queued);
            }
            catch (OperationCanceledException) when (IsNodeLifetimeEnded || NodeLifetimeCancellationToken.IsCancellationRequested)
            {
                lock (_perceptionQueueLock)
                {
                    SettlePerceptionDrainUnderLock();
                }
                return;
            }
            catch (Exception) when (IsNodeLifetimeEnded)
            {
                lock (_perceptionQueueLock)
                {
                    SettlePerceptionDrainUnderLock();
                }
                return;
            }
            catch (Exception exception)
            {
                ILogger<Mind> logger = _logger
                    ?? throw new InvalidOperationException("Mind perception fault logging requires an active logger.");
                (string workKind, Type workType) = DescribePerceptionWork(queued);
                logger.LogError(
                    exception,
                    "Mind {MindPath} failed to process {WorkKind} '{WorkType}'; the item was discarded, earlier commits stand, and later queue items continue.",
                    GetPath(),
                    workKind,
                    workType);
            }
        }
    }

    private void SettlePerceptionDrainUnderLock()
    {
        _perceptionQueue.Clear();
        _perceptionDrainRunning = false;
    }

    private ValueTask ProcessPerceptionWorkAsync(PerceptionWork work) => work switch
    {
        QueuedPercept queuedPercept => ProcessPerceptAsync(queuedPercept),
        QueuedObservation queuedObservation => ProcessObservationAsync(queuedObservation),
        _ => throw new ArgumentOutOfRangeException(nameof(work)),
    };

    private async ValueTask ProcessPerceptAsync(QueuedPercept queued)
    {
        CancellationToken cancellationToken = NodeLifetimeCancellationToken;
        ThrowIfPerceptionLifetimeEnded(cancellationToken);
        var context = new PerceptionContext(ResolveOwningCharacter(), _sceneContextLoader());
        foreach (IPerception faculty in queued.Faculties)
        {
            ThrowIfPerceptionLifetimeEnded(cancellationToken);
            await faculty.PerceiveAsync(queued.Percept, context, cancellationToken);
            ThrowIfPerceptionLifetimeEnded(cancellationToken);
        }
    }

    /// <summary>
    /// Commits one faculty-emitted observation as one independent atomic unit (AI-001 TR-30/41, AI-006 TR-33–35): its
    /// attention effects apply together with, for durable observations, ingestion effects, or not at all.
    /// </summary>
    private ValueTask ProcessObservationAsync(QueuedObservation queued)
    {
        CancellationToken cancellationToken = NodeLifetimeCancellationToken;
        ThrowIfPerceptionLifetimeEnded(cancellationToken);

        AgentObservation observation = queued.Observation;
        var context = new ObservationContext(ResolveOwningCharacter());
        IReadOnlyList<AttentionEffect> effects = observation.GetAttentionEffects(context);
        for (int index = 0; index < effects.Count; index++)
        {
            AttentionEffect effect = effects[index]
                ?? throw new ArgumentException($"Observation attention effect at index {index} cannot be null.", nameof(observation));
            IdentityValidator.ValidateFullId(effect.SubjectFullId, nameof(observation));
            AttentionSettings.ValidateContribution(effect.Contribution, nameof(observation));
        }

        ThrowIfPerceptionLifetimeEnded(cancellationToken);
        AttentionSettings attentionSettings = CreateAttentionSettings();
        if (observation.Retention == ObservationRetention.Transient)
        {
            // Transient observations apply attention atomically and nothing else: no stamp, duplicate filtering,
            // timeline entry, notable accumulation, or notification (AI-001 TR-41, AI-006 TR-35).
            lock (_observationStateLock)
            {
                if (IsNodeLifetimeEnded)
                {
                    return ValueTask.CompletedTask;
                }

                ApplyAttentionEffectsLocked(effects, attentionSettings);
            }

            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IngestObservations([observation], context, beforeCommit: () => ApplyAttentionEffectsLocked(effects, attentionSettings));
        return ValueTask.CompletedTask;
    }

    /// <summary>Applies elapsed decay then sequential reinforcement; callers must hold the observation state lock.</summary>
    private void ApplyAttentionEffectsLocked(IReadOnlyList<AttentionEffect> effects, AttentionSettings attentionSettings)
    {
        _attention.ApplyElapsedDecay(attentionSettings);
        foreach (AttentionEffect effect in effects)
        {
            ReinforceAttention(effect.SubjectFullId, effect.Contribution, attentionSettings);
        }
    }

    private static (string WorkKind, Type WorkType) DescribePerceptionWork(PerceptionWork work) => work switch
    {
        QueuedPercept queuedPercept => ("percept", queuedPercept.Percept.GetType()),
        QueuedObservation queuedObservation => ("observation", queuedObservation.Observation.GetType()),
        _ => throw new ArgumentOutOfRangeException(nameof(work)),
    };

    private void ThrowIfPerceptionLifetimeEnded(CancellationToken cancellationToken)
    {
        if (IsNodeLifetimeEnded)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Waits until all percepts and observations accepted before this call have settled.</summary>
    internal async Task DrainPerceptionsForTestingAsync()
    {
        while (true)
        {
            Task drain;
            lock (_perceptionQueueLock)
            {
                drain = _perceptionDrainTask;
            }

            await drain;
            lock (_perceptionQueueLock)
            {
                if (!_perceptionDrainRunning && _perceptionQueue.Count == 0)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Installs a deterministic test seam immediately before perception intake acquires its queue lock.</summary>
    internal void SetBeforePerceptionEnqueueForTesting(Action? callback)
        => Volatile.Write(ref _beforePerceptionEnqueueForTesting, callback);

    /// <summary>Gets the number of percepts and observations awaiting processing for deterministic lifetime tests.</summary>
    internal int GetPendingPerceptionCountForTesting()
    {
        lock (_perceptionQueueLock)
        {
            return _perceptionQueue.Count;
        }
    }

    private void ActivatePerceptions()
    {
        AttentionSettings _ = CreateAttentionSettings();
        ICharacter character = ResolveOwningCharacter();
        ISense[] senses = [.. character.Components.OfType<ISense>()];
        IPerception[] faculties = [.. GetChildren().OfType<IPerception>()];
        var bindings = new Dictionary<Type, IPerception[]>();
        var declaredTypes = new HashSet<Type>();
        foreach (ISense sense in senses)
        {
            foreach (Type perceptType in sense.PerceptTypes)
            {
                if (perceptType is null
                    || perceptType.IsAbstract
                    || perceptType.IsInterface
                    || !typeof(IPercept).IsAssignableFrom(perceptType)
                    || !declaredTypes.Add(perceptType))
                {
                    throw new InvalidOperationException($"Mind '{GetPath()}' requires each configured sense to declare unique exact IPercept runtime types.");
                }
            }
        }

        foreach (IPerception faculty in faculties)
        {
            Type perceptType = faculty.PerceptType;
            if (perceptType is null
                || !typeof(IPercept).IsAssignableFrom(perceptType)
                || !faculty.GetType().GetInterfaces().Any(type => type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(IPerception<>)
                    && type.GenericTypeArguments[0] == perceptType))
            {
                throw new InvalidOperationException($"Mind '{GetPath()}' has an invalid perception faculty declaration for '{perceptType?.FullName}'.");
            }
        }

        foreach (Type declaredType in declaredTypes)
        {
            IPerception[] matching = [.. faculties.Where(faculty => faculty.PerceptType.IsAssignableFrom(declaredType))];
            if (matching.Length == 0)
            {
                throw new InvalidOperationException($"Mind '{GetPath()}' requires at least one perception faculty for configured percept type '{declaredType.FullName}'.");
            }

            bindings.Add(declaredType, matching);
        }

        UnsubscribeFromSenses();
        UnsubscribeFromFaculties();
        foreach (IPerception faculty in faculties)
        {
            faculty.Observed += OnFacultyObserved;
            _observedFaculties.Add(faculty);
        }

        Volatile.Write(ref _perceptionBindings, bindings);

        _senses = senses;
        foreach (ISense sense in _senses)
        {
            void handler(IPercept percept)
            {
                if (!sense.PerceptTypes.Contains(percept.GetType()))
                {
                    throw new InvalidOperationException($"Sense '{sense.GetType().FullName}' published undeclared percept type '{percept.GetType().FullName}'.");
                }

                IReadOnlyDictionary<Type, IPerception[]> currentBindings = Volatile.Read(ref _perceptionBindings);
                IPerception[] snapshot = currentBindings.GetValueOrDefault(percept.GetType())
                    ?? throw new InvalidOperationException($"Mind '{GetPath()}' received unbound percept type '{percept.GetType().FullName}'.");
                OnPerceived(percept, snapshot);
            }
            _senseHandlers.Add(sense, handler);
            sense.Perceived += handler;
        }
    }

    private void SubscribeToComponentProjectionRefreshes()
    {
        if (_componentProjectionNotifier is not null)
        {
            return;
        }

        ICharacter character = ResolveOwningCharacter();
        if (character is not IComponentProjectionNotifier notifier)
        {
            return;
        }

        _componentProjectionNotifier = notifier;
        notifier.ComponentsRefreshed += OnComponentProjectionRefreshed;
    }

    private void UnsubscribeFromComponentProjectionRefreshes()
    {
        if (_componentProjectionNotifier is { } notifier)
        {
            notifier.ComponentsRefreshed -= OnComponentProjectionRefreshed;
            _componentProjectionNotifier = null;
        }
    }

    private void OnComponentProjectionRefreshed()
    {
        if (!IsNodeLifetimeEnded)
        {
            ActivatePerceptions();
            RefreshSpeechVoiceSubscriptions();
        }
    }

    private void UnsubscribeFromSenses()
    {
        foreach (KeyValuePair<ISense, Action<IPercept>> entry in _senseHandlers)
        {
            entry.Key.Perceived -= entry.Value;
        }

        _senses = [];
        _senseHandlers.Clear();
    }

    /// <summary>
    /// Removes every faculty observation-event subscription so rebind and exit never duplicate or outlive delivery
    /// (AI-001 TR-27, AI-006 TR-23).
    /// </summary>
    private void UnsubscribeFromFaculties()
    {
        foreach (IPerception faculty in _observedFaculties)
        {
            faculty.Observed -= OnFacultyObserved;
        }

        _observedFaculties.Clear();
    }

    /// <summary>Reinforces one canonical identity using the exact configured policy.</summary>
    protected void ReinforceAttention(string fullID, float contribution, AttentionSettings attentionSettings)
        => _attention.Reinforce(fullID, contribution, attentionSettings);

    /// <summary>Gets one decayed attention value, or zero when no retained entry exists.</summary>
    public float GetAttention(string fullID)
    {
        AttentionSettings settings = CreateAttentionSettings();
        return _attention.GetValue(fullID, settings);
    }

    /// <summary>Gets an immutable, ordinally ordered snapshot after lazy decay.</summary>
    public AttentionSnapshot GetAttentionSnapshot()
    {
        AttentionSettings settings = CreateAttentionSettings();
        return _attention.GetSnapshot(settings);
    }

    /// <summary>Gets every currently retained identity meeting the separate context threshold.</summary>
    protected IReadOnlyList<string> GetContextEligibleAttentionIDs()
    {
        AttentionSettings settings = CreateAttentionSettings();
        return _attention.GetContextEligibleIDs(settings);
    }

    internal void SetAttentionClockForTesting(Func<double> clock) => _attention.SetClock(clock);

    private static ISceneContext LoadCurrentSceneContext()
        => Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();

    private static IGameClock LoadDefaultGameClock() => Game.Instance.GetRequiredService<IGameClock>();

    internal ISceneContext GetCurrentSceneContext() => _sceneContextLoader();

    internal void SetSceneContextLoaderForTesting(Func<ISceneContext> sceneContextLoader)
    {
        ArgumentNullException.ThrowIfNull(sceneContextLoader);
        _sceneContextLoader = sceneContextLoader;
    }

    internal IGameClock GameClock => _gameClockLoader();

    internal void SetGameClockLoaderForTesting(Func<IGameClock> gameClockLoader)
    {
        ArgumentNullException.ThrowIfNull(gameClockLoader);
        _gameClockLoader = gameClockLoader;
    }

    /// <summary>
    /// Re-aligns speaking-activity subscriptions with the current scene composition.
    /// </summary>
    /// <remarks>
    /// Voice activity resolves through current-scene characters' composed <see cref="IVoice"/> via
    /// <c>ICharacter.TryGetVoice</c>, mirroring the <c>SpeechPerception</c> attribution precedent (AI-006 TR-1).
    /// Each subscribed voice keeps its resolved owning character — or null when ambiguous — so the attended-speaker
    /// state never queries the Godot scene tree from continuations. Runs on the Godot thread.
    /// </remarks>
    private void RefreshSpeechVoiceSubscriptions()
    {
        ISceneContext scene = GetCurrentSceneContext();
        var currentOwners = new Dictionary<IVoice, ICharacter?>();
        foreach (ICharacter candidate in scene.Characters)
        {
            if (!candidate.TryGetVoice(out IVoice? voice) || voice is null)
            {
                continue;
            }

            if (currentOwners.TryGetValue(voice, out ICharacter? existingOwner)
                && !ReferenceEquals(existingOwner, candidate))
            {
                // Ambiguous composition never attributes: the voice can never cue or block (AI-001 TR-34).
                currentOwners[voice] = null;
                continue;
            }

            currentOwners[voice] = candidate;
        }

        lock (_speechVoiceSubscriptionLock)
        {
            foreach (IVoice voice in _subscribedSpeechVoices.Where(voice => !currentOwners.ContainsKey(voice)).ToArray())
            {
                voice.SpeechStarted -= OnVoiceSpeechStarted;
                voice.SpeechEnded -= OnVoiceSpeechEnded;
                voice.SpeechSegmentStarted -= OnVoiceSpeechSegmentStarted;
                voice.SpeechResumed -= OnVoiceSpeechResumed;
                voice.SpeechSegmentSettled -= OnVoiceSpeechSegmentSettled;
                _ = _subscribedSpeechVoices.Remove(voice);
                _ = _speechVoiceOwners.Remove(voice);
            }

            foreach (KeyValuePair<IVoice, ICharacter?> entry in currentOwners)
            {
                if (_subscribedSpeechVoices.Add(entry.Key))
                {
                    entry.Key.SpeechStarted += OnVoiceSpeechStarted;
                    entry.Key.SpeechEnded += OnVoiceSpeechEnded;
                    entry.Key.SpeechSegmentStarted += OnVoiceSpeechSegmentStarted;
                    entry.Key.SpeechResumed += OnVoiceSpeechResumed;
                    entry.Key.SpeechSegmentSettled += OnVoiceSpeechSegmentSettled;
                }

                _speechVoiceOwners[entry.Key] = entry.Value;
            }
        }
    }

    private void UnsubscribeFromSpeechVoices()
    {
        lock (_speechVoiceSubscriptionLock)
        {
            foreach (IVoice voice in _subscribedSpeechVoices)
            {
                voice.SpeechStarted -= OnVoiceSpeechStarted;
                voice.SpeechEnded -= OnVoiceSpeechEnded;
                voice.SpeechSegmentStarted -= OnVoiceSpeechSegmentStarted;
                voice.SpeechResumed -= OnVoiceSpeechResumed;
                voice.SpeechSegmentSettled -= OnVoiceSpeechSegmentSettled;
            }

            _subscribedSpeechVoices.Clear();
            _speechVoiceOwners.Clear();
        }

        while (_speechStartNotifications.TryDequeue(out _))
        {
        }

        lock (_deferredGodotActionsLock)
        {
            _speechSubscriptionEvaluationQueued = false;
        }
    }

    private void OnVoiceSpeechStarted(IVoice voice)
    {
        // A newly speaking voice may belong to a character that entered the scene after the last subscription
        // refresh; subscriptions re-align on the Godot thread without polling.
        _speechStartNotifications.Enqueue(voice);
        QueueSpeechSubscriptionEvaluation();
    }

    /// <summary>
    /// Forwards a textless speech-start lifecycle cue for an attended external speaker.
    /// </summary>
    /// <remarks>
    /// The cue is source-generic: the voice must resolve to a unique non-self current-scene character whose full ID
    /// is present in the attention snapshot sampled once at cue receipt. Attention sampled after the cue never
    /// creates a retroactive notification, and later attention changes never retract one already forwarded.
    /// </remarks>
    private void OnVoiceSpeechSegmentStarted(IVoice voice, SpeechSegmentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(metadata);
        if (IsNodeLifetimeEnded || !TryResolveAttendedSpeaker(voice))
        {
            return;
        }

        SpeechSegmentLifecycleNotified?.Invoke(new SpeechSegmentLifecycleNotification(
            voice.Id,
            metadata,
            SpeechSegmentLifecycleTransition.Started));
    }

    private void OnVoiceSpeechResumed(IVoice voice, SpeechSegmentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(metadata);
        if (IsNodeLifetimeEnded || IsSelfSpeechVoice(voice) || !TryResolveAttendedSpeaker(voice))
        {
            return;
        }

        SpeechSegmentLifecycleNotified?.Invoke(new SpeechSegmentLifecycleNotification(
            voice.Id,
            metadata,
            SpeechSegmentLifecycleTransition.Resumed));
    }

    private void OnVoiceSpeechSegmentSettled(IVoice voice, SpeechSegmentSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(settlement);
        if (IsNodeLifetimeEnded
            || IsSelfSpeechVoice(voice)
            || !TryMapTerminalSpeechSettlement(settlement.Kind, out SpeechSegmentLifecycleTransition transition))
        {
            return;
        }

        SpeechSegmentLifecycleNotified?.Invoke(new SpeechSegmentLifecycleNotification(voice.Id, settlement.Metadata, transition));
    }

    private bool IsSelfSpeechVoice(IVoice voice)
    {
        ICharacter character = ResolveOwningCharacter();
        return character.TryGetVoice(out IVoice? ownVoice)
            && ownVoice is not null
            && string.Equals(voice.Id, ownVoice.Id, StringComparison.Ordinal);
    }

    private static bool TryMapTerminalSpeechSettlement(
        SpeechSegmentSettlementKind kind,
        out SpeechSegmentLifecycleTransition transition)
    {
        switch (kind)
        {
            case SpeechSegmentSettlementKind.Blank:
                transition = SpeechSegmentLifecycleTransition.Blank;
                return true;
            case SpeechSegmentSettlementKind.Failed:
                transition = SpeechSegmentLifecycleTransition.Failed;
                return true;
            case SpeechSegmentSettlementKind.Abandoned:
                transition = SpeechSegmentLifecycleTransition.Abandoned;
                return true;
            case SpeechSegmentSettlementKind.Published:
                transition = default;
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported speech segment settlement kind.");
        }
    }

    private void QueueSpeechSubscriptionEvaluation()
    {
        if (IsNodeLifetimeEnded || !IsInsideTree())
        {
            return;
        }

        lock (_deferredGodotActionsLock)
        {
            if (IsNodeLifetimeEnded || _speechSubscriptionEvaluationQueued)
            {
                return;
            }

            _speechSubscriptionEvaluationQueued = true;
        }

        _ = CallDeferred(nameof(EvaluateSpeechSubscriptionsDeferred));
    }

    /// <summary>
    /// Drains speech-start notifications and refreshes speaking-activity subscriptions on the Godot thread.
    /// </summary>
    private void EvaluateSpeechSubscriptionsDeferred()
    {
        lock (_deferredGodotActionsLock)
        {
            _speechSubscriptionEvaluationQueued = false;
        }

        if (IsNodeLifetimeEnded)
        {
            return;
        }

        while (_speechStartNotifications.TryDequeue(out _))
        {
        }

        RefreshSpeechVoiceSubscriptions();
    }

    private void OnVoiceSpeechEnded(IVoice voice)
    {
        if (IsNodeLifetimeEnded || !Enabled || !TryResolveAttendedSpeaker(voice))
        {
            return;
        }

        // Attended-speaker-finished cue (AI-001 TR-34): wake an active wait and unblock a blocked speak. The wait
        // itself decides whether anything notable is returned; sub-threshold observations are never promoted.
        lock (_observationStateLock)
        {
            _ = _activeWait?.TryWake(ObservationWaitWake.AttendedSpeakerFinished);
        }

        PulseAttendedSpeakerFinished();
    }

    private void PulseAttendedSpeakerFinished()
    {
        TaskCompletionSource pulse = Interlocked.Exchange(
            ref _attendedSpeakerPulse,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _ = pulse.TrySetResult();
    }

    /// <summary>
    /// Determines whether a subscribed voice is an attended speaker's voice: composed on exactly one current-scene
    /// character other than the owning character whose canonical <c>ICharacter.FullId</c> is present in the
    /// current attention snapshot at or above the retention threshold.
    /// </summary>
    /// <remarks>
    /// Blank voice IDs never attend, mirroring the <c>SpeechPerception</c> attribution precedent; unattributable and
    /// ambiguous voices never cue or block (AI-001 TR-34, AI-002 TR-25).
    /// </remarks>
    private bool TryResolveAttendedSpeaker(IVoice voice)
    {
        if (string.IsNullOrWhiteSpace(voice.Id))
        {
            return false;
        }

        ICharacter ownCharacter = ResolveOwningCharacterForCues();
        ICharacter? owner;
        lock (_speechVoiceSubscriptionLock)
        {
            if (!_speechVoiceOwners.TryGetValue(voice, out owner))
            {
                return false;
            }
        }

        return owner is not null
            && !ReferenceEquals(owner, ownCharacter)
            && GetAttentionSnapshot().Values.ContainsKey(owner.FullId);
    }

    /// <summary>
    /// Reports whether a speaker this mind attends to is currently speaking.
    /// </summary>
    /// <remarks>
    /// A voice attends iff it is composed on exactly one current-scene character other than the owning character
    /// whose canonical <c>ICharacter.FullId</c> is present in the current attention snapshot at or above the
    /// retention threshold, regardless of weight or score. The owning character's own voice never blocks, and
    /// unattributable voices never block (AI-002 TR-25). Safe from continuations: it reads only subscription state
    /// refreshed on the Godot thread, the lock-guarded attention snapshot, and the volatile speaking flag.
    /// </remarks>
    internal bool IsAttendedSpeakerSpeaking()
    {
        ICharacter ownCharacter = ResolveOwningCharacterForCues();
        AttentionSnapshot attention = GetAttentionSnapshot();
        lock (_speechVoiceSubscriptionLock)
        {
            foreach (KeyValuePair<IVoice, ICharacter?> entry in _speechVoiceOwners)
            {
                if (entry.Value is not { } candidate
                    || ReferenceEquals(candidate, ownCharacter)
                    || string.IsNullOrWhiteSpace(entry.Key.Id)
                    || !entry.Key.IsSpeaking
                    || !attention.Values.ContainsKey(candidate.FullId))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the cached owning character without Godot scene-tree traversal so cue checks stay safe from
    /// continuations.
    /// </summary>
    private ICharacter ResolveOwningCharacterForCues()
        => Volatile.Read(ref _cachedOwningCharacter) ?? ResolveOwningCharacter();

    /// <summary>
    /// Waits until no attended speaker is speaking, unblocked by the attended-speaker-finished cue.
    /// </summary>
    /// <param name="cancellationToken">Cancellation that abandons the turn-taking guard.</param>
    internal async Task WaitUntilAttendedSpeakerIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsNodeLifetimeEnded)
            {
                throw new OperationCanceledException(NodeLifetimeCancellationToken);
            }

            TaskCompletionSource pulse = Volatile.Read(ref _attendedSpeakerPulse);
            if (!IsAttendedSpeakerSpeaking())
            {
                return;
            }

            await pulse.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Submits an observation for duplicate filtering and atomic ingestion.
    /// </summary>
    protected void Observe(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        ICharacter character = ResolveOwningCharacter();
        var context = new ObservationContext(character);
        IngestObservations([observation], context);
    }

    /// <summary>
    /// Generic exact-once commit-identity gate (AI-001 TR-49): an observation that supplies an immutable identity
    /// tuple through the optional contract is suppressed when any earlier accepted or retained observation already
    /// committed the same identity. Mind owns the atomic enforcement; perception never participates, and
    /// observations that supply no identity are unaffected.
    /// </summary>
    private static bool HasCommittedIdentity(
        ObservationCommitIdentity identity,
        IReadOnlyList<AgentObservation> retained,
        IReadOnlyList<AgentObservation> accepted)
    {
        foreach (AgentObservation candidate in accepted.Concat(retained))
        {
            if (candidate is IHasCommitIdentity { CommitIdentity: { } committed } && committed.Equals(identity))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSuppressDuplicate(
        AgentObservation observation,
        IReadOnlyList<AgentObservation> retained,
        IReadOnlyList<AgentObservation> accepted)
    {
        if (observation is IHasCommitIdentity { CommitIdentity: { } identity }
            && HasCommittedIdentity(identity, retained, accepted))
        {
            return true;
        }

        if (observation.DuplicatePolicy == ObservationDuplicatePolicy.Allow)
        {
            return false;
        }

        if (observation.DuplicatePolicy != ObservationDuplicatePolicy.IgnoreEquivalent)
        {
            throw new InvalidOperationException(
                $"Observation '{observation.GetType().FullName}' declares unsupported duplicate policy '{observation.DuplicatePolicy}'.");
        }

        string scope = observation.DuplicateScope
            ?? throw new InvalidOperationException(
                $"Observation '{observation.GetType().FullName}' must declare a duplicate scope when ignoring equivalents.");
        Type concreteType = observation.GetType();
        for (int index = accepted.Count - 1; index >= 0; index--)
        {
            AgentObservation candidate = accepted[index];
            if (candidate.GetType() == concreteType
                && string.Equals(candidate.DuplicateScope, scope, StringComparison.Ordinal))
            {
                return observation.IsSemanticallyEquivalentTo(candidate);
            }
        }

        for (int index = retained.Count - 1; index >= 0; index--)
        {
            AgentObservation candidate = retained[index];
            if (candidate.GetType() == concreteType
                && string.Equals(candidate.DuplicateScope, scope, StringComparison.Ordinal))
            {
                return observation.IsSemanticallyEquivalentTo(candidate);
            }
        }

        return false;
    }

    private AttentionSettings CreateAttentionSettings()
        => AttentionSettings.Create(
            AttentionMaximum,
            AttentionDecayPerSecond,
            AttentionRetentionThreshold,
            AttentionContextThreshold);

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
        var stampedObservations = new AgentObservation[observations.Count];
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
            stampedObservations[index] = stampedObservation;
        }

        NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
        IngestObservations(stampedObservations, context, throwWhenLifetimeEnded: true);
    }

    private void IngestObservations(
        IReadOnlyList<AgentObservation> observations,
        ObservationContext context,
        Action? beforeCommit = null,
        bool throwWhenLifetimeEnded = false)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(context);

        ObservationDeliveryUrgency? signalledUrgency;
        var stampedObservations = new List<AgentObservation>();

        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded)
            {
                if (throwWhenLifetimeEnded)
                {
                    throw new OperationCanceledException(NodeLifetimeCancellationToken);
                }

                return;
            }

            var acceptedObservations = new List<AgentObservation>(observations.Count);
            for (int index = 0; index < observations.Count; index++)
            {
                AgentObservation observation = observations[index]
                    ?? throw new ArgumentException($"Observation at index {index} cannot be null.", nameof(observations));
                if (!ShouldSuppressDuplicate(observation, _observationTimeline, acceptedObservations))
                {
                    acceptedObservations.Add(observation);
                }
            }

            // Staged evaluation (AI-001 TR-42): importance and freshness are each calculated exactly once for every
            // accepted observation, before timestamping, mutation, or commitment. Rejected duplicates contribute
            // neither importance nor freshness.
            var pending = new PendingObservation[acceptedObservations.Count];
            for (int index = 0; index < acceptedObservations.Count; index++)
            {
                AgentObservation observation = acceptedObservations[index];
                bool requiresFreshTurn = observation.RequiresFreshTurn(context);
                pending[index] = new PendingObservation(
                    observation,
                    CalculateAndValidateImportance(observation, context),
                    requiresFreshTurn);
            }

            beforeCommit?.Invoke();
            ObservationDeliveryUrgency? urgencyBefore = PendingDeliveryUrgencyLocked();
            double? stamp = pending.Length > 0 ? GameClock.NowSeconds : null;
            foreach (PendingObservation pendingObservation in pending)
            {
                AgentObservation stampedObservation = pendingObservation.Observation with
                {
                    ObservedAt = stamp
                };
                _observationTimeline.Add(stampedObservation);
                _notableAccumulation.Add(pendingObservation with
                {
                    Observation = stampedObservation
                });
                _cumulativeNotableImportance += pendingObservation.Importance;
                stampedObservations.Add(stampedObservation);

                // Freshness upgrades the complete current accumulation — including preceding sub-threshold
                // observations — to deliverable regardless of cumulative importance (AI-001 TR-43).
                _freshUrgencyPending |= pendingObservation.RequiresFreshTurn;
                if (!_notablePending && _cumulativeNotableImportance >= EffectiveObservationImportanceThreshold)
                {
                    _notablePending = true;
                }
            }

            ObservationDeliveryUrgency? urgencyAfter = PendingDeliveryUrgencyLocked();
            signalledUrgency = urgencyAfter is { } urgency && (urgencyBefore is not { } before || urgency > before)
                ? urgency
                : null;
        }

        foreach (AgentObservation observation in stampedObservations)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            OnObservationIngested(observation);
        }

        if (signalledUrgency is { } deliveryUrgency)
        {
            SignalObservationDelivery(deliveryUrgency);
        }
    }

    /// <summary>
    /// Delivers one urgency upgrade after its committing batch has settled (AI-001 TR-35/44): an active wait is
    /// woken through its normal completion mechanism — never by runner cancellation — with the wake reason matching
    /// the urgency, and listeners receive delivery urgency plus wait ownership. Disabled minds retain the window
    /// without waking or signalling (AI-001 TR-5).
    /// </summary>
    private void SignalObservationDelivery(ObservationDeliveryUrgency urgency)
    {
        bool waitOwned;
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded || !_enabled)
            {
                return;
            }

            waitOwned = _activeWait is not null;
            if (_activeWait is { } wait)
            {
                _ = wait.TryWake(
                    urgency == ObservationDeliveryUrgency.Fresh
                        ? ObservationWaitWake.FreshObservation
                        : ObservationWaitWake.ThresholdCrossed);
            }
        }

        ObservationDeliverySignalled?.Invoke(new ObservationDeliverySignal(urgency, waitOwned));
    }

    /// <summary>
    /// Gets whether an observation wait is currently active: deterministic synchronisation for fixtures that must
    /// observe strictly after a wait registered its delivery ownership (AI-002 TR-41).
    /// </summary>
    internal bool HasActiveObservationWait
    {
        get
        {
            lock (_observationStateLock)
            {
                return _activeWait is not null;
            }
        }
    }

    /// <summary>
    /// Claims the pending deliverable observation window when no wait is active and delivery is enabled,
    /// transferring delivery ownership atomically (AI-001 TR-44): exactly one consumer — a claim or a wait — owns
    /// each observation batch. The claim is completed once its observations are safely queued for rendering, or
    /// abandoned to restore the window.
    /// </summary>
    /// <returns>The claimed delivery window in FIFO ingestion order with its urgency, or null when nothing is
    /// deliverable, a wait owns delivery, delivery is paused while disabled (AI-001 TR-5), or the node lifetime has
    /// ended.</returns>
    internal ObservationDeliveryClaim? TryClaimPendingObservationDelivery()
    {
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded || !_enabled || _activeWait is not null || PendingDeliveryUrgencyLocked() is null)
            {
                return null;
            }

            ObservationDeliveryClaim claim = new(
                [.. _notableAccumulation],
                _freshUrgencyPending ? ObservationDeliveryUrgency.Fresh : ObservationDeliveryUrgency.Ordinary);
            ResetNotableAccumulationLocked();
            return claim;
        }
    }

    /// <summary>
    /// Finalises one claimed delivery after its observations are safely queued for rendering (AI-001 TR-44): the
    /// claim releases ownership and its observations are not deliverable again.
    /// </summary>
    internal void CompleteObservationDelivery(ObservationDeliveryClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_observationStateLock)
        {
            claim.MarkSettled();
        }
    }

    /// <summary>
    /// Restores an undelivered claim to the front of the pending accumulation in FIFO order (AI-001 TR-44): a
    /// failed rendering never silently loses its observations — they stay deliverable for the next wait or claim.
    /// Restoring never re-signals; the next urgency upgrade or wait call delivers the restored window.
    /// </summary>
    internal void AbandonObservationDelivery(ObservationDeliveryClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded || !claim.TryMarkAbandoned())
            {
                return;
            }

            _notableAccumulation.InsertRange(0, claim.Records);
            _cumulativeNotableImportance += claim.TotalImportance;
            if (_cumulativeNotableImportance >= EffectiveObservationImportanceThreshold)
            {
                _notablePending = true;
            }

            _freshUrgencyPending |= claim.HasFreshRecords;
        }
    }

    /// <summary>
    /// Notifies derived minds after a successfully committed observation.
    /// </summary>
    protected virtual void OnObservationIngested(AgentObservation observation)
    {
    }

    /// <summary>
    /// Gets an atomic, top-level read-only copy of the complete node-lifetime observation timeline membership and order.
    /// Observation records are passed directly under the producer immutability convention.
    /// </summary>
    internal IReadOnlyList<AgentObservation> GetObservationTimelineSnapshot()
    {
        lock (_observationStateLock)
        {
            return new ReadOnlyCollection<AgentObservation>([.. _observationTimeline]);
        }
    }

    /// <summary>
    /// Reports whether a committed timeline record still sits in the pending deliverable accumulation, so a
    /// derived mind can distinguish speech awaiting its ordinary delivery from speech a delivery channel already
    /// consumed (AI-001 TR-44; AI-002 TR-57 no-leaked-holds). Comparison is by record identity.
    /// </summary>
    internal bool ContainsPendingDeliveryObservation(AgentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_observationStateLock)
        {
            foreach (PendingObservation pending in _notableAccumulation)
            {
                if (ReferenceEquals(pending.Observation, observation))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Waits for the pending observation accumulation, completing early when accumulated importance reaches the
    /// configured threshold, when a fresh observation arrives regardless of importance, or when an attended speaker
    /// finishes speaking, and otherwise after <paramref name="maxWait"/> (AI-001 TR-6/7/43, AI-002 TR-31–33).
    /// </summary>
    /// <param name="maxWait">Maximum duration of one wait before quiet expiry.</param>
    /// <param name="cancellationToken">Cancellation that abandons the wait; node-lifetime cancellation is terminal
    /// and never surfaces a normal wait result (AI-001 TR-19). A wait already woken through its normal completion
    /// mechanism still delivers its window when this token is cancelled afterwards (AI-002 TR-41).</param>
    /// <returns>
    /// The observations its delivery window owns in FIFO ingestion order — the accumulation since the previous wait
    /// completion plus any sub-threshold predecessors a fresh observation upgraded — and the wake reason. Quiet
    /// expiry of an ordinary accumulation returns nothing: sub-threshold observations stay recorded in the timeline
    /// and reachable through the history tool.
    /// </returns>
    internal async Task<WaitOutcome> WaitForNotableObservationsAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        TimeSpan boundedWait = maxWait >= TimeSpan.Zero ? maxWait : MaxObservationWait;
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            NodeLifetimeCancellationToken);
        CancellationToken waitToken = waitCancellation.Token;

        ActiveWait wait;
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded)
            {
                throw new OperationCanceledException(NodeLifetimeCancellationToken);
            }

            if (_activeWait is not null)
            {
                throw new InvalidOperationException($"Mind '{GetPath()}' supports exactly one active observation wait.");
            }

            if (PendingDeliveryUrgencyLocked() is { } heldUrgency)
            {
                // An already-deliverable window — held ordinary threshold or retained fresh urgency — is delivered
                // immediately by this wait call (AI-001 TR-6, TR-43).
                List<AgentObservation> held = [.. _notableAccumulation.Select(static entry => entry.Observation)];
                ResetNotableAccumulationLocked();
                return new WaitOutcome(
                    held,
                    heldUrgency == ObservationDeliveryUrgency.Fresh
                        ? ObservationWaitWake.FreshObservation
                        : ObservationWaitWake.ThresholdCrossed);
            }

            wait = _activeWait = new ActiveWait();
        }

        ObservationWaitWake wake = ObservationWaitWake.QuietExpiry;
        List<AgentObservation> delivered = [];
        bool wokenByCompletion = false;
        try
        {
            Task wakeOrExpiry = await Task.WhenAny(
                wait.Completion.Task,
                Task.Delay(boundedWait, waitToken)).ConfigureAwait(false);
            if (ReferenceEquals(wakeOrExpiry, wait.Completion.Task))
            {
                wokenByCompletion = true;
                wake = await wait.Completion.Task.ConfigureAwait(false);
            }
            else
            {
                // Abandoned wait: cancellation — terminal node lifetime above all — must throw rather than surface a
                // normal wait result (AI-001 TR-19).
                waitToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            lock (_observationStateLock)
            {
                if (ReferenceEquals(_activeWait, wait))
                {
                    _activeWait = null;
                }

                if (wokenByCompletion || !waitToken.IsCancellationRequested)
                {
                    // A wait that was woken completed through its normal mechanism and owns its window (AI-002
                    // TR-41): it delivers the complete accumulation even when the external token was cancelled
                    // after the wake — a wait-owned fresh signal invalidates the surrounding batch without
                    // cancelling the wait — so only a wait that never woke treats cancellation as "never
                    // completed" and retains its accumulation. The window covers observations since the previous
                    // wait completion (AI-001 TR-6): any wait completion, early or quiet, starts a fresh
                    // accumulation window. Quiet expiry returns only what is already deliverable and never
                    // promotes sub-threshold observations — except that a pending fresh window, including one
                    // racing this completion, is delivered with fresh urgency rather than reset (TR-43).
                    if (_freshUrgencyPending || _notablePending)
                    {
                        if (_freshUrgencyPending)
                        {
                            wake = ObservationWaitWake.FreshObservation;
                        }

                        delivered = [.. _notableAccumulation.Select(static entry => entry.Observation)];
                    }

                    ResetNotableAccumulationLocked();
                }
            }
        }

        return new WaitOutcome(delivered, wake);
    }

    private void ResetNotableAccumulationLocked()
    {
        _notableAccumulation.Clear();
        _cumulativeNotableImportance = 0f;
        _notablePending = false;
        _freshUrgencyPending = false;
    }

    /// <summary>Gets the current pending delivery urgency, or null while the accumulation is not deliverable.</summary>
    private ObservationDeliveryUrgency? PendingDeliveryUrgencyLocked()
        => _freshUrgencyPending ? ObservationDeliveryUrgency.Fresh
            : _notablePending ? ObservationDeliveryUrgency.Ordinary
            : null;

    private TimeSpan MaxObservationWait
        => TimeSpan.FromSeconds(Math.Max(MaxObservationWaitSeconds, 0.05f));

    private float EffectiveObservationImportanceThreshold
        => Math.Max(ObservationImportanceThreshold, 0.01f);

    private void RejectEndedLifetimeReentry()
    {
        if (IsNodeLifetimeEnded && GetParent() is { } parent)
        {
            parent.RemoveChild(this);
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

    private static double GetStopwatchSeconds() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>One staged accumulation entry with its once-evaluated importance and freshness (AI-001 TR-42).</summary>
    internal readonly record struct PendingObservation(
        AgentObservation Observation,
        float Importance,
        bool RequiresFreshTurn);

    /// <summary>
    /// One serialisable unit of perception work: either a percept fan-out or an observation awaiting atomic commit,
    /// committed strictly in enqueue order (AI-001 TR-29, AI-006 TR-34).
    /// </summary>
    private abstract record PerceptionWork;

    private sealed record QueuedPercept(IPercept Percept, IPerception[] Faculties) : PerceptionWork;

    private sealed record QueuedObservation(AgentObservation Observation) : PerceptionWork;

    /// <summary>
    /// Ownership token for one claimed pending observation window (AI-001 TR-44): delivery ownership transfers only
    /// through Mind's claim API, and an abandoned — undelivered — claim restores its observations instead of losing
    /// them.
    /// </summary>
    internal sealed class ObservationDeliveryClaim
    {
        private int _settled;

        internal ObservationDeliveryClaim(IReadOnlyList<PendingObservation> records, ObservationDeliveryUrgency urgency)
        {
            Records = records;
            Urgency = urgency;
            Observations = [.. records.Select(static record => record.Observation)];
            TotalImportance = records.Sum(static record => record.Importance);
            HasFreshRecords = records.Any(static record => record.RequiresFreshTurn);
        }

        /// <summary>Claimed observations in FIFO ingestion order.</summary>
        public IReadOnlyList<AgentObservation> Observations
        {
            get;
        }

        /// <summary>Delivery urgency of the claimed window; fresh urgency dominates ordinary threshold delivery.</summary>
        public ObservationDeliveryUrgency Urgency
        {
            get;
        }

        internal IReadOnlyList<PendingObservation> Records
        {
            get;
        }

        internal float TotalImportance
        {
            get;
        }

        internal bool HasFreshRecords
        {
            get;
        }

        /// <summary>Marks the claim settled after its delivery completed; later settlement attempts are no-ops.</summary>
        internal void MarkSettled() => Interlocked.Exchange(ref _settled, 1);

        /// <summary>Attempts to settle the claim as abandoned; only the first settlement restores ownership.</summary>
        internal bool TryMarkAbandoned() => Interlocked.Exchange(ref _settled, 1) == 0;
    }

    /// <summary>
    /// Outcome of one observation wait: the observations its delivery window owns and the wake reason that
    /// completed it.
    /// </summary>
    /// <param name="Delivered">Delivered observations in FIFO ingestion order; empty on ordinary quiet expiry.</param>
    /// <param name="Wake">Reason the wait completed: quiet expiry, attended-speaker cue, threshold crossing, or a
    /// fresh observation that upgraded the complete accumulation.</param>
    internal readonly record struct WaitOutcome(IReadOnlyList<AgentObservation> Delivered, ObservationWaitWake Wake);
}
