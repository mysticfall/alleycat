using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Core.Time;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Decision;
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
/// as independent atomic units, own accepted observation-log retention and event history, and accumulate notable
/// scheduling metadata for delivery to the NPC's agent session.
/// </summary>
[GlobalClass]
public abstract partial class Mind : Node
{
    /// <summary>
    /// Reason one observation wait completed: the wait gate returned an immediate verdict, a deadline armed by a
    /// deferred verdict expired, or the agent's explicitly chosen quiet time elapsed first. Every wake is a chance
    /// for the agent to act, never a command.
    /// </summary>
    internal enum ObservationWaitWake
    {
        /// <summary>
        /// The gate's immediate verdict: a response to the character arrived, or the circumstances call for them to
        /// act.
        /// </summary>
        DecisionAct,

        /// <summary>A deadline armed by a deferred verdict expired: a pause gives the character an opening.</summary>
        PauseElapsed,

        /// <summary>
        /// The agent's explicitly chosen quiet time elapsed before any verdict ended or shortened it.
        /// </summary>
        TimerElapsed,
    }

    /// <summary>
    /// Delivery urgency of one pending fresh-turn window: a fresh observation invalidates stale reasoning at the
    /// session's next request boundary. The deleted importance-threshold pressure had an ordinary tier below
    /// this; fresh-turn urgency is the only remaining producer.
    /// </summary>
    internal enum ObservationDeliveryUrgency
    {
        /// <summary>Fresh-turn urgency that replaces stale reasoning.</summary>
        Fresh = 1,
    }

    /// <summary>
    /// Post-commit delivery signal: the pending window's delivery urgency and whether an active wait owns the
    /// delivery instead of the signalled runtime.
    /// </summary>
    /// <param name="Urgency">Delivery urgency of the newly deliverable window.</param>
    /// <param name="WaitOwned">Whether an active wait was parked when the window became deliverable.</param>
    internal readonly record struct ObservationDeliverySignal(ObservationDeliveryUrgency Urgency, bool WaitOwned);

    private readonly Lock _observationStateLock = new();
    private readonly Lock _perceptionQueueLock = new();
    private readonly Lock _deferredGodotActionsLock = new();
    private readonly Lock _speechVoiceSubscriptionLock = new();
    private readonly List<AcceptedObservationEntry> _acceptedObservationLog = [];
    private readonly List<AcceptedObservationEntry> _persistentEventTimeline = [];
    private readonly HashSet<ObservationCommitIdentity> _committedObservationIdentities = [];
    private readonly Dictionary<long, int> _acceptedObservationLogIndices = [];
    private readonly Dictionary<long, AcceptedObservationEntry> _activeRetainedEntries = [];
    private readonly Dictionary<long, AcceptedObservationEntry> _expiringRetainedEntries = [];
    private readonly List<long> _pendingFreshSequenceIDs = [];
    private readonly List<long> _pendingWakeSequenceIDs = [];
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
    private Func<IDecisionClient> _sceneDecisionClientLoader = static ()
        => Game.Instance.GetRequiredService<IDecisionClient>();
    private Func<DecisionWaitTimingSettings> _waitTimingLoader = DecisionWaitTimingSettings.LoadOrDefault;
    private Func<double> _waitJitterSampler = static () => Random.Shared.NextDouble();
    /// <summary>
    /// Identity marker of the single active observation wait: set while a wait runs, so delivery signalling knows
    /// a wait owns the window and a second concurrent wait is rejected.
    /// </summary>
    private ActiveObservationWait? _activeWait;
    private SubjectSceneDecisions? _sceneDecisions;
    internal SubjectSceneDecisions GetSceneDecisions() => _sceneDecisions ??= new(this, _sceneDecisionClientLoader(), _logger);
    internal MindSessionTranscriptRecorder? DecisionTranscript
    {
        get => !WaitDiagnosticsSettings.EnableSessionTranscriptLogging
        ? null : field ??= new(OwningCharacter.Id,
            static () => ProjectSettings.GlobalizePath(MindSessionTranscriptRecorder.UserRootPath),
            GameLoggerResolver.ResolveRequired<MindSessionTranscriptRecorder>());
    }
    internal void PumpSceneDecisions() => _sceneDecisions?.Pump(NodeLifetimeCancellationToken);
    private TaskCompletionSource _attendedSpeakerPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ICharacter? _cachedOwningCharacter;
    private bool _freshUrgencyPending;
    private bool _speechSubscriptionEvaluationQueued;
    private int _nodeLifetimeEnded;
    private bool _perceptionDrainRunning;
    private Task _perceptionDrainTask = Task.CompletedTask;
    private Action? _beforePerceptionEnqueueForTesting;
    private ILogger<Mind>? _logger;
    private ObservationLifetimePolicyRegistry? _lifetimePolicies;
    private long _nextObservationSequenceID = 1;
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Enabled setter controls delivery.")]
    private bool _enabled = true;

