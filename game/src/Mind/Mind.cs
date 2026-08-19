using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Time;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech.Voice;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind;

/// <summary>
/// Abstract base for NPC mind-like components that synchronously interpret stimuli into an ordered observation
/// timeline and accumulate notable observations for delivery to the NPC's agent session.
/// </summary>
[GlobalClass]
public abstract partial class Mind : Node
{
    /// <summary>
    /// Registered active wait woken by threshold crossings or attended-speaker-finished cues.
    /// </summary>
    private sealed class ActiveWait
    {
        public ActiveWait()
        {
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<bool> Completion
        {
            get;
        }

        public bool Settled
        {
            get; private set;
        }

        public bool TryWake(bool attendedSpeakerFinished)
        {
            lock (this)
            {
                if (Settled)
                {
                    return false;
                }

                Settled = true;
            }

            return Completion.TrySetResult(attendedSpeakerFinished);
        }
    }

    private static readonly TimeSpan _defaultMaxObservationWait = TimeSpan.FromSeconds(10);

    private readonly Lock _observationStateLock = new();
    private readonly Lock _deferredGodotActionsLock = new();
    private readonly Lock _speechVoiceSubscriptionLock = new();
    private readonly List<AgentObservation> _observationTimeline = [];
    private readonly List<PendingObservation> _notableAccumulation = [];
    private readonly CancellationTokenSource _nodeLifetimeCancellation = new();
    private readonly AttentionPolicy _attention = new(GetStopwatchSeconds);
    private readonly Dictionary<Type, IPerception> _perceptions = [];
    private readonly Dictionary<ISense, Action<IPercept>> _senseHandlers = [];
    private readonly HashSet<IVoice> _subscribedSpeechVoices = [];
    private readonly Dictionary<IVoice, ICharacter?> _speechVoiceOwners = [];
    private readonly ConcurrentQueue<IVoice> _speechStartNotifications = new();
    private ISense[] _senses = [];
    private IComponentProjectionNotifier? _componentProjectionNotifier;
    private Func<ISceneContext> _sceneContextLoader = LoadCurrentSceneContext;
    private Func<IGameClock> _gameClockLoader = LoadDefaultGameClock;
    private ActiveWait? _activeWait;
    private TaskCompletionSource _attendedSpeakerPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ICharacter? _cachedOwningCharacter;
    private float _cumulativeNotableImportance;
    private bool _notablePending;
    private bool _speechSubscriptionEvaluationQueued;
    private int _nodeLifetimeEnded;
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Enabled setter controls delivery.")]
    private bool _enabled = true;

