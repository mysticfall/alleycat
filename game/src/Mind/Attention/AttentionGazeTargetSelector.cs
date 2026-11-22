using System.Diagnostics;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Decision;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlleyCat.Mind.Attention;

/// <summary>
/// Adapts this Mind's current attention snapshot into decision-driven, cue-owned gaze assignments for its owning
/// character: each evaluation asks the decision model to choose one attended cue (or nothing in particular)
/// and revalidates that exact cue before applying it.
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
    private const double SpeakIntentionExpirySeconds = 30d;

    /// <summary>The decision-payload dump category this gate files under: one directory per producer.</summary>
    private const string DumpCategory = "gaze";

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _triggerLock = new();
    private Mind? _mind;
    private ICharacter? _character;
    private IComponentProjectionNotifier? _componentProjectionNotifier;
    private SubjectSceneDecisions? _subject;
    private bool _due;
    internal GazeOwnAction CurrentOwnAction => ComposeOwnAction(_mind!, _clock.Elapsed.TotalSeconds);
    private IVision? _vision;
    private ILogger<AttentionGazeTargetSelector>? _logger;
    private DecisionPayloadDumpRecorder? _dumpRecorder;
    private MindSessionTranscriptRecorder? _transcript;
    private CancellationTokenSource? _evaluationCancellation;
    private GazeSpeakIntention? _speakIntention;
    private GazeEvaluationTrigger _pendingTrigger = GazeEvaluationTrigger.Cadence;
    private HashSet<string>? _lastEvaluatedMembership;
    private string? _appliedSubjectFullId;
    private VisualCue? _appliedCue;
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
        if (!_isReady || _vision is null || _subject is null)
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

        _secondsSinceLastEvaluation += delta;
        if (IsEvaluationDue())
        {
            _due = true;
        }
        else
        {
            PollAttentionMembershipChange();
        }
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
        _due = false;
        _speakIntention = null;
        _lastEvaluatedMembership = null;
        _vision = null;
        if (_subject is { } subject)
        {
            subject.Unregister(Prepare);
            subject.Gaze = null;
            _subject = null;
        }
        _dumpRecorder = null;
        _transcript = null;
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
    {
        get => AppliedCueValue is null ? 0d : Math.Max(0d, _clock.Elapsed.TotalSeconds - field);
        private set;
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
            _evaluationRequested = true;
        }
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
        _evaluationCancellation?.Cancel();
        _evaluationCancellation?.Dispose();
        _evaluationCancellation = new();
        _evaluationInFlight = false;
        if (_subject is null)
        {
            _subject = _mind!.GetSceneDecisions();
            _subject.Gaze = this;
            _subject.Register(Prepare);
            ProcessPriority = _mind.ProcessPriority - 1;
        }
        _dumpRecorder = CreateDumpRecorderIfEnabled();
        _transcript = _mind!.DecisionTranscript;
        _initialEvaluationPending = true;
        _evaluationRequested = false;
        _secondsSinceLastEvaluation = 0d;
        _appliedSubjectFullId = null;
        AppliedCueValue = null;
        _appliedCue = null;
        AppliedDwellSeconds = 0d;
        LogVisionBound(vision);
    }

    private DecisionPayloadDumpRecorder? CreateDumpRecorderIfEnabled()
    {
        if (!_mind!.WaitDiagnosticsSettings.EnableDecisionPayloadLogging)
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

    private SceneDecisionGroup? Prepare()
    {
        // A slow request can settle after the frame noticed an overdue cadence. A failure moves that
        // cadence boundary; an actual nudge remains pending independently and still bypasses backoff.
        if (!_due || _evaluationInFlight || !IsEvaluationDue())
        {
            return null;
        }

        _due = false;
        _initialEvaluationPending = false;
        _secondsSinceLastEvaluation = 0;
        GazeEvaluationTrigger trigger = ConsumePendingTrigger();
        int coalescedNudges = Interlocked.Exchange(ref _coalescedNudges, 0);

        Mind mind = _mind
            ?? throw new InvalidOperationException("Attention gaze selector has no Mind parent after activation.");
        ISceneContext scene = mind.GetCurrentSceneContext();
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
            return null;
        }

        ICharacter owner = mind.OwningCharacter;
        IReadOnlyList<GazeCueCandidate> candidates = [.. composedCues.Select(c => c with
        {
            CueDescription = DescribeCueSynchronously(c, scene, owner),
        })];
        _evaluationInFlight = true;
        return DecisionGazePolicy.PrepareGroup(candidates, CurrentOwnAction, trigger, result =>
        {
            _evaluationInFlight = false;
            ApplyCompleted(new(trigger, coalescedNudges, result, DateTimeOffset.Now));
        }, _evaluationCancellation!.Token);
    }

    private bool IsEvaluationDue() => _initialEvaluationPending || _evaluationRequested
        || _secondsSinceLastEvaluation >= EvaluationIntervalSeconds + _failureBackoffSeconds;

    private GazeEvaluationTrigger ConsumePendingTrigger()
    {
        lock (_triggerLock)
        {
            _evaluationRequested = false;
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

    private void ApplyCompleted(GazeCompletedEvaluation completion)
    {
        if (!_isReady || _vision is null)
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
                _failureBackoffSeconds = DecisionFailureBackoff.NextSeconds(_failureBackoffSeconds, EvaluationIntervalSeconds);
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
        AppliedDwellSeconds = _clock.Elapsed.TotalSeconds;
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
        if (_mind!.GetCurrentSceneContext().Find(subjectFullId) is not IVisualSubject subject)
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
        string? payload = _dumpRecorder?.Record(new DecisionPayloadDump(
            completion.SettledAt,
            _character!.FullId,
            completion.Trigger.Token,
            verdict,
            completion.Result.Confidence,
            completion.Result.CompositionMilliseconds,
            completion.Result.DecisionMilliseconds,
            completion.Result.EvidenceCharacters,
            completion.CoalescedNudges,
            failure,
            completion.Result.Evidence,
            completion.Result.Questions));
        if (failure is null)
        {
            _transcript?.RecordSceneDecision(DecisionGazePolicy.QuestionName, verdict, payload);
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
        GazeEvaluationTrigger Trigger,
        int CoalescedNudges,
        GazeEvaluationResult Result,
        DateTimeOffset SettledAt);
}
