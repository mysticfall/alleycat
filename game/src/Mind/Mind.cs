using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech.Voice;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind;

/// <summary>
/// Abstract base for NPC mind-like components that synchronously interpret stimuli and schedule durable observations.
/// </summary>
[GlobalClass]
public abstract partial class Mind : Node
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
    private readonly AttentionPolicy _attention = new(GetTimestamp);
    private readonly Dictionary<Type, IPerception> _perceptions = [];
    private readonly Dictionary<ISense, Action<IPercept>> _senseHandlers = [];
    private readonly Lock _speechVoiceSubscriptionLock = new();
    private readonly HashSet<IVoice> _subscribedSpeechVoices = [];
    private readonly ConcurrentQueue<IVoice> _speechStartNotifications = new();
    private ISense[] _senses = [];
    private IComponentProjectionNotifier? _componentProjectionNotifier;
    private Func<ISceneContext> _sceneContextLoader = LoadCurrentSceneContext;
    private bool _speechActivityEvaluationQueued;
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Enabled setter controls scheduling.")]
    private bool _enabled = true;

    internal Func<CancellationToken, Task>? ObservationBatchClaimedHookForTesting
    {
        get;
        set;
    }

    /// <summary>
    /// Enables stimulus intake and observation processing.
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
    /// Enables an attended speaker starting speech to pre-empt an active turn and cut its audible speech.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="HighImportanceInterruptionEnabled"/>, this voice-activity trigger is enabled by default.
    /// </remarks>
    [Export]
    public bool SpeechInterruptionEnabled { get; set; } = true;

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

        SubscribeToComponentProjectionRefreshes();
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        SubscribeToComponentProjectionRefreshes();
        if (_componentProjectionNotifier is null || _componentProjectionNotifier.HasComponentProjection)
        {
            ActivatePerceptions();
        }
        RefreshSpeechVoiceSubscriptions();
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
        UnsubscribeFromComponentProjectionRefreshes();
        UnsubscribeFromSenses();
        UnsubscribeFromSpeechVoices();
        _perceptions.Clear();
        if (_schedulingTimer is { } schedulingTimer)
        {
            schedulingTimer.Timeout -= OnSchedulingTimerTimeout;
        }

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
        ISceneContext scene = Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
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

    internal ISceneContext GetCurrentSceneContext() => _sceneContextLoader();

    internal void SetSceneContextLoaderForTesting(Func<ISceneContext> sceneContextLoader)
    {
        ArgumentNullException.ThrowIfNull(sceneContextLoader);
        _sceneContextLoader = sceneContextLoader;
    }

    /// <summary>
    /// Re-aligns speaking-activity subscriptions with the current scene composition.
    /// </summary>
    /// <remarks>
    /// Voice activity resolves through current-scene characters' composed <see cref="IVoice"/> via
    /// <c>ICharacter.TryGetVoice</c>, mirroring the <c>SpeechPerception</c> attribution precedent (AI-006 TR-1).
    /// Runs on the Godot thread.
    /// </remarks>
    private void RefreshSpeechVoiceSubscriptions()
    {
        ISceneContext scene = GetCurrentSceneContext();
        List<IVoice> currentVoices = [];
        foreach (ICharacter candidate in scene.Characters)
        {
            if (candidate.TryGetVoice(out IVoice? voice) && voice is not null && !currentVoices.Contains(voice))
            {
                currentVoices.Add(voice);
            }
        }

        lock (_speechVoiceSubscriptionLock)
        {
            foreach (IVoice voice in _subscribedSpeechVoices.Where(voice => !currentVoices.Contains(voice)).ToArray())
            {
                voice.SpeechStarted -= OnVoiceSpeechStarted;
                voice.SpeechEnded -= OnVoiceSpeechEnded;
                _ = _subscribedSpeechVoices.Remove(voice);
            }

            foreach (IVoice voice in currentVoices.Where(voice => !_subscribedSpeechVoices.Contains(voice)))
            {
                voice.SpeechStarted += OnVoiceSpeechStarted;
                voice.SpeechEnded += OnVoiceSpeechEnded;
                _ = _subscribedSpeechVoices.Add(voice);
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
        }

        while (_speechStartNotifications.TryDequeue(out _))
        {
        }

        lock (_deferredGodotActionsLock)
        {
            _speechActivityEvaluationQueued = false;
        }
    }

    private void OnVoiceSpeechStarted(IVoice voice)
    {
        _speechStartNotifications.Enqueue(voice);
        QueueSpeechActivityEvaluation();
    }

    private void OnVoiceSpeechEnded(IVoice voice)
    {
        _ = voice;

        // A blocking speaking window just closed: immediately re-run scheduling evaluation without polling
        // (AI-001 TR-42).
        QueueSchedulingEvaluation();
    }

    private void QueueSpeechActivityEvaluation()
    {
        if (IsNodeLifetimeEnded || !IsInsideTree())
        {
            return;
        }

        lock (_deferredGodotActionsLock)
        {
            if (IsNodeLifetimeEnded || _speechActivityEvaluationQueued)
            {
                return;
            }

            _speechActivityEvaluationQueued = true;
        }

        _ = CallDeferred(nameof(EvaluateSpeechActivityDeferred));
    }

    /// <summary>
    /// Evaluates drained speech-start notifications for the speech-driven interruption trigger on the Godot thread.
    /// </summary>
    private void EvaluateSpeechActivityDeferred()
    {
        lock (_deferredGodotActionsLock)
        {
            _speechActivityEvaluationQueued = false;
        }

        if (IsNodeLifetimeEnded)
        {
            return;
        }

        List<IVoice> startedVoices = [];
        while (_speechStartNotifications.TryDequeue(out IVoice? voice))
        {
            if (voice is not null && !startedVoices.Contains(voice))
            {
                startedVoices.Add(voice);
            }
        }

        if (startedVoices.Count == 0)
        {
            return;
        }

        RefreshSpeechVoiceSubscriptions();
        foreach (IVoice voice in startedVoices)
        {
            if (IsSpeechInterruptionTrigger(voice))
            {
                RequestSpeechDrivenInterruption();
                break;
            }
        }
    }

    /// <summary>
    /// Determines whether one speech-start event pre-empts the active turn (AI-001 TR-43, TR-45).
    /// </summary>
    private bool IsSpeechInterruptionTrigger(IVoice voice)
    {
        if (!SpeechInterruptionEnabled)
        {
            return false;
        }

        ICharacter ownCharacter = ResolveOwningCharacter();
        if (ownCharacter.TryGetVoice(out IVoice? ownVoice)
            && (ReferenceEquals(voice, ownVoice)
                || (ownVoice is not null
                    && !string.IsNullOrWhiteSpace(ownVoice.Id)
                    && string.Equals(voice.Id, ownVoice.Id, StringComparison.Ordinal))))
        {
            // Own-voice exclusion: the turn's own speech admission never cancels its own turn (TR-45).
            return false;
        }

        return IsAttributedAttentionMember(voice, ownCharacter);
    }

    /// <summary>
    /// Determines whether a speaking voice belongs to exactly one attention-member current-scene character.
    /// </summary>
    /// <remarks>
    /// Ordinal voice-ID matching mirrors the <c>SpeechPerception</c> attribution precedent; blank and ambiguous IDs
    /// never gate, and membership uses retention-threshold snapshot presence rather than the context threshold
    /// (AI-006 TR-33).
    /// </remarks>
    private bool IsAttributedAttentionMember(IVoice voice, ICharacter ownCharacter)
    {
        if (string.IsNullOrWhiteSpace(voice.Id))
        {
            return false;
        }

        ISceneContext scene = GetCurrentSceneContext();
        ICharacter? speaker = null;
        foreach (ICharacter candidate in scene.Characters)
        {
            if (!candidate.TryGetVoice(out IVoice? candidateVoice)
                || candidateVoice is null
                || string.IsNullOrWhiteSpace(candidateVoice.Id)
                || !string.Equals(candidateVoice.Id, voice.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (speaker is not null)
            {
                // Ambiguous attribution never gates, mirroring SpeechPerception's failure without throwing here.
                return false;
            }

            speaker = candidate;
        }

        return speaker is not null
            && !ReferenceEquals(speaker, ownCharacter)
            && GetAttentionSnapshot().Values.ContainsKey(speaker.FullId);
    }

    /// <summary>
    /// Requests expected cancellation of the active turn after an attended speaker started speaking, cutting any
    /// already-audible own speech (AI-001 TR-43, TR-44).
    /// </summary>
    private void RequestSpeechDrivenInterruption()
    {
        CancellationTokenSource? interruptionCancellation;
        lock (_observationStateLock)
        {
            if (IsNodeLifetimeEnded
                || !_enabled
                || !_isProcessingObservations
                || _interruptionRequested)
            {
                return;
            }

            _interruptionRequested = true;
            _immediateReplacementPending = true;
            interruptionCancellation = _activeTurnCancellation;
        }

        CutOwningVoiceSpeech();

        if (interruptionCancellation is null)
        {
            return;
        }

        try
        {
            interruptionCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Natural completion won the race after pre-emption was committed.
        }
    }

    /// <summary>
    /// Cuts the owning character's already-audible speech so the interrupted turn stops speaking immediately.
    /// </summary>
    private void CutOwningVoiceSpeech()
    {
        if (ResolveOwningCharacter().TryGetVoice(out IVoice? voice) && voice is AIVoice aiVoice)
        {
            // Only AI voices own cuttable playback; other voice kinds keep their own window boundaries.
            // The cut is typed deliberately: a default-interface member could never dispatch here because
            // interface mapping is established on the Voice base class (AI-001 TR-44).
            aiVoice.CutSpeech();
        }
    }

    /// <summary>
    /// Refreshes speaking-activity subscriptions and reports whether the speaking gate blocks turn starts.
    /// </summary>
    /// <remarks>
    /// The own character's voice gates unconditionally; other current-scene character voices gate through
    /// retention-threshold attention membership regardless of weight; unattributable voices never gate (AI-001
    /// TR-41).
    /// </remarks>
    private bool IsSpeakingGateClosed()
    {
        RefreshSpeechVoiceSubscriptions();

        ICharacter ownCharacter = ResolveOwningCharacter();
        if (ownCharacter.TryGetVoice(out IVoice? ownVoice) && ownVoice is { IsSpeaking: true })
        {
            return true;
        }

        ISceneContext scene = GetCurrentSceneContext();
        AttentionSnapshot attention = GetAttentionSnapshot();
        foreach (ICharacter candidate in scene.Characters)
        {
            if (ReferenceEquals(candidate, ownCharacter)
                || !candidate.TryGetVoice(out IVoice? voice)
                || voice is not { IsSpeaking: true }
                || string.IsNullOrWhiteSpace(voice.Id))
            {
                continue;
            }

            if (attention.Values.ContainsKey(candidate.FullId))
            {
                return true;
            }
        }

        return false;
    }

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
            _ = CommitObservations(pending);
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
        _ = CommitObservations(pending, throwWhenLifetimeEnded: true);
    }

    private MindScheduleDecision CommitObservations(
        IReadOnlyList<PendingObservation> observations,
        bool throwWhenLifetimeEnded = false)
    {
        bool shouldEvaluateScheduling;
        bool shouldProcessImmediately;
        CancellationTokenSource? interruptionCancellation = null;
        var stampedObservations = new List<AgentObservation>();

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
            DateTimeOffset stamp = DateTimeOffset.UtcNow;
            foreach (PendingObservation pendingObservation in observations)
            {
                AgentObservation stampedObservation = pendingObservation.Observation with
                {
                    ObservedAt = stamp
                };
                PendingObservation stampedPending = pendingObservation with
                {
                    Observation = stampedObservation
                };
                _observationTimeline.Add(stampedObservation);
                _pendingObservations.Enqueue(stampedPending);
                _cumulativeObservationImportance += stampedPending.Importance;

                if (_enabled
                    && HighImportanceInterruptionEnabled
                    && _isProcessingObservations
                    && !_interruptionRequested
                    && stampedPending.Importance >= EffectiveHighImportanceInterruptionThreshold)
                {
                    _interruptionRequested = true;
                    _immediateReplacementPending = true;
                    interruptionCancellation = _activeTurnCancellation;
                }

                stampedObservations.Add(stampedObservation);
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

        foreach (AgentObservation observation in stampedObservations)
        {
            OnObservationIngested(observation);
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
    /// Indicates whether the active foreground turn was claimed as the immediate replacement of an interrupted turn.
    /// </summary>
    /// <remarks>
    /// Derived minds use this to carry per-turn state across an interruption boundary: a replacement turn replaces
    /// the interrupted one and may reuse turn-scoped bindings captured before the interruption instead of rebuilding
    /// them. The flag is captured when the turn claims its batch and cleared when the turn settles.
    /// </remarks>
    protected bool IsForegroundTurnImmediateReplacement
    {
        get;
        private set;
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
            if (IsSpeakingGateClosed())
            {
                // Park the eligible turn behind the speaking gate; a blocking SpeechEnded re-queues this
                // evaluation immediately without polling (AI-001 TR-41, TR-42).
                return;
            }

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

        if (IsSpeakingGateClosed())
        {
            // A new turn may not start while a gating voice speaks; eligibility stays pending until the gate opens
            // (AI-001 TR-41, TR-42).
            return false;
        }

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
            IsForegroundTurnImmediateReplacement = _immediateReplacementPending;
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
                IsForegroundTurnImmediateReplacement = false;
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
