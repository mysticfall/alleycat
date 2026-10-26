using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Executes one long-running agent session as a replayed transcript driven through bounded, stateless provider
/// requests (AI-002 TR-1/2).
/// </summary>
/// <remarks>
/// <para>
/// This loop is deliberately custom rather than hosted by <c>Microsoft.Agents.AI</c>'s
/// <c>ChatClientAgent</c>/<c>AIAgent.RunAsync</c> abstractions. The framework's run loop applies one cancellation
/// token to the whole run, so it cannot cancel only an in-flight generation while a tool in flight continues
/// normally with its cut-short result; it does not provide the strict whole-batch tool-only validation before any
/// execution (AI-002 TR-12); and its history and function-invocation machinery would append protocol entries beyond
/// assistant tool calls and tool results, which the transient transcript contract keeps out (AI-002 TR-1/2) — a
/// contract that now retains every accepted exchange for the whole session instead of disposing settled batches
/// (AI-002 TR-17). Where the framework fits without deviation — the stateless transport underneath — it is used
/// unchanged through <see cref="IChatClient"/>.
/// </para>
/// <para>
/// Thread safety: <see cref="InvalidateForFreshTurn()"/> and its overloads may be called from any thread; the
/// accepted transcript itself is only touched by <see cref="RunAsync"/>.
/// </para>
/// <para>
/// Transcript recording (AI-011 TR-5–TR-7) is an optional injected sink observing the request-cycle loop from
/// outside: the runner stays character-agnostic and filesystem-free, the sink receives one capture per resolved
/// cycle, and recording never changes validation, acceptance, retention, delivered-watermark, scheduling, or
/// cancellation semantics.
/// </para>
/// </remarks>
internal sealed class AgentSessionRunner
{
    /// <summary>
    /// Canonical cancellation result for every tool call that produced no natural result, keeping a retained
    /// in-flight exchange protocol-valid (AI-002 TR-19). Internal so transcript tests assert the runner-generated value
    /// rather than a hand-supplied string (AI-011 TR-6).
    /// </summary>
    internal const string CancelledActionResult = "The action was cancelled before it completed.";

    /// <summary>Root path label of a call's arguments object inside schema-mismatch descriptions (AI-011 TR-21).</summary>
    internal const string ArgumentsPathRoot = "arguments";

    private static readonly TimeSpan[] _defaultRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<ChatMessage> _runInputMessages;
    private readonly IReadOnlyDictionary<string, AIFunction> _functions;
    private readonly ChatOptions _chatOptions;
    private readonly ILogger _logger;
    private readonly bool _enableReasoningLogging;
    private readonly TimeSpan[] _retryDelays;
    private readonly int _maxTransportRetries;
    private readonly IInvalidResponseRecoveryPolicy _invalidResponseRecoveryPolicy;
    private readonly IAgentRequestContextSource _requestContextSource;
    private readonly SessionScopedTranscriptSink _transcriptSink;
    private readonly string _instructions;
    private readonly MindTranscriptRequestOptions _transcriptRequestOptions;
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private readonly Lock _stateLock = new();
    private readonly List<ChatMessage> _acceptedTranscript = [];
    private readonly List<int> _runtimeAuthoredMessageIndexes = [];
    private long _invalidationEpoch;
    private ActivePhaseState? _activePhase;
    private int _freshInvalidation;
    private volatile bool _ended;