    /// <summary>
    /// Occurs after a committed batch raises the pending fresh window's delivery urgency: carries the urgency and
    /// whether an active wait was parked (AI-001 TR-7, AI-002 TR-7–9). The session runtime's fresh-turn
    /// invalidation bridge is the production consumer; the wait decision poll is nudged separately.
    /// </summary>
    internal event Action<ObservationDeliverySignal>? ObservationDeliverySignalled;

    /// <summary>
    /// Occurs for every observation committed into this Mind's timeline, after ingestion processing: the generic,
    /// modality-free committed-observation seam for same-assembly consumers — the wait decision's player-speech
    /// nudge above all. Payload-bearing observation interpretation stays with the consumer, never here.
    /// </summary>
    internal event Action<AgentObservation>? ObservationCommitted;

    /// <summary>
    /// Occurs after an atomic accepted batch changes active retained evidence. Removals are retention maintenance only:
    /// they never create a semantic observation, event timeline entry, scheduling pressure, or model message.
    /// </summary>
    internal event Action<RetainedObservationChange>? RetainedObservationChanged;

    /// <summary>
    /// Occurs for textless external automatic-segment lifecycle transitions. These notifications intentionally bypass
    /// perception, observation, attention, timeline, delivery, and wait processing.
    /// </summary>
    internal event Action<SpeechSegmentLifecycleNotification>? SpeechSegmentLifecycleNotified;

    /// <summary>
    /// Occurs when the owning character's speak tool resolves valid declared recipients, before admission: the
    /// pre-admission intention signal carrying the canonical recipient IDs in declaration order.
    /// </summary>
    internal event Action<IReadOnlyList<string>>? SpeakIntentionNotified;

    /// <summary>
    /// Occurs when the owning character's own speaking window closes, regardless of attention state.
    /// </summary>
    internal event Action? OwnSpeechEndedNotified;

