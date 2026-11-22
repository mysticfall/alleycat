using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// Adapts this Mind's current attention snapshot into decision-driven, cue-owned gaze assignments for its owning
/// character: each evaluation asks the decision model to choose one attended character (or nothing in particular)
/// and applies the chosen subject's best cue.
/// </summary>
/// <remarks>
/// The periodic trigger is deliberately isolated from the policy evaluation path. Speech-adjacent nudges — speak
/// intentions, own speech end, attended speaker end — call <see cref="RequestEvaluation()"/> through the same path.
/// Evaluations run at most one in flight; nudges during a flight coalesce into the next evaluation. Every decision
/// failure keeps the current target, logs a Warning, and backs off the cadence instead of throwing into
/// <c>_Process</c>.
/// </remarks>
[GlobalClass]
public partial class AttentionGazeTargetSelector : Node
{
    private const int RecentEventCount = 6;
    private const int MaximumSceneCharacters = 8;
    private const int RecentEventCharacterBudget = 2000;
    private const double SpeakIntentionExpirySeconds = 30d;

    /// <summary>The decision-payload dump category this gate files under: one directory per producer.</summary>
    private const string DumpCategory = "gaze";

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _triggerLock = new();
    private readonly Lock _evaluationStateLock = new();
    private Func<ISceneContext> _sceneContextLoader = static ()
        => Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
    private Func<IDecisionClient> _decisionClientLoader = static ()
        => Game.Instance.GetRequiredService<IDecisionClient>();
    private Func<ILoreQueryService?> _loreQueryServiceLoader = static () => Game.Instance.GetRequiredService<ILoreQueryService>();
    private Mind? _mind;
    private ICharacter? _character;
    private IComponentProjectionNotifier? _componentProjectionNotifier;
    private DecisionGazePolicy? _policy;
    private IVision? _vision;
    private ILogger<AttentionGazeTargetSelector>? _logger;
    private DecisionPayloadDumpRecorder? _dumpRecorder;
    private CancellationTokenSource? _evaluationCancellation;
    private GazeSpeakIntention? _speakIntention;
    private GazeEvaluationTrigger _pendingTrigger = GazeEvaluationTrigger.Cadence;
    private GazeCompletedEvaluation? _completedEvaluation;
    private Task _scenePremiseResolution = Task.CompletedTask;
    private HashSet<string>? _lastEvaluatedMembership;
    private volatile string? _scenePremise;
    private int _scenePremiseResolutionStarted;
    private string? _appliedSubjectFullId;
    private VisualCue? _appliedCue;
    private double _appliedAtSeconds;
    private double _secondsSinceLastEvaluation;
    private double _failureBackoffSeconds;
    private int _coalescedNudges;
    private volatile bool _evaluationRequested;
    private volatile bool _evaluationInFlight;
    private bool _isReady;
    private bool _initialEvaluationPending;

    /// <summary>
    /// Gets or sets the maximum interval between decision-gaze evaluations, including the failure backoff in
    /// effect. Nudges may request an earlier evaluation.
    /// </summary>
    [ExportGroup("Cadence")]
    [Export(PropertyHint.Range, "0.01,60,0.01,or_greater")]
    public float EvaluationIntervalSeconds
    {
        get; set;
    } = 0.5f;

    /// <inheritdoc />
    public override void _Ready()
    {
        ValidateCadenceAuthoring();
        _mind = GetParent() as Mind
            ?? throw new InvalidOperationException(
                $"Attention gaze selector '{GetPath()}' requires a direct {nameof(Mind)} parent.");
        _character = _mind.OwningCharacter;
        _componentProjectionNotifier = _character as IComponentProjectionNotifier
            ?? throw new InvalidOperationException(
                $"Attention gaze selector '{GetPath()}' requires its owning character '{_character.GetType().FullName}' to implement {nameof(IComponentProjectionNotifier)}.");

        _componentProjectionNotifier.ComponentsRefreshed += OnComponentsRefreshed;
        _mind.SpeakIntentionNotified += OnSpeakIntentionNotified;
        _mind.OwnSpeechEndedNotified += OnOwnSpeechEndedNotified;
        _mind.AttendedSpeakerEndedNotified += OnAttendedSpeakerEndedNotified;
        _mind.SpeechSegmentLifecycleNotified += OnSpeechSegmentLifecycleNotified;
        _isReady = true;
        _initialEvaluationPending = true;

        if (_componentProjectionNotifier.HasComponentProjection)
        {
            BindVisionAfterProjection();
        }
        else
        {
            LogWaitingForProjection();
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_isReady || _vision is null || _policy is null)
        {
            return;
        }

        if (!double.IsFinite(delta) || delta < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "Attention gaze selector process delta must be finite and non-negative.");
        }