    public AgentSessionRunner(
        IChatClient chatClient,
        string instructions,
        IReadOnlyList<ChatMessage> runInputMessages,
        IList<AITool> productionTools,
        ILogger logger,
        bool enableReasoningLogging = true,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        IInvalidResponseRecoveryPolicy? invalidResponseRecoveryPolicy = null,
        IAgentRequestContextSource? requestContextSource = null,
        IAgentSessionTranscriptSink? transcriptSink = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _runInputMessages = runInputMessages ?? throw new ArgumentNullException(nameof(runInputMessages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enableReasoningLogging = enableReasoningLogging;
        _retryDelays = [.. retryDelays ?? _defaultRetryDelays];
        _maxTransportRetries = _retryDelays.Length;
        _invalidResponseRecoveryPolicy = invalidResponseRecoveryPolicy ?? new InvalidResponseRecoveryPolicy(
            InvalidResponseRecoveryPolicy.DefaultConsecutiveFailureBudget,
            InvalidResponseRecoveryPolicy.DefaultBackoffDelays);
        _requestContextSource = requestContextSource ?? EmptyAgentRequestContextSource.Instance;
        // Recording is scoped to this runner's lifetime: the gate closes in RunAsync's finally, so an abandoned
        // continuation can never reach the recorder after the session loop has exited (AI-011 TR-6/TR-7).
        _transcriptSink = new SessionScopedTranscriptSink(transcriptSink ?? NullAgentSessionTranscriptSink.Instance);
        _functions = ResolveFunctions(productionTools);
        _instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        _chatOptions = new ChatOptions
        {
            Instructions = _instructions,
            Tools = [.. _functions.Values],
            ToolMode = ChatToolMode.RequireAny,
            // Responses may carry multiple tool calls, validated and executed serially as one batch (AI-002 TR-12).
            AllowMultipleToolCalls = true,
            ResponseFormat = null,
        };
        _transcriptRequestOptions = new MindTranscriptRequestOptions(
            _chatOptions.ModelId,
            _chatOptions.ToolMode?.GetType().Name ?? "Auto",
            [.. _functions.Values.Select(static function => function.Name)]);
    }

    /// <summary>
    /// Records a fresh-turn invalidation (AI-002 TR-4/5) that immediately supersedes every piece of work originating
    /// from the current stale response: the active phase — generation, invalid-response backoff, or the active tool
    /// call of a validated batch — is cancelled co-operatively at once, and the stale latch persists across
    /// generation, validation, backoff, and the complete remaining tool batch until the replacement request is
    /// issued, which rematerialises its context and confirms nothing from the stale work. A response returned by a
    /// non-cooperative provider after its generation was invalidated is discarded.
    /// </summary>
    public void InvalidateForFreshTurn()
        => InvalidateForFreshTurnCore(cancelActivePhase: true, freshSpeechKeys: null);

    /// <summary>
    /// Records a fresh-turn invalidation (AI-002 TR-4/5) whose cancellation of the active phase is optional: a
    /// wait-owned fresh signal passes <paramref name="cancelActivePhase"/> as false because the woken wait — the
    /// active phase and the sole delivery channel for its window — must complete naturally with its delivery
    /// (AI-002 TR-8/10), after which the stale latch persists across the wait's natural result, skips the batch's
    /// remaining calls with canonical cancellation results, and precedes exactly one replacement request. Every
    /// other caller cancels the active phase co-operatively at once.
    /// </summary>
    public void InvalidateForFreshTurn(bool cancelActivePhase)
        => InvalidateForFreshTurnCore(cancelActivePhase, freshSpeechKeys: null);

    /// <summary>
    /// Records a fresh-turn invalidation (AI-002 TR-4/5) whose active-phase cancellation is scoped by the fresh
    /// delivery's speech keys: an admitted arbitrated phase whose admission beat every supplied key is never
    /// cancelled by the matching player-speech lifecycle — it stays committed at playback hand-off (AI-002
    /// TR-14) — while an empty, absent, or only partially matching key set is unrelated freshness and cancels with
    /// ordinary pre-hand-off authority. Keying never changes the epoch, stale-batch latch, or request-context
    /// materialisation semantics.
    /// </summary>
    /// <param name="freshSpeechKeys">Continuation keys of every pending speech continuation the fresh event satisfies, or
    /// null/empty when the window carries no matching speech lifecycle.</param>
    internal void InvalidateForFreshTurn(
        IReadOnlySet<SpeechContinuationKey>? freshSpeechKeys)
        => InvalidateForFreshTurnCore(cancelActivePhase: true, freshSpeechKeys);

    private void InvalidateForFreshTurnCore(
        bool cancelActivePhase,
        IReadOnlySet<SpeechContinuationKey>? freshSpeechKeys)
    {
        if (_ended)
        {
            return;
        }

        bool cancel;
        lock (_stateLock)
        {
            if (_ended)
            {
                return;
            }

            AdvanceInvalidationLocked();
            cancel = cancelActivePhase && !IsProtectedByFreshSpeechKeysLocked(freshSpeechKeys);
        }

        if (cancel)
        {
            CancelActivePhase();
        }
    }

    /// <summary>
    /// Registers a continuation identity for composition-side correlation without adding model-facing input.
    /// Canonical request context supplies the committed speech.
    /// </summary>
    /// <remarks>
    /// A cue that linearises after an admission-arbitrated submission was admitted does not cancel that phase
    /// (AI-002 TR-14): its exact speech key is associated with the admitted phase so the protected submission
    /// settles naturally, while every other phase — including an arbitrated submission still awaiting admission —
    /// is cancelled co-operatively at once.
    /// </remarks>
    internal void RegisterSpeechContinuation(SpeechContinuationKey continuation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(continuation.VoiceID);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuation.SpeechGroupID);
        ArgumentOutOfRangeException.ThrowIfNegative(continuation.SegmentIndex);

        lock (_stateLock)
        {
            if (_ended)
            {
                return;
            }

            if (_activePhase is { Admission: ToolPhaseAdmission.Admitted } phase)
            {
                _ = phase.ProtectedBySpeechKeys.Add(continuation);
            }
        }

    }

    /// <summary>
    /// Arbitrates the admission transaction of an admission-arbitrated tool phase (AI-002 TR-14) atomically under
    /// the state lock: admission is refused — committing nothing — after fresh invalidation or when the session no
    /// longer owns a pending-arbitration phase; otherwise the caller's queue admission commits inside this critical
    /// section and the active phase is marked admitted, so later matching cues protect rather than cancel it.
    /// </summary>
    /// <param name="commit">Arbitrated pipeline's queue admission to run inside the lock; it must not throw.</param>
    /// <returns>Whether the submission was admitted.</returns>
    internal bool TryAdmitToolPhase(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (_stateLock)
        {
            if (_ended
                || Volatile.Read(ref _freshInvalidation) != 0
                || _activePhase is not { Admission: ToolPhaseAdmission.Pending } arbitratedPhase)
            {
                return false;
            }

            commit();
            arbitratedPhase.Admission = ToolPhaseAdmission.Admitted;
            return true;
        }
    }

    /// <summary>
    /// Cancels the currently registered phase — always the phase whose token discriminates self-inflicted
    /// interruption from provider timeouts — quietly when that phase already completed naturally.
    /// </summary>
    private void CancelActivePhase()
    {
        CancellationTokenSource? cancellation = ReadActivePhaseCancellation();
        if (cancellation is not null)
        {
            TryCancelQuietly(cancellation);
        }
    }

    /// <summary>
    /// Reads the active phase's cancellation source under the state lock so cancellation itself always happens
    /// outside it: the onset path takes only the runner state lock and cancels after releasing it.
    /// </summary>
    private CancellationTokenSource? ReadActivePhaseCancellation()
    {
        lock (_stateLock)
        {
            return _activePhase?.Cancellation;
        }
    }

    private static void TryCancelQuietly(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The phase completed naturally after the invalidation was recorded; the stale latch still supersedes
            // its response or skips its remaining batch calls before the replacement request.
        }
    }

    /// <summary>
    /// Registers one active phase atomically under the state lock: registration and cue linearisation become
    /// mutually exclusive, closing the write-then-observe race where a phase began after a pending speech hold or
    /// fresh invalidation was recorded but before its cancellation source was observable. A phase refuses to start
    /// while any pending expectation or stale latch blocks the session's next work.
    /// </summary>
    /// <returns>Whether the phase was registered; false means the caller must refuse to start it.</returns>
    private bool TryBeginPhase(ActivePhaseState phase, CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(phase);
        lock (_stateLock)
        {
            if (lifetimeToken.IsCancellationRequested)
            {
                return false;
            }

            if (_ended || Volatile.Read(ref _freshInvalidation) != 0)
            {
                return false;
            }

            _activePhase = phase;
            return true;
        }
    }