    /// <summary>
    /// Occurs when an attended speaker's speaking window closes, carrying the speaker's canonical full ID.
    /// </summary>
    internal event Action<string>? AttendedSpeakerEndedNotified;

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
            }
        }
    }

    /// <summary>
    /// Maximum interval between wait-decision evaluations while a <c>wait</c> runs, including the failure backoff
    /// in effect. Event nudges may request an earlier evaluation.
    /// </summary>
    [ExportGroup("Runtime")]
    [Export(PropertyHint.Range, "0.05,120,0.05")]
    public float WaitEvaluationIntervalSeconds { get; set; } = 2f;

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
        _ = EnsureLifetimePolicies();
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
    public override void _Process(double delta)
    {
        _ = delta;
        if (!IsNodeLifetimeEnded)
        {
            ProcessRetainedObservationExpiry();
            PumpSceneDecisions();
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

        UnsubscribeFromComponentProjectionRefreshes();
        UnsubscribeFromSenses();
        UnsubscribeFromFaculties();
        UnsubscribeFromSpeechVoices();

        // One irreversible lifetime boundary: cancels active waits, session activity, and cue subscriptions so no
        // deferred callback accesses Mind services after exit (AI-001 TR-11).
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
    /// Trivial faculty observation intake (AI-006 TR-1): validates and enqueues one observation work item without
    /// interpreting, committing, or blocking the emitting faculty.
    /// </summary>
    private void OnFacultyObserved(AgentObservation observation) => EnqueueObservation(observation);

    /// <summary>Accepts a faculty emission while retaining its non-semantic transport at the Mind boundary.</summary>
    private void OnFacultyEmission(ObservationEmission emission)
        => EnqueueObservation(emission.Observation, isStillCurrent: null, emission.SpeechTransport);

    /// <summary>
    /// Source-neutral, non-blocking observation intake seam. Producers enqueue only; Mind serialises interpretation and
    /// acceptance on its existing work queue. This is intentionally internal until future source registries bind it.
    /// </summary>
    internal void EnqueueObservation(AgentObservation observation)
        => EnqueueObservation(observation, isStillCurrent: null, speechTransport: null);

    /// <summary>
    /// Source-neutral, non-blocking observation intake with an optional source-lifetime admission guard. The guard is
    /// evaluated only when serial processing reaches the item, so a source that has ended after enqueue cannot create
    /// a deferred semantic observation.
    /// </summary>
    internal void EnqueueObservation(AgentObservation observation, Func<bool>? isStillCurrent)
        => EnqueueObservation(observation, isStillCurrent, speechTransport: null);

    private void EnqueueObservation(
        AgentObservation observation,
        Func<bool>? isStillCurrent,
        SpeechObservationTransport? speechTransport)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (IsNodeLifetimeEnded || !Enabled)
        {
            return;
        }

        EnqueuePerceptionWork(new QueuedObservation(observation, isStillCurrent, speechTransport));
    }

    /// <summary>
    /// Enqueues one serial perception-work item and starts the drain worker when idle, preserving enqueue order across
    /// percepts and observations (AI-001 TR-8, AI-006 TR-2).
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
    /// Commits one faculty-emitted observation as one independent atomic unit (AI-001 TR-8/UR-3, AI-006 TR-8): its
    /// attention effects apply together with, for durable observations, ingestion effects, or not at all.
    /// </summary>
    private ValueTask ProcessObservationAsync(QueuedObservation queued)
    {
        CancellationToken cancellationToken = NodeLifetimeCancellationToken;
        ThrowIfPerceptionLifetimeEnded(cancellationToken);
        if (queued.IsStillCurrent is not null && !queued.IsStillCurrent())
        {
            return ValueTask.CompletedTask;
        }

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
        if (observation.IsAttentionOnly)
        {
            // Transient observations apply attention atomically and nothing else: no stamp, duplicate filtering,
            // timeline entry, notable accumulation, or notification (AI-001 UR-3, AI-006 TR-3).
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
        IngestObservations(
            [observation],
            context,
            beforeCommit: () => ApplyAttentionEffectsLocked(effects, attentionSettings),
            speechTransports: [queued.SpeechTransport]);
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
            if (faculty is PerceptionNode node)
            {
                node.ObservationEmitted += OnFacultyEmission;
            }
            else
            {
                faculty.Observed += OnFacultyObserved;
            }
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
    /// (AI-001 TR-8, AI-006 TR-2).
    /// </summary>
    private void UnsubscribeFromFaculties()
    {
        foreach (IPerception faculty in _observedFaculties)
        {
            if (faculty is PerceptionNode node)
            {
                node.ObservationEmitted -= OnFacultyEmission;
            }
            else
            {
                faculty.Observed -= OnFacultyObserved;
            }
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

    /// <summary>
    /// Gets every currently retained identity meeting the separate context threshold. Internal so same-assembly
    /// consumers that build the core render context beside the agent session — the gaze selector's one-shot
    /// scenario premise resolution above all — read exactly the session's context-eligible set.
    /// </summary>
    internal IReadOnlyList<string> GetContextEligibleAttentionIDs()
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
    /// <c>ICharacter.TryGetVoice</c>, mirroring the <c>SpeechPerception</c> attribution precedent (AI-006 TR-4).
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
                // Ambiguous composition never attributes: the voice can never cue or block (AI-002 TR-7).
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
        if (IsNodeLifetimeEnded || !Enabled)
        {
            return;
        }

        if (IsSelfSpeechVoice(voice))
        {
            // The owner's own window closing is its own cue: attention membership never applies to self speech.
            OwnSpeechEndedNotified?.Invoke();
            return;
        }

        if (!TryResolveAttendedSpeaker(voice, out ICharacter speaker))
        {
            return;
        }

        // Attended-speaker-finished cue (AI-002 TR-7): unblock a blocked speak and pulse the idle guard. The
        // wait decision poll is nudged through AttendedSpeakerEndedNotified — the reply-window trigger — and
        // decides itself whether reacting is warranted (wait experiment §4).
        PulseAttendedSpeakerFinished();
        AttendedSpeakerEndedNotified?.Invoke(speaker.FullId);
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
    /// ambiguous voices never cue or block (AI-002 TR-7).
    /// </remarks>
    private bool TryResolveAttendedSpeaker(IVoice voice) => TryResolveAttendedSpeaker(voice, out _);

    /// <inheritdoc cref="TryResolveAttendedSpeaker(IVoice)"/>
    private bool TryResolveAttendedSpeaker(IVoice voice, out ICharacter speaker)
    {
        speaker = null!;
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

        if (owner is null
            || ReferenceEquals(owner, ownCharacter)
            || !GetAttentionSnapshot().Values.ContainsKey(owner.FullId))
        {
            return false;
        }

        speaker = owner;
        return true;
    }

    /// <summary>
    /// Reports whether a speaker this mind attends to is currently speaking.
    /// </summary>
    /// <remarks>
    /// A voice attends iff it is composed on exactly one current-scene character other than the owning character
    /// whose canonical <c>ICharacter.FullId</c> is present in the current attention snapshot at or above the
    /// retention threshold, regardless of weight or score. The owning character's own voice never blocks, and
    /// unattributable voices never block (SPCH-005 TR-37). Safe from continuations: it reads only subscription state
    /// refreshed on the Godot thread, the lock-guarded attention snapshot, and the volatile speaking flag.
    /// </remarks>
    internal bool IsAttendedSpeakerSpeaking() => TryGetAttendedSpeaker(out _);

    /// <summary>
    /// Resolves one attended speaker who is currently speaking, if any.
    /// </summary>
    /// <remarks>
    /// Attendance follows <see cref="IsAttendedSpeakerSpeaking"/> exactly; when several attended speakers talk at
    /// once, the subscription-order-first match wins. Safe from continuations for the same reasons.
    /// </remarks>
    /// <param name="speakerFullId">The attended speaker's canonical full ID when one is found.</param>
    /// <returns>Whether an attended speaker is currently speaking.</returns>
    internal bool TryGetAttendedSpeaker([NotNullWhen(true)] out string? speakerFullId)
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

                speakerFullId = candidate.FullId;
                return true;
            }
        }

        speakerFullId = null;
        return false;
    }

    /// <summary>
    /// Raises <see cref="SpeakIntentionNotified"/> for the speak tool's resolved recipient declarations.
    /// </summary>
    /// <param name="recipients">Canonical distinct recipients in declaration order; never empty.</param>
    internal void NotifySpeakIntention(IReadOnlyList<string> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        if (IsNodeLifetimeEnded || recipients.Count == 0)
        {
            return;
        }

        SpeakIntentionNotified?.Invoke(recipients);
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
    /// Creates the configured concrete-type lifetime policies. Undeclared runtime types deliberately resolve to the
    /// registry's never-expiring, event-eligible fallback rather than requiring a Mind type switch.
    /// </summary>
    protected virtual IEnumerable<IObservationLifetimePolicy> CreateLifetimePolicies()
        => InitialObservationLifetimePolicies.Create();

    /// <summary>Validates configured policies once for the Mind node lifetime.</summary>
    private ObservationLifetimePolicyRegistry EnsureLifetimePolicies()
    {
        lock (_observationStateLock)
        {
            _lifetimePolicies ??= new ObservationLifetimePolicyRegistry(CreateLifetimePolicies());
            return _lifetimePolicies;
        }
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
        bool throwWhenLifetimeEnded = false,
        IReadOnlyList<SpeechObservationTransport?>? speechTransports = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(context);
        if (speechTransports is not null && speechTransports.Count != observations.Count)
        {
            throw new ArgumentException("Speech transport count must match observation count.", nameof(speechTransports));
        }

        List<AgentObservation> stampedObservations = [];
        List<RetainedObservationChange> retentionChanges = [];
        bool signalledFresh = false;
        ActiveObservationWait? wokenWait = null;

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

            ObservationLifetimePolicyRegistry policies = EnsureLifetimePolicies();
            // A replayed commit identity is rejected before a clock sample, policy evaluation, scheduling, or any
            // retained-state mutation. Apart from preserving exact-once acceptance, this keeps an all-duplicate
            // submission observationally inert to its caller.
            if (observations.Select((observation, index) => GetCommitIdentity(observation, speechTransports?[index])).All(identity => identity is not null
                && _committedObservationIdentities.Contains(identity)))
            {
                return;
            }

            double observedAt = GameClock.NowSeconds;
            List<AcceptedObservationEntry> activeEntries =
            [
                .. _activeRetainedEntries.Values.Where(entry =>
                    !policies.Resolve(entry.Payload.GetType()).IsExpired(entry, observedAt)),
            ];
            HashSet<ObservationCommitIdentity> stagedCommitIdentities = [.. _committedObservationIdentities];
            List<StagedAcceptance> accepted = [];
            long nextSequenceID = _nextObservationSequenceID;

            for (int index = 0; index < observations.Count; index++)
            {
                AgentObservation observation = observations[index]
                    ?? throw new ArgumentException($"Observation at index {index} cannot be null.", nameof(observations));
                SpeechObservationTransport? speechTransport = speechTransports?[index];
                ObservationCommitIdentity? commitIdentity = GetCommitIdentity(observation, speechTransport);
                if (commitIdentity is not null && stagedCommitIdentities.Contains(commitIdentity))
                {
                    continue;
                }

                IObservationLifetimePolicy policy = policies.Resolve(observation.GetType());
                ObservationLifetimePolicyDecision decision = policy.Evaluate(observation, activeEntries, observedAt);
                if (decision.Suppress)
                {
                    continue;
                }

                ValidateSupersessionDecision(decision, activeEntries, policy, observation);

                // Scheduling is evaluated exactly once only after policy acceptance and before any state mutation.
                bool requiresFreshTurn = observation.RequiresFreshTurn(context);
                bool wakesWaitingAgent = observation.WakesWaitingAgent(context);
                var scheduling = new ObservationSchedulingMetadata(
                    CalculateAndValidateImportance(observation, context),
                    requiresFreshTurn);
                AgentObservation stampedObservation = observation with
                {
                    ObservedAt = observedAt,
                };
                var entry = new AcceptedObservationEntry(
                    nextSequenceID++,
                    observedAt,
                    stampedObservation,
                    scheduling,
                    IsRetained: true)
                {
                    SpeechTransport = speechTransport,
                };
                accepted.Add(new StagedAcceptance(
                    entry,
                    policy.IsEventEligible(stampedObservation),
                    policy.HasFiniteLifetime,
                    wakesWaitingAgent));
                _ = activeEntries.RemoveAll(entry => decision.SupersededSequenceIDs.Contains(entry.SequenceID));
                activeEntries.Add(entry);
                if (commitIdentity is not null)
                {
                    _ = stagedCommitIdentities.Add(commitIdentity);
                }
            }

            var finalActiveIDs = activeEntries.Select(static entry => entry.SequenceID).ToHashSet();
            if (accepted.Count > 0)
            {
                beforeCommit?.Invoke();
            }

            List<AcceptedObservationEntry> removed = [];
            foreach (AcceptedObservationEntry current in _activeRetainedEntries.Values.ToArray())
            {
                if (!finalActiveIDs.Contains(current.SequenceID))
                {
                    AcceptedObservationEntry inactive = current with
                    {
                        IsRetained = false,
                    };
                    _acceptedObservationLog[_acceptedObservationLogIndices[current.SequenceID]] = inactive;
                    _ = _activeRetainedEntries.Remove(current.SequenceID);
                    _ = _expiringRetainedEntries.Remove(current.SequenceID);
                    removed.Add(inactive);
                }
            }

            List<AcceptedObservationEntry> committedEntries = [];
            foreach (StagedAcceptance staged in accepted)
            {
                AcceptedObservationEntry entry = staged.Entry with
                {
                    IsRetained = finalActiveIDs.Contains(staged.Entry.SequenceID),
                };
                _acceptedObservationLogIndices.Add(entry.SequenceID, _acceptedObservationLog.Count);
                _acceptedObservationLog.Add(entry);
                committedEntries.Add(entry);
                if (entry.IsRetained)
                {
                    _activeRetainedEntries.Add(entry.SequenceID, entry);
                    if (staged.HasFiniteLifetime)
                    {
                        _expiringRetainedEntries.Add(entry.SequenceID, entry);
                    }
                }

                if (staged.IsEventEligible)
                {
                    _persistentEventTimeline.Add(entry);
                }

                // A wake-worthy observation stays pending until a provider request presents it, so a wait that
                // begins before then also ends at once; a wait already parked ends after this batch settles.
                if (staged.WakesWaitingAgent)
                {
                    _pendingWakeSequenceIDs.Add(entry.SequenceID);
                    wokenWait = _activeWait;
                }
            }

            if (accepted.Count > 0)
            {
                _nextObservationSequenceID = nextSequenceID;
                _committedObservationIdentities.Clear();
                _committedObservationIdentities.UnionWith(stagedCommitIdentities);
            }

            bool freshBefore = _freshUrgencyPending;
            foreach (AcceptedObservationEntry entry in committedEntries)
            {
                stampedObservations.Add(entry.Payload);

                // Freshness upgrades the pending window to deliverable regardless of any other quality
                // (AI-002 UR-5): only the fresh-turn latch survives the wait experiment's pressure deletion,
                // because the session runtime's fresh invalidation bridge still consumes it.
                if (entry.Scheduling.RequiresFreshTurn)
                {
                    _pendingFreshSequenceIDs.Add(entry.SequenceID);
                    _freshUrgencyPending = true;
                }
            }

            signalledFresh = !freshBefore && _freshUrgencyPending;
            retentionChanges.AddRange(removed.Select(static entry => new RetainedObservationChange(
                RetainedObservationChangeKind.Removed,
                entry)));
            retentionChanges.AddRange(committedEntries
                .Where(static entry => entry.IsRetained)
                .Select(static entry => new RetainedObservationChange(RetainedObservationChangeKind.Added, entry)));
        }

        foreach (AgentObservation observation in stampedObservations)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            OnObservationIngested(observation);
            ObservationCommitted?.Invoke(observation);
        }

        NotifyRetainedObservationChanges(retentionChanges);

        if (signalledFresh)
        {
            SignalObservationDelivery();
        }

        wokenWait?.Wake();
    }

    private static ObservationCommitIdentity? GetCommitIdentity(
        AgentObservation observation,
        SpeechObservationTransport? speechTransport)
        => speechTransport?.CommitIdentity
            ?? (observation as IHasCommitIdentity)?.CommitIdentity;

    /// <summary>
    /// Delivers one fresh-urgency upgrade after its committing batch has settled (AI-001 TR-7, AI-002 TR-7): the
    /// session runtime's fresh-turn invalidation bridge receives the urgency and whether a wait is parked. The
    /// wait decision poll is never woken here — it judges from the timeline on its own triggers (wait
    /// experiment §4). Disabled minds retain the window without signalling.
    /// </summary>
    private void SignalObservationDelivery()
    {
        bool waitOwned;
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded || !_enabled)
            {
                return;
            }

            waitOwned = _activeWait is not null;
        }

        ObservationDeliverySignalled?.Invoke(new ObservationDeliverySignal(ObservationDeliveryUrgency.Fresh, waitOwned));
    }

    /// <summary>
    /// Consumes the pending fresh window through an accepted provider request's context watermark: fresh entries
    /// at or below the watermark were presented by that request, so only newer fresh observations keep the window
    /// deliverable.
    /// </summary>
    internal void ConfirmProviderRequestContext(long schedulingSequenceID)
    {
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded || schedulingSequenceID <= 0)
            {
                return;
            }

            // Presented wake-worthy observations no longer end a later wait.
            _ = _pendingWakeSequenceIDs.RemoveAll(sequenceID => sequenceID <= schedulingSequenceID);
            if (_pendingFreshSequenceIDs.Count == 0)
            {
                return;
            }

            _ = _pendingFreshSequenceIDs.RemoveAll(sequenceID => sequenceID <= schedulingSequenceID);
            _freshUrgencyPending = _pendingFreshSequenceIDs.Count > 0;
        }
    }

    /// <summary>
    /// Gets whether an observation wait is currently active: deterministic synchronisation for fixtures that must
    /// observe strictly after a wait registered its delivery ownership (AI-002 TR-9).
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
    /// Gets whether a committed wake-worthy observation (<see cref="AgentObservation.WakesWaitingAgent" />) has not yet
    /// been presented by an accepted provider request, so a wait beginning now would end at once: deterministic
    /// observation for fixtures (AI-002 TR-9).
    /// </summary>
    internal bool HasPendingWakeObservation
    {
        get
        {
            lock (_observationStateLock)
            {
                return _pendingWakeSequenceIDs.Count > 0;
            }
        }
    }

    /// <summary>
    /// Notifies derived minds after a successfully committed observation.
    /// </summary>
    protected virtual void OnObservationIngested(AgentObservation observation)
    {
    }

    /// <summary>
    /// Gets an atomic immutable snapshot of every accepted observation-log entry, including entries no longer retained.
    /// </summary>
    internal IReadOnlyList<AcceptedObservationEntry> GetAcceptedObservationLogSnapshot()
    {
        lock (_observationStateLock)
        {
            return new ReadOnlyCollection<AcceptedObservationEntry>([.. _acceptedObservationLog]);
        }
    }

    /// <summary>
    /// Gets an atomic immutable snapshot of active retained evidence only, in original acceptance order.
    /// </summary>
    internal IReadOnlyList<AcceptedObservationEntry> GetRetainedObservationSnapshot()
    {
        lock (_observationStateLock)
        {
            return new ReadOnlyCollection<AcceptedObservationEntry>(
                [.. _acceptedObservationLog.Where(static entry => entry.IsRetained)]);
        }
    }

    /// <summary>
    /// Gets an atomic immutable snapshot of persistent event-eligible entries. Unlike retained evidence this timeline
    /// never retracts entries after supersession or expiry.
    /// </summary>
    internal IReadOnlyList<AcceptedObservationEntry> GetPersistentEventTimelineSnapshot()
    {
        lock (_observationStateLock)
        {
            return new ReadOnlyCollection<AcceptedObservationEntry>([.. _persistentEventTimeline]);
        }
    }

    /// <summary>
    /// Compatibility snapshot for legacy timeline consumers. It deliberately maps only to the persistent event
    /// timeline; new consumers must use one of the explicitly named log, retained, or event APIs above.
    /// </summary>
    internal IReadOnlyList<AgentObservation> GetObservationTimelineSnapshot()
    {
        lock (_observationStateLock)
        {
            return new ReadOnlyCollection<AgentObservation>(
                [.. _persistentEventTimeline.Select(static entry => entry.Payload)]);
        }
    }

    /// <summary>
    /// Removes expired active retained evidence using a single game-time sample. This performs retention maintenance
    /// only: it never appends an observation or event, changes scheduling, or invokes observation-ingested hooks.
    /// </summary>
    internal void ProcessRetainedObservationExpiry()
    {
        List<RetainedObservationChange>? changes = null;
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            if (_expiringRetainedEntries.Count == 0)
            {
                return;
            }

            ObservationLifetimePolicyRegistry policies = EnsureLifetimePolicies();
            double now = GameClock.NowSeconds;
            List<long>? expiredSequenceIDs = null;
            foreach (KeyValuePair<long, AcceptedObservationEntry> retained in _expiringRetainedEntries)
            {
                AcceptedObservationEntry current = retained.Value;
                if (!policies.Resolve(current.Payload.GetType()).IsExpired(current, now))
                {
                    continue;
                }

                (expiredSequenceIDs ??= []).Add(retained.Key);
            }

            if (expiredSequenceIDs is not null)
            {
                changes = [];
                foreach (long sequenceID in expiredSequenceIDs)
                {
                    AcceptedObservationEntry current = _activeRetainedEntries[sequenceID];
                    AcceptedObservationEntry expired = current with
                    {
                        IsRetained = false,
                    };
                    _acceptedObservationLog[_acceptedObservationLogIndices[sequenceID]] = expired;
                    _ = _activeRetainedEntries.Remove(sequenceID);
                    _ = _expiringRetainedEntries.Remove(sequenceID);
                    changes.Add(new RetainedObservationChange(RetainedObservationChangeKind.Removed, expired));
                }
            }
        }

        if (changes is not null)
        {
            NotifyRetainedObservationChanges(changes);
        }
    }

    private static void ValidateSupersessionDecision(
        ObservationLifetimePolicyDecision decision,
        IReadOnlyList<AcceptedObservationEntry> activeEntries,
        IObservationLifetimePolicy policy,
        AgentObservation observation)
    {
        if (decision.Suppress && decision.SupersededSequenceIDs.Count != 0)
        {
            throw new InvalidOperationException(
                $"Observation lifetime policy '{policy.GetType().FullName}' suppressed '{observation.GetType().FullName}' "
                + "while also selecting retained entries for supersession.");
        }

        var activeIDs = activeEntries.Select(static entry => entry.SequenceID).ToHashSet();
        var selectedIDs = new HashSet<long>();
        foreach (long sequenceID in decision.SupersededSequenceIDs)
        {
            if (!activeIDs.Contains(sequenceID) || !selectedIDs.Add(sequenceID))
            {
                throw new InvalidOperationException(
                    $"Observation lifetime policy '{policy.GetType().FullName}' selected invalid retained sequence ID "
                    + $"'{sequenceID}' while accepting '{observation.GetType().FullName}'.");
            }
        }
    }

    private void NotifyRetainedObservationChanges(IReadOnlyList<RetainedObservationChange> changes)
    {
        foreach (RetainedObservationChange change in changes)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            RetainedObservationChanged?.Invoke(change);
        }
    }

    /// <summary>
    /// Waits until the two-question wait gate starts this character's next turn: a decision poll consults the
    /// model on a cadence and on speech/scene event nudges while the wait runs; an immediate verdict completes the
    /// wait at once, a deferred verdict arms its configured deadline under shorten-only rules, and an expired
    /// deadline completes the wait. The <c>wait</c> tool then returns its payload-free result and the runner
    /// proceeds exactly as after any tool. A wake-worthy observation (<see cref="AgentObservation.WakesWaitingAgent" />)
    /// committed while the wait is parked — or still unpresented when it begins — ends it at once with the same
    /// immediate wake. Waiting is never an event-delivery or cursor-advancement channel; scene events reach the model
    /// through the request-context timeline.
    /// </summary>
    /// <param name="cancellationToken">Cancellation that abandons the wait; node-lifetime cancellation is terminal
    /// and never surfaces a normal wait result (AI-001 TR-11).</param>
    /// <param name="durationSeconds">Optional explicitly chosen quiet time: it seeds the initial deadline, which
    /// immediate verdicts pre-empt and sooner verdict deadlines replace, and which nothing extends. Null starts
    /// with no deadline until a verdict arms one.</param>
    /// <param name="onGateEntered">Invoked once when the wait actually enters the decision-wait gate — after every
    /// validation and registration, immediately before the gate runs — and never when the wait is rejected first.</param>
    /// <returns>The wake reason. Event text remains exclusively in the request-context timeline.</returns>
    internal async Task<WaitOutcome> WaitForNextTurnAsync(
        CancellationToken cancellationToken,
        float? durationSeconds = null,
        Action? onGateEntered = null)
    {
        ValidateWaitCadenceAuthoring();
        ValidateWaitDuration(durationSeconds);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            NodeLifetimeCancellationToken);
        CancellationToken waitToken = waitCancellation.Token;

        ActiveObservationWait wait;
        bool wakePending;
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

            wait = _activeWait = new ActiveObservationWait();
            wakePending = _pendingWakeSequenceIDs.Count > 0;
        }

        try
        {
            if (wakePending)
            {
                // A wake-worthy observation no request has presented yet: this wait is the session's pause, and it
                // ends at once so the next turn delivers the observation.
                onGateEntered?.Invoke();
                _logger?.LogDebug("An undelivered wake-worthy observation ends the wait immediately.");
                return new WaitOutcome(ObservationWaitWake.DecisionAct);
            }

            DecisionWaitTimingSettings timing = _waitTimingLoader();
            var controller = new DecisionWaitController(
                this,
                WaitEvaluationIntervalSeconds,
                timing,
                _waitJitterSampler,
                GetStopwatchSeconds,
                _logger);
            using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(waitToken);
            Task<ObservationWaitWake> gate = controller.RunAsync(durationSeconds, gateCancellation.Token, onGateEntered);
            Task first = await Task.WhenAny(gate, wait.Woken).ConfigureAwait(false);
            if (ReferenceEquals(first, gate))
            {
                return new WaitOutcome(await gate.ConfigureAwait(false));
            }

            // A wake-worthy observation ended the wait: the gate stops, releasing its subscriptions, and the wait
            // completes with the same immediate wake an act-now verdict gives (AI-015 TR-14).
            gateCancellation.Cancel();
            try
            {
                _ = await gate.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!waitToken.IsCancellationRequested)
            {
                // The gate's cancellation is the expected end of a woken wait.
            }

            waitToken.ThrowIfCancellationRequested();
            _logger?.LogDebug("A wake-worthy observation ended the wait.");
            return new WaitOutcome(ObservationWaitWake.DecisionAct);
        }
        finally
        {
            lock (_observationStateLock)
            {
                if (ReferenceEquals(_activeWait, wait))
                {
                    _activeWait = null;
                }
            }
        }
    }

    /// <summary>Validates an explicit wait duration: null parks on the decision gate alone; otherwise positive and
    /// finite.</summary>
    private static void ValidateWaitDuration(float? durationSeconds)
    {
        if (durationSeconds is not { } seconds || (float.IsFinite(seconds) && seconds > 0f))
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(durationSeconds),
            durationSeconds,
            "A deliberate wait duration must be finite and positive.");
    }

    internal void SetSceneDecisionClientLoaderForTesting(Func<IDecisionClient> loader) => _sceneDecisionClientLoader = loader;

    /// <summary>Replaces the wait decision client before a wait for focused runtime coverage.</summary>
    internal void SetSceneDecisionClientForTesting(IDecisionClient decisionClient)
    {
        ArgumentNullException.ThrowIfNull(decisionClient);
        _sceneDecisionClientLoader = () => decisionClient;
    }

    /// <summary>
    /// Replaces the wait gate's timing and, optionally, its jitter sampler (unit samples in [0, 1)) before a wait
    /// for deterministic runtime coverage.
    /// </summary>
    internal void SetWaitTimingForTesting(DecisionWaitTimingSettings timing, Func<double>? jitterSampler = null)
    {
        ArgumentNullException.ThrowIfNull(timing);
        _waitTimingLoader = () => timing;
        if (jitterSampler is not null)
        {
            _waitJitterSampler = jitterSampler;
        }
    }

    /// <summary>
    /// The diagnostics settings the wait gate's decision-payload dump consults at wait start. The agentic session
    /// overrides this with its own diagnostics-settings loader — the same seam its transcript gate uses — so one
    /// injection point covers the session's diagnostics; the base default resolves production configuration.
    /// </summary>
    internal virtual AIDiagnosticsSettings WaitDiagnosticsSettings => AIDiagnosticsSettings.LoadOrDefault();

    private void ValidateWaitCadenceAuthoring()
    {
        if (!float.IsFinite(WaitEvaluationIntervalSeconds) || WaitEvaluationIntervalSeconds <= 0f)
        {
            throw new InvalidOperationException(
                $"Mind '{GetPath()}' requires {nameof(WaitEvaluationIntervalSeconds)} to be finite and positive, but "
                + $"found '{WaitEvaluationIntervalSeconds}'.");
        }
    }

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

    /// <summary>One policy-accepted entry staged until the complete batch can commit atomically.</summary>
    private readonly record struct StagedAcceptance(
        AcceptedObservationEntry Entry,
        bool IsEventEligible,
        bool HasFiniteLifetime,
        bool WakesWaitingAgent);

    /// <summary>
    /// One serialisable unit of perception work: either a percept fan-out or an observation awaiting atomic commit,
    /// committed strictly in enqueue order (AI-001 TR-8, AI-006 TR-2).
    /// </summary>
    private abstract record PerceptionWork;

    private sealed record QueuedPercept(IPercept Percept, IPerception[] Faculties) : PerceptionWork;

    private sealed record QueuedObservation(
        AgentObservation Observation,
        Func<bool>? IsStillCurrent,
        SpeechObservationTransport? SpeechTransport) : PerceptionWork;

    /// <summary>
    /// One active observation wait: its wake signal completes when a wake-worthy observation is committed while the
    /// wait is parked (<see cref="AgentObservation.WakesWaitingAgent" />).
    /// </summary>
    private sealed class ActiveObservationWait
    {
        private readonly TaskCompletionSource _woken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Woken => _woken.Task;

        public void Wake() => _woken.TrySetResult();
    }

    /// <summary>Outcome of one observation wait, carrying its wake reason only.</summary>
    /// <param name="Wake">Reason the wait completed.</param>
    internal readonly record struct WaitOutcome(ObservationWaitWake Wake);
}