    /// <summary>
    /// Occurs when accumulated observations become notable while no wait is active, signalling the agent session
    /// runtime to interrupt as defined by AI-002 (AI-001 TR-6, TR-35).
    /// </summary>
    internal event Action? NotableObservationsSignalled;

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
                if (value && _notablePending)
                {
                    // Delivery resumes for the preserved accumulation: a held notable window wakes an active wait
                    // (AI-001 TR-5). When no wait is active the window stays held for the next wait call.
                    _ = _activeWait?.TryWake(attendedSpeakerFinished: false);
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

    /// <summary>Authorable exact-type perception faculties used for composed senses.</summary>
    [Export]
    public PerceptionResource[] Perceptions { get; set; } = [];

    /// <inheritdoc />
    public override void _EnterTree()
    {
        if (IsNodeLifetimeEnded)
        {
            _ = CallDeferred(nameof(RejectEndedLifetimeReentry));
            return;
        }

        Volatile.Write(ref _cachedOwningCharacter, ResolveOwningCharacter());
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
        UnsubscribeFromSpeechVoices();
        _perceptions.Clear();

        // One irreversible lifetime boundary: cancels active waits, session activity, and cue subscriptions so no
        // deferred callback accesses Mind services after exit (AI-001 TR-18).
        _nodeLifetimeCancellation.Cancel();
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

    private void OnPerceived(IPercept percept)
    {
        ArgumentNullException.ThrowIfNull(percept);
        if (IsNodeLifetimeEnded || !Enabled)
        {
            return;
        }

        AttentionSettings attentionSettings = CreateAttentionSettings();
        IPerception perception = _perceptions.GetValueOrDefault(percept.GetType())
            ?? throw new InvalidOperationException($"Mind '{GetPath()}' received undeclared percept type '{percept.GetType().FullName}'.");
        ISceneContext scene = _sceneContextLoader();
        PerceptionResult result = perception.Perceive(percept, new PerceptionContext(ResolveOwningCharacter(), scene, attentionSettings));
        ApplyPerceptionResult(result, attentionSettings);
    }

    private void ActivatePerceptions()
    {
        AttentionSettings _ = CreateAttentionSettings();
        ICharacter character = ResolveOwningCharacter();
        ISense[] senses = [.. character.Components.OfType<ISense>()];
        var perceptions = new Dictionary<Type, IPerception>();
        var declaredTypes = new HashSet<Type>();
        foreach (ISense sense in senses)
        {
            foreach (Type perceptType in sense.PerceptTypes)
            {
                if (perceptType is null || !typeof(IPercept).IsAssignableFrom(perceptType) || !declaredTypes.Add(perceptType))
                {
                    throw new InvalidOperationException($"Mind '{GetPath()}' requires each configured sense to declare unique exact IPercept runtime types.");
                }
            }
        }

        foreach (PerceptionResource faculty in Perceptions)
        {
            if (faculty is null)
            {
                throw new InvalidOperationException($"Mind '{GetPath()}' has a null perception faculty.");
            }

            Type perceptType = faculty.PerceptType;
            if (!declaredTypes.Contains(perceptType)
                || !faculty.GetType().GetInterfaces().Any(type => type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(IPerception<>)
                    && type.GenericTypeArguments[0] == perceptType)
                || !perceptions.TryAdd(perceptType, faculty))
            {
                throw new InvalidOperationException($"Mind '{GetPath()}' has an invalid, duplicate, or undeclared perception faculty mapping for '{perceptType.FullName}'.");
            }
        }

        if (perceptions.Count != declaredTypes.Count)
        {
            throw new InvalidOperationException($"Mind '{GetPath()}' requires exactly one perception faculty for every configured sense percept type.");
        }

        UnsubscribeFromSenses();
        _perceptions.Clear();
        foreach (KeyValuePair<Type, IPerception> perception in perceptions)
        {
            _perceptions.Add(perception.Key, perception.Value);
        }

        _senses = senses;
        foreach (ISense sense in _senses)
        {
            void handler(IPercept percept)
            {
                if (!sense.PerceptTypes.Contains(percept.GetType()))
                {
                    throw new InvalidOperationException($"Sense '{sense.GetType().FullName}' published undeclared percept type '{percept.GetType().FullName}'.");
                }

                OnPerceived(percept);
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
                _ = _subscribedSpeechVoices.Remove(voice);
                _ = _speechVoiceOwners.Remove(voice);
            }

            foreach (KeyValuePair<IVoice, ICharacter?> entry in currentOwners)
            {
                if (_subscribedSpeechVoices.Add(entry.Key))
                {
                    entry.Key.SpeechStarted += OnVoiceSpeechStarted;
                    entry.Key.SpeechEnded += OnVoiceSpeechEnded;
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
            _ = _activeWait?.TryWake(attendedSpeakerFinished: true);
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
    /// Appends an observation to the timeline and the notable-observation accumulation.
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
        float importance = CalculateAndValidateImportance(observation, context);

        CommitObservations([new PendingObservation(observation, importance)]);
    }

    private void ApplyPerceptionResult(PerceptionResult result, AttentionSettings attentionSettings)
    {
        ArgumentNullException.ThrowIfNull(result);

        ICharacter character = ResolveOwningCharacter();
        var context = new ObservationContext(character);
        var pending = new PendingObservation[result.Observations.Count];
        for (int index = 0; index < result.AttentionEffects.Count; index++)
        {
            AttentionEffect effect = result.AttentionEffects[index]
                ?? throw new ArgumentException($"Perception attention effect at index {index} cannot be null.", nameof(result));
            IdentityValidator.ValidateFullId(effect.SubjectFullId, nameof(result));
            AttentionSettings.ValidateContribution(effect.Contribution, nameof(result));
        }

        for (int index = 0; index < result.Observations.Count; index++)
        {
            AgentObservation observation = result.Observations[index]
                ?? throw new ArgumentException($"Perception observation at index {index} cannot be null.", nameof(result));
            pending[index] = new PendingObservation(observation, CalculateAndValidateImportance(observation, context));
        }

        _attention.ApplyElapsedDecay(attentionSettings);
        foreach (AttentionEffect effect in result.AttentionEffects)
        {
            ReinforceAttention(effect.SubjectFullId, effect.Contribution, attentionSettings);
        }

        if (pending.Length > 0)
        {
            CommitObservations(pending);
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
        CommitObservations(pending, throwWhenLifetimeEnded: true);
    }

    private void CommitObservations(
        IReadOnlyList<PendingObservation> observations,
        bool throwWhenLifetimeEnded = false)
    {
        bool becameNotable;
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

            becameNotable = false;
            double stamp = GameClock.NowSeconds;
            foreach (PendingObservation pendingObservation in observations)
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

                if (!_notablePending && _cumulativeNotableImportance >= EffectiveObservationImportanceThreshold)
                {
                    _notablePending = true;
                    becameNotable = true;
                }
            }
        }

        foreach (AgentObservation observation in stampedObservations)
        {
            OnObservationIngested(observation);
        }

        if (becameNotable)
        {
            SignalNotableAccumulation();
        }
    }

    /// <summary>
    /// Delivers one notable accumulation after its committing batch has settled (AI-001 TR-35): an active wait
    /// completes early with it; otherwise, while delivery is enabled, the agent session runtime is signalled to
    /// interrupt (AI-001 TR-6, AI-002 TR-41).
    /// </summary>
    private void SignalNotableAccumulation()
    {
        bool wakeWait;
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded || !_enabled)
            {
                return;
            }

            wakeWait = _activeWait is not null;
            if (wakeWait)
            {
                _ = _activeWait!.TryWake(attendedSpeakerFinished: false);
            }
        }

        if (!wakeWait)
        {
            NotableObservationsSignalled?.Invoke();
        }
    }

    /// <summary>
    /// Takes the pending notable accumulation for injected delivery when no wait is active, resetting the window.
    /// </summary>
    /// <returns>The notable observations in FIFO ingestion order, or null when nothing is currently notable.</returns>
    internal IReadOnlyList<AgentObservation>? TryTakePendingNotableWindow()
    {
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded || !_notablePending || _activeWait is not null)
            {
                return null;
            }

            List<AgentObservation> window = [.. _notableAccumulation.Select(static entry => entry.Observation)];
            ResetNotableAccumulationLocked();
            return window;
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
    /// Waits for the notable-observation accumulation, completing early when accumulated importance reaches the
    /// configured threshold or when an attended speaker finishes speaking, and otherwise after
    /// <paramref name="maxWait"/> (AI-001 TR-6/7, AI-002 TR-31–33).
    /// </summary>
    /// <param name="maxWait">Maximum duration of one wait before quiet expiry.</param>
    /// <param name="cancellationToken">Cancellation that abandons the wait.</param>
    /// <returns>
    /// The notable observations accumulated since the previous wait completion — normally nothing on quiet expiry —
    /// and whether an attended speaker's finished cue woke the wait. Sub-threshold observations are never promoted;
    /// they remain recorded in the timeline and reachable through the history tool.
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

            if (_notablePending)
            {
                List<AgentObservation> window = [.. _notableAccumulation.Select(static entry => entry.Observation)];
                ResetNotableAccumulationLocked();
                return new WaitOutcome(window, AttendedSpeakerFinished: false);
            }

            wait = _activeWait = new ActiveWait();
        }

        bool speakerFinished = false;
        List<AgentObservation> notable = [];
        try
        {
            Task wakeOrExpiry = await Task.WhenAny(
                wait.Completion.Task,
                Task.Delay(boundedWait, waitToken)).ConfigureAwait(false);
            if (ReferenceEquals(wakeOrExpiry, wait.Completion.Task))
            {
                speakerFinished = await wait.Completion.Task.ConfigureAwait(false);
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

                // The accumulation covers observations since the previous wait completion (AI-001 TR-6): any wait
                // completion, early or quiet, starts a fresh accumulation window. Quiet expiry returns whatever is
                // already notable — normally nothing — and never promotes sub-threshold observations.
                notable = _notablePending
                    ? [.. _notableAccumulation.Select(static entry => entry.Observation)]
                    : [];
                ResetNotableAccumulationLocked();
            }
        }

        return new WaitOutcome(notable, speakerFinished);
    }

    private void ResetNotableAccumulationLocked()
    {
        _notableAccumulation.Clear();
        _cumulativeNotableImportance = 0f;
        _notablePending = false;
    }

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

    private readonly record struct PendingObservation(AgentObservation Observation, float Importance);

    /// <summary>
    /// Outcome of one observation wait: the notable observations delivered and whether the attended-speaker-finished
    /// cue completed the wait early.
    /// </summary>
    /// <param name="Notable">Notable observations in FIFO ingestion order; empty on quiet expiry.</param>
    /// <param name="AttendedSpeakerFinished">Whether an attended speaker finishing speech woke the wait early.</param>
    internal readonly record struct WaitOutcome(IReadOnlyList<AgentObservation> Notable, bool AttendedSpeakerFinished);
}
