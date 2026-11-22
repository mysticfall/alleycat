using AlleyCat.Core.Threading;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Decision;

/// <summary>
/// One active wait's two-question decision gate: while the character waits, consults the decision model on a
/// cadence and on speech/scene event nudges — player speech delivered, attended-speaker start and end,
/// scene-membership change — with one in-flight evaluation and latest-wins coalescing. Code owns the timing: an
/// immediate verdict starts the agent at once, a deferred verdict arms its configured, jittered deadline under the
/// shorten-only rules of <see cref="DecisionWaitSchedule" />, and an armed deadline starts the agent when it expires.
/// Starting the agent does exactly one thing: it completes the wait, so the <c>wait</c> tool (or the session park)
/// returns its payload-free result and the runner starts the next ordinary agent run.
/// </summary>
/// <remarks>
/// <para>
/// Every failure keeps waiting with a 2×-doubling evaluation backoff capped at 30 seconds and, for timing, arms
/// the no-reason timeout under the shorten-only rules, so a parked wait never stalls on a failing decision
/// service; armed deadlines fire even while an evaluation is in flight. Failures never throw into the wait or the
/// runner; only cancellation does. Event
/// subscriptions are taken for the controller's lifetime only and released on completion, so the poll never
/// touches another character's state.
/// </para>
/// <para>
/// Godot-thread discipline: input composition — scene membership, the gaze selector's applied cue, the attended
/// speaker — runs under the shared main-thread dispatcher (inline when already there), mirroring the auto-mode
/// visual-context composition; the premise resolution follows the gaze selector's once-per-lifetime pattern with
/// the first evaluation gated on its settlement.
/// </para>
/// </remarks>
internal sealed class DecisionWaitController
{

    /// <summary>
    /// Interval between scene-membership checks while waiting: short enough that an entrant or departure reaches
    /// the gate well within a conversational beat, long enough that the snapshot costs nothing per frame.
    /// </summary>
    private const double MembershipCheckIntervalSeconds = 0.25d;

    /// <summary>The decision-payload dump category this gate files under: one directory per producer.</summary>
    private const string DumpCategory = "wait";

    private readonly MindBase _mind;
    private SubjectSceneDecisions? _subject;
    private SceneDecisionGroup? _pendingGroup;
    private readonly double _cadenceIntervalSeconds;
    private readonly DecisionWaitTimingSettings _timing;
    private readonly Func<double> _jitterSampler;
    private readonly Func<double> _nowSeconds;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly ILogger? _logger;
    private readonly DecisionPayloadDumpRecorder? _dumpRecorder;
    private readonly Lock _nudgeLock = new();

    private TaskCompletionSource _nudgeSignal = CreateNudgeSignal();
    private DecisionWaitTrigger? _pendingNudgeTrigger;
    private HashSet<string>? _lastMembership;
    private string? _playerFullId;
    private SceneDecisionGroup? TakeGroup() => Interlocked.Exchange(ref _pendingGroup, null);
    private double _failureBackoffSeconds;
    private string _deadlineExplanation = "the explicit wait deadline elapsed";
    private string? _deadlinePayload;
    private MindSessionTranscriptRecorder? _transcript;
    private int _nudgesSinceLastEvaluation;