        TryApplyCompletedEvaluation();

        if (_initialEvaluationPending)
        {
            if (_scenePremiseResolution is { IsCompleted: false })
            {
                // Cold-start gate (GZ2-B1): the first evaluation waits until the one-shot premise resolution
                // settles — resolved text, cleanly absent, or contained failure — so no dispatched question can
                // race the premise. Later evaluations are unaffected.
                return;
            }

            _initialEvaluationPending = false;
            _ = StartEvaluation();
            TryApplyCompletedEvaluation();
            return;
        }

        _secondsSinceLastEvaluation += delta;
        if (!_evaluationRequested && _secondsSinceLastEvaluation < EvaluationIntervalSeconds + _failureBackoffSeconds)
        {
            PollAttentionMembershipChange();
            return;
        }

        if (!StartEvaluation())
        {
            // An evaluation is still in flight: the pending request flag stays set and coalesces into the next
            // evaluation once the flight completes.
            return;
        }

        _secondsSinceLastEvaluation = 0d;
        TryApplyCompletedEvaluation();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_componentProjectionNotifier is { } componentProjectionNotifier)
        {
            componentProjectionNotifier.ComponentsRefreshed -= OnComponentsRefreshed;
            _componentProjectionNotifier = null;
        }

        if (_mind is { } mind)
        {
            mind.SpeakIntentionNotified -= OnSpeakIntentionNotified;
            mind.OwnSpeechEndedNotified -= OnOwnSpeechEndedNotified;
            mind.AttendedSpeakerEndedNotified -= OnAttendedSpeakerEndedNotified;
            mind.SpeechSegmentLifecycleNotified -= OnSpeechSegmentLifecycleNotified;
        }

        _evaluationCancellation?.Cancel();
        _evaluationCancellation?.Dispose();
        _evaluationCancellation = null;
        _evaluationInFlight = false;
        _completedEvaluation = null;
        _speakIntention = null;
        _scenePremise = null;
        _scenePremiseResolution = Task.CompletedTask;
        _lastEvaluatedMembership = null;
        _vision = null;
        _policy = null;
        _dumpRecorder = null;
        _character = null;
        _mind = null;
        _isReady = false;
        _initialEvaluationPending = false;
        _evaluationRequested = false;
        _secondsSinceLastEvaluation = 0d;
        _failureBackoffSeconds = 0d;
        _coalescedNudges = 0;
        _appliedSubjectFullId = null;
        AppliedCueValue = null;
        _appliedCue = null;
    }

    /// <summary>
    /// Requests one evaluation on the next process callback. The nudge coalesces into the next
    /// evaluation while one is in flight and does not wait out the failure backoff — only the
    /// periodic cadence is delayed after failures, so speech-paced nudges stay responsive. A
    /// meaningful attention-membership change additionally resets the failure backoff, keeping the
    /// always-on structural trigger audible during decision-service flakiness.
    /// </summary>
    public void RequestEvaluation() => RequestEvaluation(GazeEvaluationTrigger.Cadence);

    /// <summary>
    /// Gets the currently applied gaze cue's encoded value (<c>subject#cue</c>), or null when the gaze is clear —
    /// the wait-decision evidence's cue-level current-gaze source.
    /// </summary>
    internal string? AppliedCueValue
    {
        get; private set;
    }

    /// <summary>Gets whether the selector currently holds an applied gaze target.</summary>
    internal bool HasAppliedTarget => AppliedCueValue is not null;

    /// <summary>Gets how long the currently applied gaze cue has been held, in seconds.</summary>
    internal double AppliedDwellSeconds
        => AppliedCueValue is null ? 0d : Math.Max(0d, _clock.Elapsed.TotalSeconds - _appliedAtSeconds);

    /// <summary>
    /// Replaces scene-context resolution before activation for focused runtime coverage.</summary>
    internal void SetSceneContextLoaderForTesting(Func<ISceneContext> sceneContextLoader)
    {
        ArgumentNullException.ThrowIfNull(sceneContextLoader);
        ThrowIfActivatedForTesting(nameof(SetSceneContextLoaderForTesting));
        _sceneContextLoader = sceneContextLoader;
    }

    /// <summary>Replaces decision-client resolution before activation for focused runtime coverage.</summary>
    internal void SetDecisionClientForTesting(IDecisionClient decisionClient)
    {
        ArgumentNullException.ThrowIfNull(decisionClient);
        ThrowIfActivatedForTesting(nameof(SetDecisionClientForTesting));
        _decisionClientLoader = () => decisionClient;
    }

    /// <summary>Replaces lore-query-service resolution before activation for focused runtime coverage.</summary>
    internal void SetLoreQueryServiceForTesting(ILoreQueryService? loreQueryService)
    {
        ThrowIfActivatedForTesting(nameof(SetLoreQueryServiceForTesting));
        _loreQueryServiceLoader = () => loreQueryService;
    }

    internal void RequestEvaluation(GazeEvaluationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!_isReady)
        {
            return;
        }

        lock (_triggerLock)
        {
            _pendingTrigger = trigger;
        }

        _evaluationRequested = true;
        if (_evaluationInFlight)
        {
            _ = Interlocked.Increment(ref _coalescedNudges);
        }
    }

    private void ValidateCadenceAuthoring()
    {
        if (!float.IsFinite(EvaluationIntervalSeconds) || EvaluationIntervalSeconds <= 0f)
        {
            throw new InvalidOperationException(
                $"Attention gaze selector '{GetPath()}' requires {nameof(EvaluationIntervalSeconds)} to be finite and positive, but found '{EvaluationIntervalSeconds}'.");
        }
    }

    private void OnComponentsRefreshed()
    {
        if (_isReady)
        {
            BindVisionAfterProjection();
        }
    }

    private void OnSpeakIntentionNotified(IReadOnlyList<string> recipients)
    {
        if (!_isReady)
        {
            return;
        }

        _speakIntention = new GazeSpeakIntention(recipients, _clock.Elapsed.TotalSeconds);
        RequestEvaluation(new GazeEvaluationTrigger(GazeEvaluationTriggerKind.SpeakInvoked, recipients));
    }

    private void OnOwnSpeechEndedNotified()
    {
        if (_isReady)
        {
            _speakIntention = null;
            RequestEvaluation(new GazeEvaluationTrigger(GazeEvaluationTriggerKind.OwnSpeechEnded));
        }
    }

    private void OnAttendedSpeakerEndedNotified(string speakerFullId)
    {
        if (_isReady)
        {
            RequestEvaluation(new GazeEvaluationTrigger(
                GazeEvaluationTriggerKind.AttendedSpeakerEnded,
                [speakerFullId]));
        }
    }

    /// <summary>
    /// Nudges an evaluation when an attended speaker's speech segment starts or resumes (GZ2-B3): the notification
    /// is attended-speaker-gated by the Mind, so it fires exactly when someone the character attends to begins
    /// speaking. The standing own-action evidence already carries the speaking state at composition time, so the
    /// trigger carries no character context; one-in-flight coalescing absorbs rapid segment onsets.
    /// </summary>
    private void OnSpeechSegmentLifecycleNotified(SpeechSegmentLifecycleNotification notification)
    {
        if (_isReady
            && notification.Transition is SpeechSegmentLifecycleTransition.Started
                or SpeechSegmentLifecycleTransition.Resumed)
        {
            RequestEvaluation(new GazeEvaluationTrigger(GazeEvaluationTriggerKind.AttendedSpeakerStarted));
        }
    }

    private void BindVisionAfterProjection()
    {
        ICharacter character = _character
            ?? throw new InvalidOperationException("Attention gaze selector has no resolved owning character after activation.");
        IVision vision = character.RequireVision();
        if (ReferenceEquals(_vision, vision))
        {
            LogProjectionRefreshWithoutVisionChange();
            return;
        }

        _vision = vision;
        _policy = new DecisionGazePolicy(_decisionClientLoader());
        _dumpRecorder = CreateDumpRecorderIfEnabled();
        StartScenePremiseResolution();
        _initialEvaluationPending = true;
        _evaluationRequested = false;
        _secondsSinceLastEvaluation = 0d;
        _appliedSubjectFullId = null;
        AppliedCueValue = null;
        _appliedCue = null;
        _appliedAtSeconds = 0d;
        LogVisionBound(vision);
    }

    private DecisionPayloadDumpRecorder? CreateDumpRecorderIfEnabled()
    {
        if (!AIDiagnosticsSettings.LoadOrDefault().EnableDecisionPayloadLogging)
        {
            return null;
        }

        ICharacter character = _character
            ?? throw new InvalidOperationException("Attention gaze selector has no owning character at recorder creation.");
        return new DecisionPayloadDumpRecorder(
            DumpCategory,
            character.FullId,
            character.Id,
            static () => ProjectSettings.GlobalizePath(MindSessionTranscriptRecorder.UserRootPath),
            TryGetLogger() ?? NullLogger<AttentionGazeTargetSelector>.Instance);
    }

    /// <summary>
    /// Resolves the authored scenario premise once per selector lifetime (playtest R1): the parent
    /// <see cref="AgenticMind"/> session's scenario manager renders its description against the same core render
    /// context the session prompt uses, and the cached verbatim text then rides every gaze question after an
    /// explicit framing label. All inputs are gathered on the Godot thread; only the manager call is asynchronous,
    /// and its task is held so the first evaluation can gate on its settlement (GZ2-B1). Every failure — including
    /// a plain Mind parent with no scenario manager — is contained: the premise stays absent and evaluations
    /// proceed unchanged.
    /// </summary>
    private void StartScenePremiseResolution()
    {
        if (Interlocked.Exchange(ref _scenePremiseResolutionStarted, 1) != 0)
        {
            return;
        }

        try
        {
            if (_mind is not AgenticMind { ScenarioManager: { } manager } || _character is not { } character)
            {
                return;
            }

            Dictionary<string, object?> coreContext = AgenticMind.CreateCoreRenderContext(
                character,
                _sceneContextLoader(),
                _mind.GetContextEligibleAttentionIDs());
            _scenePremiseResolution = ResolveScenePremiseAsync(manager, coreContext);
        }
        catch (Exception exception)
        {
            LogScenePremiseResolutionFailure(exception);
        }
    }

    private async Task ResolveScenePremiseAsync(
        ScenarioManager manager,
        IReadOnlyDictionary<string, object?> coreContext)
    {
        try
        {
            Scenario? scenario = await manager.GetCurrentScenario(coreContext).ConfigureAwait(false);
            if (!_isReady)
            {
                return;
            }

            string? description = scenario?.Description.Trim();
            _scenePremise = string.IsNullOrWhiteSpace(description) ? null : description;
            if (_scenePremise is null)
            {
                LogScenePremiseAbsent();
            }
        }
        catch (Exception exception)
        {
            LogScenePremiseResolutionFailure(exception);
        }
    }

    /// <summary>
    /// Starts one evaluation when none is in flight. Returns whether an evaluation boundary was consumed — either a
    /// dispatched evaluation or the zero-candidate short-circuit — so the caller can reset its cadence timer.
    /// </summary>
    private bool StartEvaluation()
    {
        if (_evaluationInFlight)
        {
            return false;
        }

        _evaluationRequested = false;
        GazeEvaluationTrigger trigger = ConsumePendingTrigger();
        int coalescedNudges = Interlocked.Exchange(ref _coalescedNudges, 0);

        Mind mind = _mind
            ?? throw new InvalidOperationException("Attention gaze selector has no Mind parent after activation.");
        ISceneContext scene = _sceneContextLoader();
        AttentionSnapshot snapshot = mind.GetAttentionSnapshot();
        RecordEvaluatedMembership(snapshot);
        List<AttentionGazeTargetCandidate<VisualCue>> cueCandidates = ResolveCandidates(snapshot, scene);
        IReadOnlyList<GazeCueCandidate> composedCues = GazeCueCandidates.Compose(cueCandidates);

        if (composedCues.Count == 0 && _appliedSubjectFullId is null)
        {
            // No candidate exists to look at and nothing is currently assigned: the decision-model answer is
            // deterministic, so no call is dispatched and the clear presentation fallback stays in effect.
            _failureBackoffSeconds = 0d;
            LogZeroCandidateShortCircuit();
            return true;
        }

        GazeEvaluationInput input = ComposeInput(trigger, composedCues, mind, scene);
        _evaluationCancellation?.Dispose();
        _evaluationCancellation = new CancellationTokenSource();
        _evaluationInFlight = true;
        _ = RunEvaluationAsync(input, coalescedNudges, _evaluationCancellation.Token);
        return true;
    }

    private GazeEvaluationTrigger ConsumePendingTrigger()
    {
        lock (_triggerLock)
        {
            GazeEvaluationTrigger trigger = _pendingTrigger;
            _pendingTrigger = GazeEvaluationTrigger.Cadence;
            return trigger;
        }
    }

    /// <summary>
    /// Polls the attention snapshot's membership for a meaningful structural change — a subject gained, or the
    /// currently applied gaze subject lost — and nudges an evaluation, resetting the failure backoff (GZ2-B3):
    /// containment must not mute the only always-on structural trigger. The poll runs only between evaluation
    /// boundaries, at most once per base cadence interval regardless of any backoff, so a healthy cadence never
    /// reads an extra snapshot and membership flapping is bounded by the one-in-flight plus latest-wins
    /// coalescing that already shapes nudges.
    /// </summary>
    private void PollAttentionMembershipChange()
    {
        if (_secondsSinceLastEvaluation < EvaluationIntervalSeconds || _lastEvaluatedMembership is null)
        {
            return;
        }

        Mind? mind = _mind;
        if (mind is null)
        {
            return;
        }

        AttentionSnapshot snapshot = mind.GetAttentionSnapshot();
        if (!IsMeaningfulMembershipChange(snapshot))
        {
            return;
        }

        RecordEvaluatedMembership(snapshot);
        _failureBackoffSeconds = 0d;
        _evaluationRequested = true;
        LogMembershipChangeNudge();
    }

    /// <summary>Determines whether the snapshot's membership changed meaningfully against the last-evaluated set:
    /// a subject appeared, or the currently applied gaze subject disappeared from attention.</summary>
    private bool IsMeaningfulMembershipChange(AttentionSnapshot snapshot)
    {
        if (_appliedSubjectFullId is { } applied && !snapshot.Values.ContainsKey(applied))
        {
            return true;
        }

        foreach (string fullId in snapshot.Values.Keys)
        {
            if (!_lastEvaluatedMembership!.Contains(fullId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Re-baselines the last-evaluated membership set from a snapshot read at an evaluation boundary.</summary>
    private void RecordEvaluatedMembership(AttentionSnapshot snapshot)
        => _lastEvaluatedMembership = [.. snapshot.Values.Keys];

    private GazeEvaluationInput ComposeInput(
        GazeEvaluationTrigger trigger,
        IReadOnlyList<GazeCueCandidate> composedCues,
        Mind mind,
        ISceneContext scene)
    {
        ICharacter character = _character
            ?? throw new InvalidOperationException("Attention gaze selector has no owning character at input composition.");
        double elapsedSeconds = _clock.Elapsed.TotalSeconds;
        return new GazeEvaluationInput(
            character.FullId,
            [.. composedCues.Select(candidate => candidate with
            {
                CueDescription = DescribeCueSynchronously(candidate, scene, character),
            })],
            ComposeOwnAction(mind, elapsedSeconds),
            AppliedCueValue,
            AppliedCueValue is null ? 0d : Math.Max(0d, elapsedSeconds - _appliedAtSeconds),
            trigger,
            RenderRecentEvents(mind, character),
            ScenePremise: _scenePremise,
            SceneCharacters: ComposeSceneCharacters(character, scene));
    }

    /// <summary>
    /// Renders one cue's authored description on the Godot thread: the cue's own <see cref="VisualCue.Describe" />
    /// template completes synchronously (the established canonical-rendering precedent), and any failure or
    /// incomplete render leaves the description absent so the option criteria fall back to the cue kind word.
    /// </summary>
    private static string? DescribeCueSynchronously(
        GazeCueCandidate candidate,
        ISceneContext scene,
        ICharacter observer)
    {
        if (scene.Find(candidate.SubjectFullId) is not IVisualSubject subject)
        {
            return null;
        }

        foreach (VisualCue? cue in subject.VisualCues)
        {
            if (cue is null
                || !IsInstanceValid(cue)
                || !string.Equals(cue.ID, candidate.CueKey, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                ValueTask<string> description = cue.Describe(scene, observer);
                return description.IsCompletedSuccessfully ? description.Result : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Composes the bounded scene-character roster for the evidence summaries: every scene character other than
    /// the owner, ordered by full ID, each summarised by their authored lore description through the shared cached
    /// resolver — absent or asynchronous resolutions carry the bare identity honestly.
    /// </summary>
    private IReadOnlyList<GazeSceneCharacter> ComposeSceneCharacters(ICharacter character, ISceneContext scene)
    {
        ILoreQueryService? queryService = ResolveLoreQueryService();
        List<GazeSceneCharacter> roster = [];
        foreach (ICharacter other in scene.Characters
                     .Where(static candidate => candidate is not null)
                     .OrderBy(static candidate => candidate.FullId, StringComparer.Ordinal))
        {
            if (ReferenceEquals(other, character) || roster.Count >= MaximumSceneCharacters)
            {
                continue;
            }

            string? description = queryService is null
                ? null
                : CharacterLoreDescriptions.ResolveSynchronously(
                    queryService,
                    character.FullId,
                    scene.Content,
                    other.FullId,
                    TryGetLogger());
            roster.Add(new GazeSceneCharacter(other.FullId, description));
        }

        return roster;
    }

    /// <summary>Resolves the shared lore query service when game services are available; null keeps summaries bare.</summary>
    private ILoreQueryService? ResolveLoreQueryService()
    {
        try
        {
            return _loreQueryServiceLoader();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLoreServiceUnavailable(exception);
            return null;
        }
    }

    private GazeOwnAction ComposeOwnAction(Mind mind, double elapsedSeconds)
    {
        GazeSpeakIntention? intention = _speakIntention;
        if (intention is not null && elapsedSeconds - intention.NotedAtSeconds > SpeakIntentionExpirySeconds)
        {
            // A speak intention that never opened a speaking window (for example a pre-hand-off withdrawal) is
            // dropped instead of pinning the own-action state forever.
            _speakIntention = null;
            intention = null;
        }

        return intention is not null
            ? new GazeOwnAction(GazeOwnActionKind.SpeakingTo, intention.Recipients)
            : mind.TryGetAttendedSpeaker(out string? speakerFullId)
                ? new GazeOwnAction(GazeOwnActionKind.Hearing, [speakerFullId])
                : GazeOwnAction.IdleAction;
    }

    /// <summary>Renders the bounded recent-event window through the canonical observation rendering path.</summary>
    private static IReadOnlyList<string> RenderRecentEvents(Mind mind, ICharacter character)
    {
        IReadOnlyList<AgentObservation> timeline = mind.GetObservationTimelineSnapshot();
        if (timeline.Count == 0)
        {
            return [];
        }

        List<AgentObservation> window = [.. timeline.Skip(Math.Max(0, timeline.Count - RecentEventCount))];
        // The renderer is a deterministic CPU-only canonical-text projection: every overload completes
        // synchronously, so the value is available without yielding off the Godot thread. Event times render
        // relative to this evaluation's composition moment.
        string rendered = new ObservationHistoryRenderer(character)
            .RenderAsync(window, timeline, mind.GameClock.NowSeconds)
            .GetAwaiter()
            .GetResult();
        if (string.IsNullOrEmpty(rendered))
        {
            return [];
        }

        List<string> lines = [.. rendered.Split('\n')];
        int totalCharacters = lines.Sum(static line => line.Length);
        while (lines.Count > 1 && totalCharacters > RecentEventCharacterBudget)
        {
            totalCharacters -= lines[0].Length;
            lines.RemoveAt(0);
        }

        return lines;
    }

    private async Task RunEvaluationAsync(
        GazeEvaluationInput input,
        int coalescedNudges,
        CancellationToken cancellationToken)
    {
        GazeEvaluationResult result;
        try
        {
            result = await (_policy ?? throw new InvalidOperationException(
                    "Attention gaze selector policy was not initialised."))
                .EvaluateAsync(input, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new GazeEvaluationResult(
                GazeDecision.CancelledDecision(),
                null,
                0,
                0,
                0,
                null,
                []);
        }
        catch (Exception exception)
        {
            result = new GazeEvaluationResult(
                GazeDecision.Failed("unexpected evaluation failure"),
                null,
                0,
                0,
                0,
                null,
                []);
            LogUnexpectedEvaluationFailure(exception);
        }

        GazeCompletedEvaluation completion = new(input, coalescedNudges, result, DateTimeOffset.Now);
        lock (_evaluationStateLock)
        {
            _evaluationInFlight = false;
            _completedEvaluation = completion;
        }
    }

    private void TryApplyCompletedEvaluation()
    {
        GazeCompletedEvaluation? completion;
        lock (_evaluationStateLock)
        {
            completion = _completedEvaluation;
            _completedEvaluation = null;
        }

        if (completion is null || !_isReady || _vision is null)
        {
            return;
        }

        IVision vision = _vision;
        GazeEvaluationResult result = completion.Result;
        switch (result.Decision.Kind)
        {
            case GazeDecisionKind.Cancelled:
                LogEvaluationCancelled();
                return;
            case GazeDecisionKind.Failure:
                _failureBackoffSeconds = GazeFailureBackoff.NextSeconds(_failureBackoffSeconds, EvaluationIntervalSeconds);
                LogDecisionFailure(result.Decision.Failure);
                RecordDump(completion, "failed", result.Decision.Failure);
                return;
            case GazeDecisionKind.None:
                _failureBackoffSeconds = 0d;
                RecordDump(completion, DecisionGazePolicy.NoneOptionValue, null);
                if (_appliedSubjectFullId is not null)
                {
                    ApplyDecision(vision, AttentionGazeTargetDecision<VisualCue>.Clear());
                    _appliedSubjectFullId = null;
                    AppliedCueValue = null;
                    _appliedCue = null;
                }

                return;
            case GazeDecisionKind.ChooseCue:
                _failureBackoffSeconds = 0d;
                string cueValue = result.Decision.CueValue
                    ?? throw new InvalidOperationException("Attention gaze decision chose a cue without a value.");
                RecordDump(completion, cueValue, null);
                ApplyCueDecision(vision, cueValue);
                return;
            default:
                throw new InvalidOperationException($"Unknown gaze decision kind '{result.Decision.Kind}'.");
        }
    }

    /// <summary>
    /// Applies a chosen subject-and-cue option value: parse it, resolve the subject's cue by its authored ID with
    /// full re-validation, and assign it. A vanished subject, a missing cue, or an unparseable value keeps the
    /// current target as a no-op for the next cadence to re-evaluate.
    /// </summary>
    private void ApplyCueDecision(IVision vision, string cueValue)
    {
        if (!GazePromptText.TryParseCueValue(cueValue, out string subjectFullId, out string cueKey))
        {
            LogSubjectVanished(cueValue);
            return;
        }

        VisualCue? cue = ResolveCue(subjectFullId, cueKey);
        if (cue is null)
        {
            LogSubjectVanished(cueValue);
            return;
        }

        if (ReferenceEquals(_appliedCue, cue))
        {
            return;
        }

        ApplyDecision(vision, AttentionGazeTargetDecision<VisualCue>.Set(cue));
        _appliedSubjectFullId = subjectFullId;
        AppliedCueValue = cueValue;
        _appliedCue = cue;
        _appliedAtSeconds = _clock.Elapsed.TotalSeconds;
    }

    private static List<AttentionGazeTargetCandidate<VisualCue>> ResolveCandidates(
        AttentionSnapshot snapshot,
        ISceneContext scene)
    {
        var candidates = new List<AttentionGazeTargetCandidate<VisualCue>>();
        foreach (KeyValuePair<string, float> attention in snapshot.Values)
        {
            if (scene.Find(attention.Key) is not IVisualSubject subject)
            {
                continue;
            }

            IReadOnlyList<VisualCue> cues = subject.VisualCues;
            for (int cueOrder = 0; cueOrder < cues.Count; cueOrder++)
            {
                VisualCue? cue = cues[cueOrder];
                if (cue is null || !IsInstanceValid(cue) || !float.IsFinite(cue.Prominence) || cue.Prominence <= 0f)
                {
                    continue;
                }

                candidates.Add(new AttentionGazeTargetCandidate<VisualCue>(
                    attention.Key,
                    cue.ID,
                    cueOrder,
                    cue,
                    attention.Value,
                    cue.Prominence,
                    IsValid: true));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Resolves one subject's cue by its authored ID with live re-validation: the subject must exist in the scene
    /// and the cue must be instantiated with a finite, positive prominence.
    /// </summary>
    private VisualCue? ResolveCue(string subjectFullId, string cueKey)
    {
        if (_sceneContextLoader().Find(subjectFullId) is not IVisualSubject subject)
        {
            return null;
        }

        foreach (VisualCue? cue in subject.VisualCues)
        {
            if (cue is not null
                && IsInstanceValid(cue)
                && float.IsFinite(cue.Prominence)
                && cue.Prominence > 0f
                && string.Equals(cue.ID, cueKey, StringComparison.Ordinal))
            {
                return cue;
            }
        }

        return null;
    }

    private void ApplyDecision(IVision vision, AttentionGazeTargetDecision<VisualCue> decision)
    {
        switch (decision.Action)
        {
            case AttentionGazeTargetAction.None:
                return;
            case AttentionGazeTargetAction.SetLookTarget:
                VisualCue target = decision.Target
                    ?? throw new InvalidOperationException("Attention gaze target policy requested a target assignment without a target.");
                vision.SetLookTarget(target);
                LogSetDecision(target);
                return;
            case AttentionGazeTargetAction.ClearLookTarget:
                vision.ClearLookTarget();
                LogClearDecision();
                return;
            default:
                throw new InvalidOperationException($"Unknown attention gaze target action '{decision.Action}'.");
        }
    }

    private void RecordDump(GazeCompletedEvaluation completion, string verdict, string? failure)
    {
        DecisionPayloadDumpRecorder? recorder = _dumpRecorder;
        if (recorder is null)
        {
            return;
        }

        recorder.Record(new DecisionPayloadDump(
            completion.SettledAt,
            _character?.FullId ?? completion.Input.OwnerFullId,
            completion.Input.Trigger.Token,
            verdict,
            completion.Result.Confidence,
            completion.Result.CompositionMilliseconds,
            completion.Result.DecisionMilliseconds,
            completion.Result.EvidenceCharacters,
            completion.CoalescedNudges,
            failure,
            completion.Result.Evidence,
            completion.Result.Questions));
    }

    private void ThrowIfActivatedForTesting(string operation)
    {
        if (_isReady)
        {
            throw new InvalidOperationException($"{operation} must be called before the attention gaze selector activates.");
        }
    }

    private void LogWaitingForProjection()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Attention gaze selector is waiting for its owner's component projection.");
        }
    }

    private void LogVisionBound(IVision vision)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug(
                "Attention gaze selector bound Vision component {VisionType} after component projection.",
                vision.GetType().FullName ?? vision.GetType().Name);
        }
    }

    private void LogProjectionRefreshWithoutVisionChange()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Attention gaze selector observed a component projection refresh without a Vision binding change.");
        }
    }

    private void LogZeroCandidateShortCircuit()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug(
                "Attention gaze selector found no gaze candidates and no current target; staying clear without a decision call.");
        }
    }

    private void LogMembershipChangeNudge()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug(
                "Attention gaze selector observed a meaningful attention-membership change; nudging an evaluation and resetting the failure backoff.");
        }
    }

    private void LogSubjectVanished(string subjectFullId)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug(
                "Attention gaze decision subject '{SubjectFullId}' has no valid cue at application time; keeping the current target.",
                subjectFullId);
        }
    }

    private void LogDecisionFailure(string? failure)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        logger?.LogWarning(
            "Attention gaze decision failed ({Failure}); keeping the current target and backing off. Backoff: {BackoffSeconds:F1}s.",
            failure ?? "unknown failure",
            _failureBackoffSeconds);
    }

    private void LogEvaluationCancelled()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Attention gaze evaluation was cancelled; nothing was applied.");
        }
    }

    private void LogUnexpectedEvaluationFailure(Exception exception)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        logger?.LogWarning(
            exception,
            "Attention gaze evaluation threw unexpectedly; keeping the current target and backing off.");
    }

    private void LogScenePremiseAbsent()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.LogDebug("Attention gaze selector found no scenario premise; questions compose without one.");
        }
    }

    private void LogScenePremiseResolutionFailure(Exception exception)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        logger?.LogWarning(
            exception,
            "Resolving the scenario premise for attention gaze failed; questions compose without one.");
    }

    private void LogLoreServiceUnavailable(Exception exception)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        logger?.LogWarning(
            exception,
            "Resolving the lore query service for attention gaze failed; evidence candidate summaries compose "
            + "without lore descriptions.");
    }

    private void LogSetDecision(VisualCue target)
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Trace) == true)
        {
            logger.LogTrace("Attention gaze selector applied SetLookTarget for cue {CueId}.", target.ID);
        }
    }

    private void LogClearDecision()
    {
        ILogger<AttentionGazeTargetSelector>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Trace) == true)
        {
            logger.LogTrace("Attention gaze selector applied ClearLookTarget after its decision chose none.");
        }
    }

    private ILogger<AttentionGazeTargetSelector>? TryGetLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<AttentionGazeTargetSelector>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    /// <summary>The speak intention captured at the tool's recipient resolution, with its capture instant.</summary>
    private sealed record GazeSpeakIntention(IReadOnlyList<string> Recipients, double NotedAtSeconds);

    /// <summary>One finished evaluation awaiting application on the Godot thread.</summary>
    private sealed record GazeCompletedEvaluation(
        GazeEvaluationInput Input,
        int CoalescedNudges,
        GazeEvaluationResult Result,
        DateTimeOffset SettledAt);
}
