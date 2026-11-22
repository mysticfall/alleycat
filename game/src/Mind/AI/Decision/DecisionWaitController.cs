using AlleyCat.Character;
using AlleyCat.Core.Threading;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Attention;
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

    private const int RecentEventCount = 6;
    private const int RecentEventCharacterBudget = 2000;
    private const int MaximumSceneCharacters = 8;

    /// <summary>
    /// Interval between scene-membership checks while waiting: short enough that an entrant or departure reaches
    /// the gate well within a conversational beat, long enough that the snapshot costs nothing per frame.
    /// </summary>
    private const double MembershipCheckIntervalSeconds = 0.25d;

    /// <summary>The decision-payload dump category this gate files under: one directory per producer.</summary>
    private const string DumpCategory = "wait";

    private readonly MindBase _mind;
    private readonly DecisionWaitPolicy _policy;
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
    private Task<string?>? _premiseResolution;
    private double _failureBackoffSeconds;
    private int _nudgesSinceLastEvaluation;

    public DecisionWaitController(
        MindBase mind,
        IDecisionClient client,
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
        _policy = new DecisionWaitPolicy(client);
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
    /// <returns>Why the wait ended.</returns>
    public async Task<MindBase.ObservationWaitWake> RunAsync(
        double? explicitSeconds,
        CancellationToken cancellationToken)
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
            membershipWatch = WatchMembershipAsync(_lastMembership!, membershipWatchCancellation.Token);
            StartScenePremiseResolution();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DecisionWaitTrigger? trigger = await WaitForNextTriggerAsync(schedule, cancellationToken)
                    .ConfigureAwait(false);
                if (trigger is null)
                {
                    return DeadlineWake(schedule);
                }

                (DecisionWaitEvaluationInput Input, DecisionWaitEvaluationResult Result)? evaluation =
                    await EvaluateBeforeDeadlineAsync(trigger, schedule, cancellationToken).ConfigureAwait(false);
                if (evaluation is not { } completed)
                {
                    return DeadlineWake(schedule);
                }

                int coalescedNudges = Interlocked.Exchange(ref _nudgesSinceLastEvaluation, 0);
                DecisionWaitScheduleStep step = schedule.Apply(completed.Result.Decision, _nowSeconds());
                RecordDump(completed.Input, completed.Result, coalescedNudges, step);
                switch (step.Kind)
                {
                    case DecisionWaitScheduleStepKind.ActNow:
                        return MindBase.ObservationWaitWake.DecisionAct;
                    case DecisionWaitScheduleStepKind.Armed:
                    case DecisionWaitScheduleStepKind.Kept:
                        _failureBackoffSeconds = 0d;
                        break;
                    case DecisionWaitScheduleStepKind.Failure:
                        _failureBackoffSeconds = GazeFailureBackoff.NextSeconds(
                            _failureBackoffSeconds,
                            _cadenceIntervalSeconds);
                        LogDecisionFailure(completed.Result.Decision.Failure, _failureBackoffSeconds);
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
    private async Task<(DecisionWaitEvaluationInput Input, DecisionWaitEvaluationResult Result)?>
        EvaluateBeforeDeadlineAsync(
            DecisionWaitTrigger trigger,
            DecisionWaitSchedule schedule,
            CancellationToken cancellationToken)
    {
        using var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<(DecisionWaitEvaluationInput, DecisionWaitEvaluationResult)> evaluation =
            EvaluateAsync(trigger, evaluationCancellation.Token);
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

    private async Task<(DecisionWaitEvaluationInput, DecisionWaitEvaluationResult)> EvaluateAsync(
        DecisionWaitTrigger trigger,
        CancellationToken cancellationToken)
    {
        // The first evaluation waits for the once-per-wait premise resolution to settle — resolved text, cleanly
        // absent, or contained failure (the gaze cold-start gate pattern); later evaluations observe the cached
        // completed task.
        string? premise = _premiseResolution is { } resolution
            ? await resolution.WaitAsync(cancellationToken).ConfigureAwait(false)
            : null;
        DecisionWaitEvaluationInput input = await ComposeInputAsync(trigger, premise, cancellationToken)
            .ConfigureAwait(false);
        DecisionWaitEvaluationResult result = await _policy
            .EvaluateAsync(input, cancellationToken)
            .ConfigureAwait(false);
        return (input, result);
    }

    private MindBase.ObservationWaitWake DeadlineWake(DecisionWaitSchedule schedule)
    {
        MindBase.ObservationWaitWake wake = schedule.Source == DecisionWaitDeadlineSource.Explicit
            ? MindBase.ObservationWaitWake.TimerElapsed
            : MindBase.ObservationWaitWake.PauseElapsed;
        _logger?.LogDebug("Wait deadline expired ({DeadlineSource}); starting the agent.", schedule.Source);
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

    /// <summary>
    /// Resolves the authored scenario premise once per wait through the parent AgenticMind's scenario manager —
    /// the gaze selector's pattern: inputs gathered on the Godot thread, only the manager call asynchronous, and
    /// every failure contained as an absent premise.
    /// </summary>
    private void StartScenePremiseResolution()
    {
        if (_premiseResolution is not null)
        {
            return;
        }

        try
        {
            if (_mind is not AgenticMind { ScenarioManager: { } manager } || _mind.OwningCharacter is not { } character)
            {
                _premiseResolution = Task.FromResult<string?>(null);
                return;
            }

            Dictionary<string, object?> coreContext = AgenticMind.CreateCoreRenderContext(
                character,
                _mind.GetCurrentSceneContext(),
                _mind.GetContextEligibleAttentionIDs());
            _premiseResolution = ResolveScenePremiseAsync(manager, coreContext);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPremiseResolutionFailure(exception);
            _premiseResolution = Task.FromResult<string?>(null);
        }
    }

    private async Task<string?> ResolveScenePremiseAsync(
        ScenarioManager manager,
        IReadOnlyDictionary<string, object?> coreContext)
    {
        try
        {
            Scenario? scenario = await manager.GetCurrentScenario(coreContext).ConfigureAwait(false);
            string? description = scenario?.Description.Trim();
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPremiseResolutionFailure(exception);
            return null;
        }
    }

    /// <summary>
    /// Composes one evaluation's input on the Godot thread: membership-change detection (overriding the nominal
    /// trigger when the roster changed), the lore roster, recent events, the own-action speech state, and the
    /// gaze selector's applied cue with its dwell.
    /// </summary>
    private async Task<DecisionWaitEvaluationInput> ComposeInputAsync(
        DecisionWaitTrigger trigger,
        string? premise,
        CancellationToken cancellationToken)
    {
        DecisionWaitEvaluationInput composed = null!;
        await _dispatcher.InvokeAsync(
            () => composed = ComposeInputOnMainThread(trigger, premise),
            cancellationToken).ConfigureAwait(false);
        return composed;
    }

    private DecisionWaitEvaluationInput ComposeInputOnMainThread(DecisionWaitTrigger trigger, string? premise)
    {
        ISceneContext scene = _mind.GetCurrentSceneContext();
        HashSet<string> membership = [.. scene.Characters.Select(static character => character.FullId)];
        bool membershipChanged = _lastMembership is null || !membership.SetEquals(_lastMembership);
        _lastMembership = membership;

        DecisionWaitTrigger effectiveTrigger = membershipChanged
            ? new DecisionWaitTrigger(DecisionWaitTriggerKind.MembershipChange)
            : trigger;
        return new DecisionWaitEvaluationInput(
            _mind.OwningCharacter.FullId,
            ComposeSceneCharacters(scene),
            RenderRecentEvents(),
            ComposeOwnAction(),
            ResolveCurrentGaze(out double dwellSeconds),
            dwellSeconds,
            effectiveTrigger,
            premise);
    }

    private IReadOnlyList<DecisionWaitSceneCharacter> ComposeSceneCharacters(ISceneContext scene)
    {
        ICharacter owner = _mind.OwningCharacter;
        ILoreQueryService? queryService = ResolveLoreQueryService();
        List<DecisionWaitSceneCharacter> roster = [];
        foreach (ICharacter other in scene.Characters
                     .Where(static candidate => candidate is not null)
                     .OrderBy(static candidate => candidate.FullId, StringComparer.Ordinal))
        {
            if (ReferenceEquals(other, owner) || roster.Count >= MaximumSceneCharacters)
            {
                continue;
            }

            string? description = queryService is null
                ? null
                : CharacterLoreDescriptions.ResolveSynchronously(
                    queryService,
                    owner.FullId,
                    scene.Content,
                    other.FullId,
                    _logger);
            roster.Add(new DecisionWaitSceneCharacter(other.FullId, description));
        }

        return roster;
    }

    private static ILoreQueryService? ResolveLoreQueryService()
    {
        try
        {
            return Game.Instance.GetRequiredService<ILoreQueryService>();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private IReadOnlyList<string> RenderRecentEvents()
    {
        IReadOnlyList<AgentObservation> timeline = _mind.GetObservationTimelineSnapshot();
        if (timeline.Count == 0)
        {
            return [];
        }

        List<AgentObservation> window = [.. timeline.Skip(Math.Max(0, timeline.Count - RecentEventCount))];
        // The renderer is a deterministic CPU-only canonical-text projection: every overload completes
        // synchronously, so the value is available without yielding off the Godot thread. Event times render
        // relative to this evaluation's composition moment.
        string rendered = new ObservationHistoryRenderer(_mind.OwningCharacter)
            .RenderAsync(window, timeline, _mind.GameClock.NowSeconds)
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

    private DecisionWaitOwnAction ComposeOwnAction()
        => _mind.TryGetAttendedSpeaker(out string? speakerFullId)
            ? new DecisionWaitOwnAction(
                DecisionWaitOwnActionKind.Hearing,
                speakerFullId,
                ResolveAddresseeStatus(speakerFullId))
            : DecisionWaitOwnAction.WaitingAction;

    /// <summary>
    /// Determines how the attended speaker's latest observed speech is addressed: declared to this character, to
    /// another, or undecided when no recipients were declared or no speech is retained.
    /// </summary>
    private DecisionWaitAddresseeStatus ResolveAddresseeStatus(string speakerFullId)
    {
        string ownerFullId = _mind.OwningCharacter.FullId;
        foreach (AgentObservation observation in _mind.GetObservationTimelineSnapshot().Reverse())
        {
            if (observation is ObservedSpeech { ActorId: { } actor } speech
                && string.Equals(actor, speakerFullId, StringComparison.Ordinal))
            {
                return speech.Addressees.Contains(ownerFullId, StringComparer.Ordinal)
                    ? DecisionWaitAddresseeStatus.AddressedToMe
                    : speech.Addressees.Count > 0
                        ? DecisionWaitAddresseeStatus.AddressedToAnother
                        : DecisionWaitAddresseeStatus.Undecided;
            }
        }

        return DecisionWaitAddresseeStatus.Undecided;
    }

    /// <summary>Reads the gaze selector's applied cue value and dwell; no selector or clear gaze yields null.</summary>
    private string? ResolveCurrentGaze(out double dwellSeconds)
    {
        dwellSeconds = 0d;
        foreach (Node child in _mind.GetChildren())
        {
            if (child is AttentionGazeTargetSelector { HasAppliedTarget: true } selector)
            {
                dwellSeconds = selector.AppliedDwellSeconds;
                return selector.AppliedCueValue;
            }
        }

        return null;
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

    private void RecordDump(
        DecisionWaitEvaluationInput input,
        DecisionWaitEvaluationResult result,
        int coalescedNudges,
        DecisionWaitScheduleStep step)
    {
        DecisionPayloadDumpRecorder? recorder = _dumpRecorder;
        if (recorder is null)
        {
            return;
        }

        recorder.Record(new DecisionPayloadDump(
            DateTimeOffset.Now,
            input.OwnerFullId,
            input.Trigger.Token,
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

    private void LogPremiseResolutionFailure(Exception exception)
        => _logger?.LogWarning(
            exception,
            "Resolving the scenario premise for the wait decision failed; questions compose without one.");
}