    public DecisionWaitController(
        MindBase mind,
        double cadenceIntervalSeconds,
        DecisionWaitTimingSettings timing,
        Func<double> jitterSampler,
        Func<double> nowSeconds,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mind);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(jitterSampler);
        ArgumentNullException.ThrowIfNull(nowSeconds);
        if (!double.IsFinite(cadenceIntervalSeconds) || cadenceIntervalSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cadenceIntervalSeconds),
                cadenceIntervalSeconds,
                "Wait evaluation cadence must be finite and positive.");
        }

        _mind = mind;
        _cadenceIntervalSeconds = cadenceIntervalSeconds;
        _timing = timing;
        _jitterSampler = jitterSampler;
        _nowSeconds = nowSeconds;
        _dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        _logger = logger;
        _dumpRecorder = CreateDumpRecorderIfEnabled();
    }

    /// <summary>
    /// Runs the gate until an immediate verdict or an armed deadline starts the agent, or cancellation abandons
    /// the wait; cancellation throws.
    /// </summary>
    /// <param name="explicitSeconds">The agent's explicit <c>wait(seconds)</c> request, seeding the initial
    /// deadline; null starts with no deadline until a verdict arms one.</param>
    /// <param name="cancellationToken">Cancellation that abandons the wait.</param>
    /// <param name="onGateEntered">Marks an explicit wait only after its registered gate is ready.</param>
    /// <returns>Why the wait ended.</returns>
    public async Task<MindBase.ObservationWaitWake> RunAsync(
        double? explicitSeconds,
        CancellationToken cancellationToken,
        Action? onGateEntered)
    {
        DecisionWaitSchedule schedule = new(_timing, _jitterSampler);
        if (explicitSeconds is { } seconds)
        {
            schedule.SeedExplicit(_nowSeconds(), seconds);
        }

        _mind.ObservationCommitted += OnObservationCommitted;
        _mind.SpeechSegmentLifecycleNotified += OnSpeechSegmentLifecycleNotified;
        _mind.AttendedSpeakerEndedNotified += OnAttendedSpeakerEndedNotified;
        using var membershipWatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task membershipWatch = Task.CompletedTask;
        try
        {
            await CaptureStartupStateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            onGateEntered?.Invoke();
            membershipWatch = WatchMembershipAsync(_lastMembership!, membershipWatchCancellation.Token);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DecisionWaitTrigger? trigger = await WaitForNextTriggerAsync(schedule, cancellationToken)
                    .ConfigureAwait(false);
                if (trigger is null)
                {
                    return DeadlineWake(schedule);
                }

                DecisionWaitEvaluationResult? evaluation =
                    await EvaluateBeforeDeadlineAsync(schedule, cancellationToken).ConfigureAwait(false);
                if (evaluation is not { } completed || schedule.IsDue(_nowSeconds()))
                {
                    return DeadlineWake(schedule);
                }

                int coalescedNudges = Interlocked.Exchange(ref _nudgesSinceLastEvaluation, 0);
                double? previous = schedule.DeadlineAt;
                DecisionWaitScheduleStep step = schedule.Apply(completed.Decision, _nowSeconds());
                string? payload = RecordDump(trigger, completed, coalescedNudges, step);
                if (_transcript is { } transcript)
                {
                    foreach (DecisionPayloadAnswer answer in completed.Answers)
                    {
                        transcript.RecordSceneDecision(answer.Name, answer.Choice, payload);
                    }

                    if (schedule.DeadlineAt != previous)
                    {
                        string cause = step.Kind == DecisionWaitScheduleStepKind.Failure
                            ? "evaluation failure; no-reason fallback" : DescribeArm(step.Arm!.Value);
                        _deadlineExplanation = $"the {(previous is null ? "armed" : "shortened")} deadline elapsed ({cause})";
                        _deadlinePayload = payload;
                    }
                }
                switch (step.Kind)
                {
                    case DecisionWaitScheduleStepKind.ActNow:
                        if (_transcript is { } wakeTranscript)
                        {
                            bool arrived = completed.Answers.Any(a => a.Choice == DecisionWaitPolicy.ExpectingArrivedValue);
                            string reason = arrived ? "a response arrived" : "the situation requires action";
                            wakeTranscript.RecordSceneDecision(null, $"immediate decision: {reason}"
                                + (previous.HasValue ? "; pre-empted the armed deadline" : string.Empty), payload);
                        }
                        return MindBase.ObservationWaitWake.DecisionAct;
                    case DecisionWaitScheduleStepKind.Armed:
                    case DecisionWaitScheduleStepKind.Kept:
                        _failureBackoffSeconds = 0d;
                        break;
                    case DecisionWaitScheduleStepKind.Failure:
                        _failureBackoffSeconds = DecisionFailureBackoff.NextSeconds(
                            _failureBackoffSeconds,
                            _cadenceIntervalSeconds);
                        LogDecisionFailure(completed.Decision.Failure, _failureBackoffSeconds);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown wait schedule step '{step.Kind}'.");
                }
            }
        }
        finally
        {
            _mind.ObservationCommitted -= OnObservationCommitted;
            _mind.SpeechSegmentLifecycleNotified -= OnSpeechSegmentLifecycleNotified;
            _mind.AttendedSpeakerEndedNotified -= OnAttendedSpeakerEndedNotified;
            // The membership watch lives exactly as long as this wait, on every exit path.
            membershipWatchCancellation.Cancel();
            await membershipWatch.ConfigureAwait(false);
            if (_subject is { } subject)
            {
                await _dispatcher.InvokeAsync(() => subject.Unregister(TakeGroup), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Watches scene membership for the wait's lifetime (TQ-B4): every <see cref="MembershipCheckIntervalSeconds" />
    /// it captures the roster through the mind's scene-context seam on the Godot thread — the same snapshot the
    /// evaluation composes from, polled like the gaze selector's membership check — and an entrant or departure
    /// signals the coalescing <see cref="DecisionWaitTriggerKind.MembershipChange" /> nudge, which brings the next
    /// evaluation forward past the cadence and any failure backoff. An unchanged roster never signals, so the
    /// watch cannot spin the gate. Never throws: cancellation ends it quietly and any capture failure is contained.
    /// </summary>
    private async Task WatchMembershipAsync(HashSet<string> baseline, CancellationToken cancellationToken)
    {
        HashSet<string> lastSeen = baseline;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(MembershipCheckIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
                HashSet<string> current = lastSeen;
                await _dispatcher.InvokeAsync(
                    () => current = CaptureMembershipOnMainThread(),
                    cancellationToken).ConfigureAwait(false);
                if (!current.SetEquals(lastSeen))
                {
                    lastSeen = current;
                    SignalNudge(DecisionWaitTriggerKind.MembershipChange);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The wait ended; the watch ends with it.
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Watching scene membership for the wait decision failed; membership changes no longer nudge this "
                    + "wait, which keeps its cadence, nudges and deadlines.");
        }
    }

    private HashSet<string> CaptureMembershipOnMainThread()
        => [.. _mind.GetCurrentSceneContext().Characters.Select(static character => character.FullId)];

    /// <summary>
    /// Runs one evaluation — premise settlement, input composition, and the decision round trip — racing the
    /// armed deadline: when the deadline expires first, the in-flight evaluation is cancelled and null returned
    /// so the deadline starts the agent on time.
    /// </summary>
    private async Task<DecisionWaitEvaluationResult?>
        EvaluateBeforeDeadlineAsync(
            DecisionWaitSchedule schedule,
            CancellationToken cancellationToken)
    {
        using var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<DecisionWaitEvaluationResult> evaluation = EvaluateAsync(evaluationCancellation.Token);
        if (schedule.RemainingSeconds(_nowSeconds()) is not { } remaining)
        {
            return await evaluation.ConfigureAwait(false);
        }

        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = Task.Delay(TimeSpan.FromSeconds(remaining), deadlineCancellation.Token);
        Task first = await Task.WhenAny(evaluation, deadline).ConfigureAwait(false);
        if (ReferenceEquals(first, evaluation))
        {
            deadlineCancellation.Cancel();
            return await evaluation.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        evaluationCancellation.Cancel();
        try
        {
            _ = await evaluation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The deadline won the race; the abandoned evaluation's cancellation is expected.
        }

        return null;
    }

    private Task<DecisionWaitEvaluationResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<DecisionWaitEvaluationResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Interlocked.Exchange(ref _pendingGroup, DecisionWaitPolicy.PrepareGroup(result => completion.TrySetResult(result), cancellationToken));
        return completion.Task.WaitAsync(cancellationToken);
    }

    private MindBase.ObservationWaitWake DeadlineWake(DecisionWaitSchedule schedule)
    {
        MindBase.ObservationWaitWake wake = schedule.Source == DecisionWaitDeadlineSource.Explicit
            ? MindBase.ObservationWaitWake.TimerElapsed
            : MindBase.ObservationWaitWake.PauseElapsed;
        _logger?.LogDebug("Wait deadline expired ({DeadlineSource}); starting the agent.", schedule.Source);
        _transcript?.RecordSceneDecision(null, _deadlineExplanation, _deadlinePayload);
        return wake;
    }

    private void OnObservationCommitted(AgentObservation observation)
    {
        // Player speech delivered always nudges (wait experiment §4); the modality filter lives here, never in
        // Mind's modality-free ingestion.
        if (observation is ObservedSpeech { ActorId: { } actor }
            && _playerFullId is { } player
            && string.Equals(actor, player, StringComparison.Ordinal))
        {
            SignalNudge(DecisionWaitTriggerKind.PlayerSpeech);
        }
    }

    private void OnSpeechSegmentLifecycleNotified(SpeechSegmentLifecycleNotification notification)
    {
        if (notification.Transition is SpeechSegmentLifecycleTransition.Started
            or SpeechSegmentLifecycleTransition.Resumed)
        {
            SignalNudge(DecisionWaitTriggerKind.AttendedSpeakerStart);
        }
    }

    private void OnAttendedSpeakerEndedNotified(string speakerFullId)
        => SignalNudge(DecisionWaitTriggerKind.AttendedSpeakerEnd);

    private void SignalNudge(DecisionWaitTriggerKind kind)
    {
        TaskCompletionSource signal;
        lock (_nudgeLock)
        {
            _pendingNudgeTrigger = new DecisionWaitTrigger(kind);
            signal = _nudgeSignal;
        }

        _ = Interlocked.Increment(ref _nudgesSinceLastEvaluation);
        _ = signal.TrySetResult();
    }

    /// <summary>
    /// Waits for the next evaluation trigger — a nudge (latest-wins, bypassing the failure backoff) or the cadence
    /// boundary — or for the armed deadline, whichever comes first; null means the deadline is due. Coalescing is
    /// inherent — nudges arriving during an in-flight evaluation only replace the pending trigger for the next
    /// iteration.
    /// </summary>
    private async Task<DecisionWaitTrigger?> WaitForNextTriggerAsync(
        DecisionWaitSchedule schedule,
        CancellationToken cancellationToken)
    {
        double? remaining = schedule.RemainingSeconds(_nowSeconds());
        if (remaining is <= 0d)
        {
            return null;
        }

        TaskCompletionSource signal;
        lock (_nudgeLock)
        {
            signal = _nudgeSignal;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cadence = Task.Delay(
            TimeSpan.FromSeconds(_cadenceIntervalSeconds + _failureBackoffSeconds),
            delayCancellation.Token);
        Task? deadline = remaining is { } seconds
            ? Task.Delay(TimeSpan.FromSeconds(seconds), delayCancellation.Token)
            : null;
        Task completed = deadline is null
            ? await Task.WhenAny(signal.Task, cadence).ConfigureAwait(false)
            : await Task.WhenAny(signal.Task, cadence, deadline).ConfigureAwait(false);
        delayCancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(completed, deadline) || schedule.IsDue(_nowSeconds()))
        {
            return null;
        }

        if (ReferenceEquals(completed, signal.Task))
        {
            lock (_nudgeLock)
            {
                DecisionWaitTrigger trigger = _pendingNudgeTrigger ?? DecisionWaitTrigger.Cadence;
                _pendingNudgeTrigger = null;
                _nudgeSignal = CreateNudgeSignal();
                return trigger;
            }
        }

        return DecisionWaitTrigger.Cadence;
    }

    /// <summary>
    /// Captures the startup state on the Godot thread: the scene-membership baseline and the player identity for
    /// the speech nudge filter.
    /// </summary>
    private async Task CaptureStartupStateAsync(CancellationToken cancellationToken)
    {
        (HashSet<string> membership, string? playerFullId) startup = default;
        await _dispatcher.InvokeAsync(
            () => startup = CaptureStartupStateOnMainThread(),
            cancellationToken).ConfigureAwait(false);
        _lastMembership = startup.membership;
        _playerFullId = startup.playerFullId;
    }

    private (HashSet<string> Membership, string? PlayerFullId) CaptureStartupStateOnMainThread()
    {
        _transcript = _mind.DecisionTranscript;
        _subject = _mind.GetSceneDecisions();
        _subject.Register(TakeGroup);
        ISceneContext scene = _mind.GetCurrentSceneContext();
        HashSet<string> membership = [.. scene.Characters.Select(static character => character.FullId)];
        string? player;
        try
        {
            player = scene.Player.FullId;
        }
        catch (InvalidOperationException)
        {
            // Player-less contexts are valid in narrow fixtures: no player-speech nudges, honestly.
            player = null;
        }

        return (membership, player);
    }

    private DecisionPayloadDumpRecorder? CreateDumpRecorderIfEnabled()
    {
        try
        {
            return !_mind.WaitDiagnosticsSettings.EnableDecisionPayloadLogging
                ? null
                : new DecisionPayloadDumpRecorder(
                    DumpCategory,
                    _mind.OwningCharacter.FullId,
                    _mind.OwningCharacter.Id,
                    static () => ProjectSettings.GlobalizePath(MindSessionTranscriptRecorder.UserRootPath),
                    _logger ?? NullLogger.Instance);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private string? RecordDump(
        DecisionWaitTrigger trigger,
        DecisionWaitEvaluationResult result,
        int coalescedNudges,
        DecisionWaitScheduleStep step)
    {
        return _dumpRecorder?.Record(new DecisionPayloadDump(
            DateTimeOffset.Now,
            _mind.OwningCharacter.FullId,
            trigger.Token,
            step.Token,
            null,
            result.CompositionMilliseconds,
            result.DecisionMilliseconds,
            result.EvidenceCharacters,
            coalescedNudges,
            result.Decision.Failure,
            result.Evidence,
            result.Questions,
            result.Answers,
            new DecisionPayloadWaitResolution(
                DescribeStep(step.Kind),
                step.Arm is { } arm ? DescribeArm(arm) : null,
                RoundSeconds(step.RequestedSeconds),
                RoundSeconds(step.RemainingSeconds),
                step.Source switch
                {
                    DecisionWaitDeadlineSource.Explicit => "explicit",
                    DecisionWaitDeadlineSource.Decision => "decision",
                    _ => null,
                })));
    }

    private static string DescribeStep(DecisionWaitScheduleStepKind kind) => kind switch
    {
        DecisionWaitScheduleStepKind.ActNow => "now",
        DecisionWaitScheduleStepKind.Armed => "armed",
        DecisionWaitScheduleStepKind.Kept => "kept",
        DecisionWaitScheduleStepKind.Failure => "failed",
        _ => "failed",
    };

    private static string DescribeArm(DecisionWaitArm arm) => arm switch
    {
        DecisionWaitArm.ResponsePending => "response_pending",
        DecisionWaitArm.MayAct => "may_act",
        DecisionWaitArm.NoReason => "no_reason",
        _ => "no_reason",
    };

    private static double? RoundSeconds(double? seconds)
        => seconds is { } value ? Math.Round(value, 2) : null;

    private static TaskCompletionSource CreateNudgeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void LogDecisionFailure(string? failure, double backoffSeconds)
        => _logger?.LogWarning(
            "Wait decision failed ({Failure}); keeping the wait and backing off. Backoff: {BackoffSeconds:F1}s.",
            failure ?? "unknown failure",
            backoffSeconds);

}