    /// <summary>Detaches a completed phase; only the phase that is still registered is detached.</summary>
    private void EndActivePhase(ActivePhaseState phase)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_activePhase, phase))
            {
                _activePhase = null;
            }
        }
    }

    /// <summary>
    /// Determines whether the active phase is an admitted arbitrated submission protected by every key of the fresh
    /// delivery: protection applies only to the matching player-speech lifecycle, so an absent, empty, or partially
    /// matching key set is unrelated freshness and keeps ordinary cancellation authority (AI-002 TR-14).
    /// </summary>
    /// <remarks>Must be called while holding <see cref="_stateLock"/>.</remarks>
    private bool IsProtectedByFreshSpeechKeysLocked(IReadOnlySet<SpeechContinuationKey>? freshSpeechKeys)
        => _activePhase is { Admission: ToolPhaseAdmission.Admitted, ProtectedBySpeechKeys.Count: > 0 } phase
            && freshSpeechKeys is { Count: > 0 }
            && freshSpeechKeys.All(phase.ProtectedBySpeechKeys.Contains);

    private void AdvanceInvalidationLocked()
    {
        _invalidationEpoch++;
        Volatile.Write(ref _freshInvalidation, 1);
    }

    /// <summary>
    /// Runs the session until node-lifetime cancellation or a fatal, contained failure.
    /// </summary>
    /// <param name="lifetimeToken">Node-lifetime cancellation that ends the session quietly.</param>
    public async Task RunAsync(CancellationToken lifetimeToken)
    {
        // Session-scoped transient protocol state, discarded at session end (AI-002 TR-1).
        lock (_stateLock)
        {
            _acceptedTranscript.AddRange(_runInputMessages);
        }

        int requestCount = 0;
        int consecutiveInvalidResponseCount = 0;

        // Resolves one cycle's transcript capture and reports it to the sink (AI-011 TR-6/TR-7). Recording is fully
        // contained: a throwing sink — or a throwing warning logger — never propagates into the session loop and
        // never replaces the cycle's own cancellation or error outcome.
        void CompleteCycleTranscript(
            MindSessionCycleTranscript completed,
            Stopwatch stopwatch,
            MindTranscriptCycleOutcome outcome,
            string? detail = null)
        {
            completed.Latency = stopwatch.Elapsed;
            completed.Outcome = outcome;
            completed.OutcomeDetail = detail;
            try
            {
                _transcriptSink.Record(completed);
            }
            catch (Exception exception)
            {
                TryLogTranscriptContainmentFailure(exception);
            }
        }

        void TryLogTranscriptContainmentFailure(Exception exception)
        {
            try
            {
                _logger.LogWarning(exception, "Recording the agent session transcript failed.");
            }
            catch (Exception)
            {
                // Logging infrastructure failure stays contained with the recording failure (AI-011 TR-7).
            }
        }

        _logger.LogInformation("Agent session starting with {ToolCount} tool(s).", _functions.Count);
        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                while (Interlocked.Exchange(ref _freshInvalidation, 0) != 0)
                {
                    // A fresh invalidation makes this boundary's request the replacement request (AI-002 TR-4).
                    // A second signal while reaching the boundary repeats the arbitration before context is sampled.
                }

                requestCount++;
                long materialisationEpoch = ReadInvalidationEpoch();
                AgentRequestContext requestContext = await _requestContextSource.MaterialiseAsync(lifetimeToken);
                if (ReadInvalidationEpoch() != materialisationEpoch)
                {
                    // Freshness arrived while the source was rendering. That snapshot must never become the fresh
                    // replacement request: discard it and materialise the new logical request at the next boundary.
                    _requestContextSource.Discard(requestContext.Confirmation);
                    continue;
                }

                IReadOnlyList<ChatMessage> requestTranscript = PrepareRequestTranscript(
                    requestContext,
                    out long requestEpoch);

                // Transcript capture (AI-011 TR-6) accompanies the cycle from its first provider interaction: a
                // cycle that never reaches the provider is not recorded and does not consume a transcript turn
                // number, and its outcome is only reported once the provider interaction actually started.
                MindSessionCycleTranscript cycleTranscript = new()
                {
                    CycleIndex = requestCount,
                    StartedAt = DateTimeOffset.Now,
                    Instructions = _instructions,
                    RequestMessages = requestTranscript,
                    RequestOptions = _transcriptRequestOptions,
                };
                lock (_stateLock)
                {
                    // Positions are stable within this frozen request: synthetic exchanges are retained forever
                    // (AI-002 TR-17), so every appended runtime-authored message is marked on every later cycle too.
                    cycleTranscript.RuntimeAuthoredMessageIndexes = [.. _runtimeAuthoredMessageIndexes];
                }

                var cycleStopwatch = Stopwatch.StartNew();

                ChatResponse? response;
                try
                {
                    response = await RequestWithTransportRetryAsync(
                        requestTranscript,
                        requestCount,
                        lifetimeToken,
                        cycleTranscript);
                }
                catch (Exception exception)
                {
                    _requestContextSource.Discard(requestContext.Confirmation);
                    if (cycleTranscript.ProviderInteractionStarted)
                    {
                        CompleteCycleTranscript(
                            cycleTranscript,
                            cycleStopwatch,
                            exception is OperationCanceledException
                                ? MindTranscriptCycleOutcome.Interrupted
                                : MindTranscriptCycleOutcome.Failed,
                            exception.Message);
                    }

                    throw;
                }
                if (response is null)
                {
                    // Interrupted mid-generation — by fresh-turn invalidation or lifetime — so partial assistant
                    // output was discarded; a fresh request replays the accepted transcript (AI-002 TR-4/5).
                    _requestContextSource.Discard(requestContext.Confirmation);
                    if (cycleTranscript.ProviderInteractionStarted)
                    {
                        bool staleCompletion = cycleTranscript.Response is not null;
                        cycleTranscript.Annotate(
                            MindTranscriptAnnotationKind.FreshTurnInvalidation,
                            staleCompletion
                                ? "A completed response returned after a fresh-turn invalidation was discarded "
                                + "whole without validation."
                                : "The request was superseded by a fresh-turn invalidation before a usable "
                                + "response settled.");
                        CompleteCycleTranscript(
                            cycleTranscript,
                            cycleStopwatch,
                            staleCompletion
                                ? MindTranscriptCycleOutcome.DiscardedStaleResponse
                                : MindTranscriptCycleOutcome.DiscardedBeforeResponse);
                    }

                    continue;
                }
                cycleTranscript.Response = response;

                FunctionCallContent[] calls;
                try
                {
                    calls = ValidateResponse(response, requestCount);
                }
                catch (AgentSessionException exception)
                {
                    _requestContextSource.Discard(requestContext.Confirmation);
                    int nextInvalidResponseCount = consecutiveInvalidResponseCount + 1;
                    cycleTranscript.Annotate(
                        MindTranscriptAnnotationKind.InvalidResponseRecovery,
                        $"Invalid response shape discarded ({exception.Message}); consecutive invalid responses "
                        + $"{nextInvalidResponseCount}/{_invalidResponseRecoveryPolicy.ConsecutiveFailureBudget}.");
                    bool recoveryCompleted;
                    try
                    {
                        // The final rejection's specific reason travels into the terminal exhaustion outcome
                        // (AI-011 TR-23).
                        recoveryCompleted = await TryBackoffInvalidResponseRecoveryAsync(
                            requestCount,
                            nextInvalidResponseCount,
                            exception.Message,
                            lifetimeToken);
                    }
                    catch (Exception recoveryException)
                    {
                        CompleteCycleTranscript(
                            cycleTranscript,
                            cycleStopwatch,
                            recoveryException is OperationCanceledException
                                ? MindTranscriptCycleOutcome.Interrupted
                                : MindTranscriptCycleOutcome.RecoveryExhausted,
                            recoveryException.Message);
                        throw;
                    }

                    if (!recoveryCompleted)
                    {
                        cycleTranscript.Annotate(
                            MindTranscriptAnnotationKind.InvalidResponseRecovery,
                            "Recovery was superseded by a fresh-turn invalidation; the invalid-response budget was "
                            + "not consumed.");
                        CompleteCycleTranscript(
                            cycleTranscript,
                            cycleStopwatch,
                            MindTranscriptCycleOutcome.InvalidResponseRecoverySuperseded);
                        continue;
                    }

                    cycleTranscript.Annotate(
                        MindTranscriptAnnotationKind.InvalidResponseRecovery,
                        $"Recovery backoff completed; a fresh request follows ({nextInvalidResponseCount}/"
                        + $"{_invalidResponseRecoveryPolicy.ConsecutiveFailureBudget} consecutive invalid responses).");
                    CompleteCycleTranscript(
                        cycleTranscript,
                        cycleStopwatch,
                        MindTranscriptCycleOutcome.InvalidResponseRecoveryScheduled);
                    consecutiveInvalidResponseCount = nextInvalidResponseCount;
                    continue;
                }

                // A response only resets the recovery streak after every shape, call-ID, and argument check passed.
                consecutiveInvalidResponseCount = 0;
                List<string> toleratedTexts = [.. CollectToleratedTexts(response)];
                if (!TryAcceptResponse(response, requestEpoch, requestContext.Confirmation, lifetimeToken))
                {
                    // Registration won while validation was running. The mutable turn remains available for its
                    // replacement request and the response never reaches accepted history (AI-002 TR-5).
                    _requestContextSource.Discard(requestContext.Confirmation);
                    cycleTranscript.Annotate(
                        MindTranscriptAnnotationKind.FreshTurnInvalidation,
                        "A fresh-turn invalidation arrived while the response was being validated; the completed "
                        + "stale response was discarded without acceptance.");
                    CompleteCycleTranscript(
                        cycleTranscript,
                        cycleStopwatch,
                        MindTranscriptCycleOutcome.DiscardedStaleResponse);
                    continue;
                }

                // Text alongside tool calls was tolerated to keep the response valid (AI-002 TR-12): it is stripped
                // from the retained exchange and recorded in the transcript sink (AI-011 TR-6).
                cycleTranscript.StrippedTexts.AddRange(toleratedTexts);

                List<AIContent> results = new(calls.Length);
                bool batchSkippedByInvalidation = false;
                try
                {
                    foreach (FunctionCallContent call in calls)
                    {
                        lifetimeToken.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref _freshInvalidation) != 0)
                        {
                            // The originating response is stale (AI-002 TR-4/5): this and every remaining call is never
                            // invoked, and each call ID without a natural result receives exactly one canonical
                            // cancellation result so an appended exchange stays protocol-valid. Node lifetime is
                            // checked first and stays terminal without synthetic results.
                            _logger.LogDebug(
                                "Agent session tool '{ToolName}' skipped: its response batch was invalidated.",
                                call.Name);
                            results.Add(new FunctionResultContent(call.CallId, CancelledActionResult));
                            cycleTranscript.ToolInvocations.Add(new MindTranscriptToolInvocation(
                                call.Name,
                                call.CallId,
                                call.Arguments)
                            {
                                Status = MindTranscriptToolInvocationStatus.SkippedByInvalidation,
                                Result = CancelledActionResult,
                            });
                            batchSkippedByInvalidation = true;
                            continue;
                        }

                        // The invocation is captured at start so an interruption still leaves a record whose terminal
                        // status shows it never delivered a result (AI-011 TR-6).
                        MindTranscriptToolInvocation invocation = new(call.Name, call.CallId, call.Arguments);
                        cycleTranscript.ToolInvocations.Add(invocation);
                        object? toolResult;
                        try
                        {
                            toolResult = await InvokeToolAsync(call, lifetimeToken);
                        }
                        catch (OperationCanceledException)
                        {
                            invocation.Status = MindTranscriptToolInvocationStatus.Interrupted;
                            throw;
                        }

                        invocation.Status = MindTranscriptToolInvocationStatus.Completed;
                        invocation.Result = toolResult;
                        results.Add(new FunctionResultContent(call.CallId, toolResult));
                    }
                }
                catch (Exception exception)
                {
                    if (cycleTranscript.ProviderInteractionStarted)
                    {
                        CompleteCycleTranscript(
                            cycleTranscript,
                            cycleStopwatch,
                            exception is OperationCanceledException
                                ? MindTranscriptCycleOutcome.Interrupted
                                : MindTranscriptCycleOutcome.Failed,
                            exception.Message);
                    }

                    throw;
                }

                if (batchSkippedByInvalidation)
                {
                    cycleTranscript.Annotate(
                        MindTranscriptAnnotationKind.FreshTurnInvalidation,
                        "A fresh-turn invalidation skipped part of the validated tool batch; skipped calls received "
                        + "the canonical cancellation result.");
                }

                // Retention boundary (AI-002 TR-17/TR-20): the batch has fully settled — every call executed or
                // canonically skipped — and the whole exchange joins the retained transcript: the stripped assistant
                // messages appended atomically at acceptance plus this tool-result message. Nothing is ever removed.
                lock (_stateLock)
                {
                    _acceptedTranscript.Add(new ChatMessage(ChatRole.Tool, results));
                }

                CompleteCycleTranscript(cycleTranscript, cycleStopwatch, MindTranscriptCycleOutcome.Accepted);
            }

            _logger.LogInformation("Agent session ended after {RequestCount} request(s).", requestCount);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            // Expected node-lifetime interruption is never a backend failure (AI-002 TR-14).
            _logger.LogInformation("Agent session ended by cancellation after {RequestCount} request(s).", requestCount);
        }
        finally
        {
            lock (_stateLock)
            {
                _ended = true;
                _acceptedTranscript.Clear();
            }

            // The session loop has exited: close the transcript gate so any later capture attempt from an
            // abandoned continuation becomes a contained no-op (AI-011 TR-6/TR-7).
            _transcriptSink.End();
        }
    }

    private async Task<bool> TryBackoffInvalidResponseRecoveryAsync(
        int requestCount,
        int nextInvalidResponseCount,
        string finalRejection,
        CancellationToken lifetimeToken)
    {
        var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        var phase = new ActivePhaseState(
            AgentSessionPhaseKind.InvalidResponseBackoff,
            toolName: null,
            callId: null,
            recoveryCancellation);
        if (!TryBeginPhase(phase, lifetimeToken))
        {
            recoveryCancellation.Dispose();
            lifetimeToken.ThrowIfCancellationRequested();
            // Superseded by a pending hold or fresh invalidation before the backoff began (AI-002 TR-4/5 versus
            // TR-12): recovery yields without consuming the invalid-response budget.
            return false;
        }

        try
        {
            lifetimeToken.ThrowIfCancellationRequested();
            if (recoveryCancellation.IsCancellationRequested || Volatile.Read(ref _freshInvalidation) != 0)
            {
                // Superseded by a fresh-turn invalidation (AI-002 TR-4/5 versus TR-12): the fresh request replaces
                // recovery without consuming the invalid-response budget or issuing an extra recovery request.
                return false;
            }

            if (nextInvalidResponseCount >= _invalidResponseRecoveryPolicy.ConsecutiveFailureBudget)
            {
                throw InvalidResponseRecoveryExhausted(requestCount, nextInvalidResponseCount, finalRejection);
            }

            _logger.LogWarning(
                "Agent session request {RequestCount} returned an invalid response shape; discarding it and "
                + "requesting a fresh response after configured backoff ({InvalidResponseCount}/{InvalidResponseLimit}).",
                requestCount,
                nextInvalidResponseCount,
                _invalidResponseRecoveryPolicy.ConsecutiveFailureBudget);
            await _invalidResponseRecoveryPolicy.BackoffAsync(nextInvalidResponseCount, recoveryCancellation.Token);
            lifetimeToken.ThrowIfCancellationRequested();
            return !recoveryCancellation.IsCancellationRequested && Volatile.Read(ref _freshInvalidation) == 0;
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Agent session request {RequestCount} invalid-response recovery interrupted.",
                requestCount);
            return false;
        }
        finally
        {
            EndActivePhase(phase);
            recoveryCancellation.Dispose();
        }
    }

    private async Task<ChatResponse?> RequestWithTransportRetryAsync(
        IReadOnlyList<ChatMessage> requestTranscript,
        int requestCount,
        CancellationToken lifetimeToken,
        MindSessionCycleTranscript cycleTranscript)
    {
        for (int attempt = 0; ; attempt++)
        {
            lifetimeToken.ThrowIfCancellationRequested();
            var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            var phase = new ActivePhaseState(
                AgentSessionPhaseKind.ModelRequest,
                toolName: null,
                callId: null,
                phaseCancellation);
            if (!TryBeginPhase(phase, lifetimeToken))
            {
                phaseCancellation.Dispose();
                lifetimeToken.ThrowIfCancellationRequested();
                // A fresh-turn invalidation superseded this attempt before or between transport retries
                // (AI-002 TR-4/5 versus TR-12): the loop issues the replacement request without consuming the
                // transport-retry budget.
                _logger.LogDebug(
                    "Agent session request {RequestCount} superseded by a fresh-turn invalidation.",
                    requestCount);
                return null;
            }

            try
            {
                _logger.LogDebug("Agent session request {RequestCount} starting.", requestCount);
                // The provider interaction gate (AI-011 TR-6): from the first issued provider call, the cycle's
                // outcome resolves into a transcript record.
                cycleTranscript.ProviderInteractionStarted = true;
                ChatResponse response = await _chatClient.GetResponseAsync(requestTranscript, _chatOptions, phaseCancellation.Token);
                // The returned response is retained for diagnostics immediately — before any cancellation or
                // rejection check — so even a non-cooperative provider returning after node-lifetime cancellation
                // leaves its contents on the transcript (AI-011 TR-6). Cancellation below stays terminal.
                cycleTranscript.Response = response;
                lifetimeToken.ThrowIfCancellationRequested();
                if (phaseCancellation.IsCancellationRequested || Volatile.Read(ref _freshInvalidation) != 0)
                {
                    // Cancelled generation — or a non-cooperative provider returning after invalidation — is
                    // discarded whole: the stale response is never validated, appended, or executed (AI-002 TR-5).
                    _logger.LogDebug("Agent session request {RequestCount} interrupted.", requestCount);
                    return null;
                }

                return response;
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (phaseCancellation.Token.IsCancellationRequested)
            {
                // Self-inflicted phase cancellation — issued only by a fresh-turn invalidation, always after the
                // stale latch is set and always before its own cancellation can arrive — is the expected
                // invalidation path: never a backend failure, never retried (AI-002 TR-4/5). Node-lifetime
                // cancellation is checked first and stays terminal (TR-14).
                _logger.LogDebug("Agent session request {RequestCount} interrupted.", requestCount);
                return null;
            }
            catch (Exception exception) when (attempt < _maxTransportRetries && IsTransientTransportFailure(exception, phaseCancellation.Token))
            {
                TimeSpan delay = _retryDelays[Math.Min(attempt, _retryDelays.Length - 1)];
                _logger.LogWarning(
                    exception,
                    "Agent session request {RequestCount} failed transiently; retrying in {RetryDelay}.",
                    requestCount,
                    delay);
                cycleTranscript.Annotate(
                    MindTranscriptAnnotationKind.TransportRetry,
                    $"Transient transport failure on attempt {attempt + 1} of at most {_maxTransportRetries + 1} "
                    + $"({exception.GetType().Name}: {exception.Message}); retrying in {delay}.");
                var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                var retryPhase = new ActivePhaseState(
                    AgentSessionPhaseKind.TransportRetryBackoff,
                    toolName: null,
                    callId: null,
                    retryCancellation);
                if (!TryBeginPhase(retryPhase, lifetimeToken))
                {
                    retryCancellation.Dispose();
                    lifetimeToken.ThrowIfCancellationRequested();
                    // The pending hold or invalidation superseded the pending retry (AI-002 TR-4/5): the stale
                    // request is never re-issued and the transport-retry budget is not consumed by it.
                    _logger.LogDebug(
                        "Agent session request {RequestCount} retry superseded by a fresh-turn invalidation.",
                        requestCount);
                    return null;
                }

                try
                {
                    await Task.Delay(delay, retryCancellation.Token);
                }
                catch (OperationCanceledException) when (!lifetimeToken.IsCancellationRequested)
                {
                    // A fresh-turn invalidation superseded the pending retry (AI-002 TR-4/5): the stale request is
                    // never re-issued and the transport-retry budget is not consumed by the invalidation.
                    _logger.LogDebug(
                        "Agent session request {RequestCount} retry superseded by a fresh-turn invalidation.",
                        requestCount);
                    return null;
                }
                finally
                {
                    EndActivePhase(retryPhase);
                    retryCancellation.Dispose();
                }
            }
            catch (Exception exception) when (IsTransientTransportFailure(exception, phaseCancellation.Token))
            {
                // Retry exhaustion ends the session through the contained failure path (AI-002 TR-12): the failure
                // is never surfaced to the agent as a tool result or transcript entry.
                throw new AgentSessionException(
                    $"The agent session request {requestCount} exhausted its transport retries: {exception.Message}",
                    exception);
            }
            catch (Exception exception)
            {
                throw new AgentSessionException(
                    $"The agent session request {requestCount} failed: {exception.Message}",
                    exception);
            }
            finally
            {
                EndActivePhase(phase);
                phaseCancellation.Dispose();
            }
        }
    }

    private async Task<object?> InvokeToolAsync(
        FunctionCallContent call,
        CancellationToken lifetimeToken)
    {
        AIFunction function = _functions[call.Name];
        var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        // Whether this invocation executes under admission arbitration is decided by its composition-registered
        // per-function phase policy alone — never by inspecting the function's identity. An arbitrated invocation
        // registers as a pending-admission phase (AI-002 TR-14; SPCH-005 TR-37): the submission's pipeline
        // admission transaction admits exactly this phase, and a cue linearising first either cancels it here —
        // tool selection alone stays ordinary cancellable work — or refuses that admission outright.
        var phase = new ActivePhaseState(
            AgentSessionPhaseKind.Tool,
            function.Name,
            call.CallId,
            phaseCancellation,
            function is PhasePolicyBoundFunction { Policy.AdmissionArbitrated: true }
                ? ToolPhaseAdmission.Pending
                : ToolPhaseAdmission.NotApplicable);

        if (!TryBeginPhase(phase, lifetimeToken))
        {
            phaseCancellation.Dispose();
            lifetimeToken.ThrowIfCancellationRequested();
            // The originating response is stale (AI-002 TR-4/5): this call never starts and its assistant call
            // ID still receives exactly one canonical cancellation result so an appended exchange stays
            // protocol-valid.
            _logger.LogDebug(
                "Agent session tool '{ToolName}' skipped: its response batch was invalidated.",
                call.Name);
            return CancelledActionResult;
        }

        try
        {
            _logger.LogDebug("Agent session tool '{ToolName}' starting.", function.Name);
            return await function.InvokeAsync(new AIFunctionArguments(call.Arguments), phaseCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Fresh-turn invalidation makes a co-operatively cancelled tool return early with the canonical
            // cancelled result (AI-002 TR-4/5); production tools normally report their own cut-short wording, and
            // a tool that crossed its commit boundary keeps its natural result above.
            _logger.LogDebug("Agent session tool '{ToolName}' interrupted.", function.Name);
            return CancelledActionResult;
        }
        catch (Exception exception)
        {
            // Tool errors surface through the tool result so the agent decides whether and how to retry
            // (AI-002 TR-12); they never end the session.
            _logger.LogWarning(exception, "Agent session tool '{ToolName}' failed.", function.Name);
            return $"The action failed: {exception.Message}";
        }
        finally
        {
            _logger.LogDebug("Agent session tool '{ToolName}' completed.", function.Name);
            EndActivePhase(phase);
            phaseCancellation.Dispose();
        }
    }

    /// <summary>
    /// Freezes one logical provider request over the retained transcript (AI-002 TR-2/TR-4): the materialisation's
    /// pending synthetic delivery exchange is appended to the retained transcript first — in chronological tail
    /// position, with its synthetic call IDs registered session-wide and delivery marked irrevocably (TR-18/TR-19) —
    /// then the request becomes the whole retained transcript followed by the materialisation's suffix, whose fresh
    /// current-scene message renders last.
    /// </summary>
    private IReadOnlyList<ChatMessage> PrepareRequestTranscript(
        AgentRequestContext requestContext,
        out long requestEpoch)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        lock (_stateLock)
        {
            if (requestContext.TranscriptMessages.Count > 0)
            {
                int firstIndex = _acceptedTranscript.Count;
                _acceptedTranscript.AddRange(requestContext.TranscriptMessages);
                foreach (ChatMessage message in requestContext.TranscriptMessages)
                {
                    foreach (AIContent content in message.Contents)
                    {
                        if (content is FunctionCallContent call)
                        {
                            if (!_callIds.Add(call.CallId))
                            {
                                throw new AgentSessionException(
                                    $"The agent session synthetic delivery reused the already-consumed call ID "
                                    + $"'{call.CallId}'.");
                            }
                        }
                    }

                    _runtimeAuthoredMessageIndexes.Add(firstIndex++);
                }

                // Delivery marking shares the acceptance critical section: the moment the synthetic exchange joins
                // the retained transcript its observations are delivered forever, whatever happens to this request
                // (AI-002 TR-19).
                _requestContextSource.Retain(requestContext.Confirmation);
            }

            requestEpoch = _invalidationEpoch;
            return [.. _acceptedTranscript, .. requestContext.SuffixMessages];
        }
    }

    private long ReadInvalidationEpoch()
    {
        lock (_stateLock)
        {
            return _invalidationEpoch;
        }
    }

    /// <summary>
    /// Atomically decides whether validated assistant output belongs to the request's epoch. If it does, the mutable
    /// assistant batch becomes accepted history — with tolerated text content stripped from the retained exchange
    /// (AI-002 TR-12/TR-20) — and the same critical section confirms the request snapshot's watermark (AI-002 TR-5).
    /// </summary>
    private bool TryAcceptResponse(
        ChatResponse response,
        long requestEpoch,
        object? contextConfirmation,
        CancellationToken lifetimeToken)
    {
        lock (_stateLock)
        {
            if (lifetimeToken.IsCancellationRequested || _ended || requestEpoch != _invalidationEpoch)
            {
                return false;
            }

            foreach (ChatMessage message in response.Messages)
            {
                _acceptedTranscript.Add(StripRetainedMessage(message));
                foreach (AIContent content in message.Contents)
                {
                    if (content is FunctionCallContent call)
                    {
                        _ = _callIds.Add(call.CallId);
                    }
                }
            }

            // Context confirmation is part of the same acceptance critical section as transcript mutation and epoch
            // arbitration. The source token is opaque to this generic runner.
            _requestContextSource.Confirm(contextConfirmation);
            return true;
        }
    }

    /// <summary>
    /// Returns the retained-exchange view of one accepted assistant message (AI-002 TR-20): the message instance
    /// itself when it carries no text content, otherwise a copy carrying only its tool calls and reasoning content —
    /// accompanying text is stripped and recorded separately (TR-12). The original parsed message stays untouched for
    /// transcript capture.
    /// </summary>
    private static ChatMessage StripRetainedMessage(ChatMessage message)
        => message.Contents.Any(static content => content is TextContent)
            ? new ChatMessage(message.Role, [.. message.Contents.Where(static content => content is not TextContent)])
            : message;

    /// <summary>
    /// Collects the non-empty text contents tolerated alongside a validated response's tool calls (AI-002 TR-12):
    /// the response stays valid, and each text becomes a stripped-text record in the transcript sink (AI-011 TR-11)
    /// when the response is accepted.
    /// </summary>
    private static IEnumerable<string> CollectToleratedTexts(ChatResponse response)
        => response.Messages
            .SelectMany(static message => message.Contents)
            .OfType<TextContent>()
            .Select(static text => text.Text)
            .Where(static text => !string.IsNullOrEmpty(text))!;

    /// <summary>Classifies one registered active phase of the session loop.</summary>
    private enum AgentSessionPhaseKind
    {
        ModelRequest,
        TransportRetryBackoff,
        InvalidResponseBackoff,
        Tool,
    }

    /// <summary>Admission state of an active tool phase under its composition-registered phase policy (AI-002 TR-14).</summary>
    private enum ToolPhaseAdmission
    {
        /// <summary>The phase's function was not composed with admission arbitration.</summary>
        NotApplicable,

        /// <summary>An arbitrated invocation whose pipeline admission transaction has not committed yet.</summary>
        Pending,

        /// <summary>An arbitrated submission whose pipeline queue admission committed before any matching cue.</summary>
        Admitted,
    }

    /// <summary>
    /// Lock-guarded active-phase state: every model request, retry or backoff delay, and tool invocation registers
    /// itself under the runner's state lock, carrying its cancellation source, its admission state under the
    /// composition-registered phase policy, and the speech keys whose cues its admission beat.
    /// </summary>
    private sealed class ActivePhaseState(
        AgentSessionPhaseKind kind,
        string? toolName,
        string? callId,
        CancellationTokenSource cancellation,
        ToolPhaseAdmission admission = ToolPhaseAdmission.NotApplicable)
    {
        public AgentSessionPhaseKind Kind { get; } = kind;

        public string? ToolName { get; } = toolName;

        public string? CallId { get; } = callId;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public ToolPhaseAdmission Admission
        {
            get;
            set;
        } = admission;

        /// <summary>Continuation keys whose attended cues linearised after admission, protecting this phase.</summary>
        public HashSet<SpeechContinuationKey> ProtectedBySpeechKeys { get; } = [];
    }

    private static IReadOnlyDictionary<string, AIFunction> ResolveFunctions(IList<AITool> productionTools)
    {
        Dictionary<string, AIFunction> functions = new(StringComparer.Ordinal);
        foreach (AITool tool in productionTools)
        {
            if (tool is not AIFunction function || string.IsNullOrWhiteSpace(function.Name))
            {
                throw new AgentSessionException("The agent session requires named production functions.");
            }

            if (!functions.TryAdd(function.Name, function))
            {
                throw new AgentSessionException("The agent session rejects duplicate production function names.");
            }
        }

        return functions;
    }

    /// <summary>
    /// Validates one response's shape and arguments — every call, identifier, argument, and content item —
    /// before any tool in the batch executes (AI-002 TR-12), rejecting each failing branch with a
    /// branch-specific diagnostic reason naming the parsed content that failed (AI-011 TR-20). The predicates
    /// and their evaluation order are unchanged: only the rejection reason differs, so no verdict, acceptance
    /// rule, or recovery-budget behaviour changes. Text content alongside tool calls is tolerated and stripped at
    /// acceptance (AI-002 TR-12).
    /// </summary>
    private FunctionCallContent[] ValidateResponse(ChatResponse response, int requestCount)
    {
        if (response.Messages.Count == 0)
        {
            throw InvalidResponse(requestCount, "the response carried no messages");
        }

        List<FunctionCallContent> calls = [];
        HashSet<string> batchCallIds = new(StringComparer.Ordinal);
        for (int messageIndex = 0; messageIndex < response.Messages.Count; messageIndex++)
        {
            ChatMessage message = response.Messages[messageIndex];
            if (message.Role != ChatRole.Assistant)
            {
                throw InvalidResponse(
                    requestCount,
                    $"message {messageIndex + 1} carried role '{message.Role.Value}' instead of 'assistant'");
            }

            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent reasoning)
                {
                    if (_enableReasoningLogging
                        && _logger.IsEnabled(LogLevel.Trace)
                        && !string.IsNullOrWhiteSpace(reasoning.Text))
                    {
                        _logger.LogTrace("Reasoning: {}", reasoning.Text);
                    }

                    continue;
                }

                if (content is TextContent)
                {
                    // Text alongside tool calls is tolerated: the response stays valid and the text is stripped from
                    // the retained exchange at acceptance (AI-002 TR-12).
                    continue;
                }

                if (content is TextContent)
                {
                    // Text alongside tool calls is tolerated: the response stays valid and the text is stripped from
                    // the retained exchange at acceptance (AI-002 TR-12). A response whose every content item is text
                    // still fails the at-least-one-call check below.
                    continue;
                }

                if (content is not FunctionCallContent call)
                {
                    throw InvalidResponse(
                        requestCount,
                        $"message {messageIndex + 1} carried {DescribeNonCallContent(content)} instead of a "
                        + "function call");
                }

                if (call.Exception is not null)
                {
                    throw InvalidResponse(
                        requestCount,
                        $"function call {DescribeCall(call)} failed to parse: {call.Exception.Message}");
                }

                if (call.InformationalOnly)
                {
                    throw InvalidResponse(
                        requestCount,
                        $"function call {DescribeCall(call)} was informational only and requests no invocation");
                }

                if (string.IsNullOrWhiteSpace(call.CallId))
                {
                    throw InvalidResponse(
                        requestCount,
                        $"the function call for tool '{call.Name}' carried no call ID");
                }

                if (_callIds.Contains(call.CallId))
                {
                    throw InvalidResponse(
                        requestCount,
                        $"call ID '{call.CallId}' was reused from an earlier accepted exchange");
                }

                if (!batchCallIds.Add(call.CallId))
                {
                    throw InvalidResponse(
                        requestCount,
                        $"call ID '{call.CallId}' appeared more than once in the same response");
                }

                if (!_functions.ContainsKey(call.Name))
                {
                    throw InvalidResponse(
                        requestCount,
                        $"function call {DescribeCall(call)} targeted unknown tool '{call.Name}'");
                }

                calls.Add(call);
            }
        }

        if (calls.Count == 0)
        {
            throw InvalidResponse(requestCount, "the response carried no function calls");
        }

        foreach (FunctionCallContent call in calls)
        {
            ValidateArguments(call, requestCount);
        }

        return [.. calls];
    }

    /// <summary>
    /// Describes one non-call content item for an invalid-shape rejection reason (AI-011 TR-20): parsed content
    /// metadata only — never a raw provider payload.
    /// </summary>
    private static string DescribeNonCallContent(AIContent content)
        => content switch
        {
            TextContent => "text content",
            FunctionResultContent => "a function call result",
            _ => $"content of type '{content.GetType().Name}'",
        };

    /// <summary>
    /// Labels one function call for a rejection reason using its parsed identifier and tool name; neither is an
    /// argument value (AI-011 TR-21).
    /// </summary>
    private static string DescribeCall(FunctionCallContent call)
        => string.IsNullOrWhiteSpace(call.CallId)
            ? $"for tool '{call.Name}'"
            : $"'{call.CallId}' of tool '{call.Name}'";

    private void ValidateArguments(FunctionCallContent call, int requestCount)
    {
        AIFunction function = _functions[call.Name];
        if (call.Arguments is null)
        {
            throw InvalidResponse(
                requestCount,
                $"the function call {DescribeCall(call)} supplied no arguments object");
        }

        string? mismatch = DescribeArgumentsMismatch(call.Arguments, function);
        if (mismatch is not null)
        {
            throw InvalidResponse(
                requestCount,
                $"the arguments of function call {DescribeCall(call)} mismatched their schema — {mismatch}");
        }
    }

    /// <summary>
    /// Returns the argument-schema mismatch description of one call's arguments, or null when they match. The
    /// description names the failing property path and the expected constraint without copying argument values
    /// (AI-011 TR-21); acceptance itself is decided by the unchanged schema walk.
    /// </summary>
    private static string? DescribeArgumentsMismatch(IDictionary<string, object?> arguments, AIFunction function)
    {
        JsonElement value;
        try
        {
            value = JsonSerializer.SerializeToElement(arguments, function.JsonSerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return "the parsed arguments could not be serialised for schema validation";
        }

        return TryDescribeSchemaMismatch(value, function.JsonSchema, ArgumentsPathRoot);
    }

    /// <summary>
    /// Walks one parsed value against its JSON schema, returning null when the value matches and otherwise a
    /// description naming the failing property path and the expected constraint (AI-011 TR-20/TR-21). The walk's
    /// structure and evaluation order are those of the schema matcher it replaces, so acceptance behaviour is
    /// unchanged while the verdict and its diagnostic now come from one shared implementation.
    /// </summary>
    /// <remarks>
    /// Internal so unit tests pin both the matching and mismatching descriptions against hand-built schemas,
    /// independently of any provider (AI-011 TR-13).
    /// </remarks>
    internal static string? TryDescribeSchemaMismatch(JsonElement value, JsonElement schema, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.Undefined)
        {
            return null;
        }

        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return schema.ValueKind == JsonValueKind.False
                ? $"{path}: the schema permits no value"
                : $"{path}: the schema is not a JSON object schema";
        }

        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(value, type))
        {
            return $"{path}: {DescribeTypeMismatch(value, type)}";
        }

        if (schema.TryGetProperty("enum", out JsonElement enumValues)
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            return $"{path}: expected one of the enumerated constants {DescribeEnumConstants(enumValues)}";
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> required = schema.TryGetProperty("required", out JsonElement requiredElement)
                ? [.. requiredElement.EnumerateArray().Select(item => item.GetString()!)]
                : [];
            foreach (string name in required)
            {
                if (!value.TryGetProperty(name, out _))
                {
                    return $"{path}: the required property '{name}' is missing";
                }
            }

            JsonElement properties = schema.TryGetProperty("properties", out JsonElement propertyElement)
                ? propertyElement
                : default;
            bool allowAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional)
                && additional.ValueKind != JsonValueKind.False;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                {
                    string? nested = TryDescribeSchemaMismatch(
                        property.Value,
                        propertySchema,
                        $"{path}.{property.Name}");
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
                else if (!allowAdditional)
                {
                    return $"{path}.{property.Name}: the property is not declared by the schema and additional "
                        + "properties are disallowed";
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement itemSchema))
        {
            int itemIndex = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                string? nested = TryDescribeSchemaMismatch(item, itemSchema, $"{path}[{itemIndex}]");
                if (nested is not null)
                {
                    return nested;
                }

                itemIndex++;
            }
        }

        return null;
    }

    /// <summary>
    /// Describes a type constraint failure using the schema's expected type and the parsed value's JSON kind —
    /// metadata only, never the argument value itself (AI-011 TR-21).
    /// </summary>
    private static string DescribeTypeMismatch(JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.String
            && type.GetString() == "integer"
            && value.ValueKind == JsonValueKind.Number
            && !value.TryGetInt64(out _))
        {
            return "expected type 'integer' but the parsed number is not an integer";
        }

        string expected = type.ValueKind == JsonValueKind.Array
            ? string.Join("', '", type.EnumerateArray().Select(DescribeSchemaType))
            : DescribeSchemaType(type);
        return type.ValueKind == JsonValueKind.Array
            ? $"expected one of the types '{expected}' but the parsed value is of kind '{value.ValueKind}'"
            : $"expected type '{expected}' but the parsed value is of kind '{value.ValueKind}'";
    }

    private static string DescribeSchemaType(JsonElement type)
        => type.ValueKind == JsonValueKind.String ? type.GetString() ?? string.Empty : type.GetRawText();

    private static string DescribeEnumConstants(JsonElement enumValues)
        => string.Join(
            ", ",
            enumValues.EnumerateArray().Select(static candidate =>
                $"'{(candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : candidate.GetRawText())}'"));

    private static bool MatchesType(JsonElement value, JsonElement type)
        => type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(candidate => MatchesType(value, candidate))
            : type.ValueKind == JsonValueKind.String && type.GetString() switch
            {
                "null" => value.ValueKind == JsonValueKind.Null,
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                _ => false,
            };

    /// <summary>
    /// Classifies transport failures requiring bounded transparent retry (AI-002 TR-12). Provider and HTTP
    /// timeouts — the OpenAI SDK's network timeout and <c>HttpClient</c> timeouts — surface as
    /// <see cref="TaskCanceledException" />/<see cref="OperationCanceledException" /> on a linked token rather than as
    /// <see cref="TimeoutException" /> and without cancelling the phase token, so a cancellation that cancelled
    /// neither the lifetime token nor the phase token is by elimination such a transport timeout: a cancelling
    /// <see cref="InvalidateForFreshTurn(bool)" /> always cancels the phase token before its own cancellation can
    /// arrive, while the wait-owned no-cancel invalidation cancels nothing at all, making phase-cancellation state
    /// the only self-inflicted-interruption discriminator.
    /// </summary>
    private static bool IsTransientTransportFailure(Exception exception, CancellationToken phaseToken)
    {
        if (exception is OperationCanceledException
            && (!phaseToken.IsCancellationRequested || exception.InnerException is TimeoutException))
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException:
                case IOException:
                case TimeoutException:
                    return true;
                case ClientResultException clientResult:
                    int status = clientResult.Status;
                    return status is 408 or 429 or (>= 500 and < 600);
                default:
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the invalid-response rejection with its branch-specific reason (AI-011 TR-20). The reason derives
    /// exclusively from the parsed response the runner already holds.
    /// </summary>
    private static AgentSessionException InvalidResponse(int requestCount, string reason)
        => new($"The agent session rejected the response at request {requestCount} because {reason}.");

    /// <summary>
    /// Builds the terminal exhaustion failure carrying both the exhaustion state and the branch-specific reason of
    /// the final rejection (AI-011 TR-23).
    /// </summary>
    private static AgentSessionException InvalidResponseRecoveryExhausted(
        int requestCount,
        int invalidResponseCount,
        string finalRejection)
        => new(
            $"The agent session exhausted its invalid response recovery budget after "
            + $"{invalidResponseCount} consecutive invalid response shapes at request {requestCount}. "
            + $"Final rejection: {finalRejection}");
}

/// <summary>
/// Contained, session-ending failure of an agent session: logged without crashing the scene and never retried
/// automatically (AI-002 TR-12).
/// </summary>
internal sealed class AgentSessionException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
