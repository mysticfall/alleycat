using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Executes one long-running agent session as an append-only transcript driven through bounded, stateless provider
/// requests (AI-002 Transcript Execution).
/// </summary>
/// <remarks>
/// <para>
/// This loop is deliberately custom rather than hosted by <c>Microsoft.Agents.AI</c>'s
/// <c>ChatClientAgent</c>/<c>AIAgent.RunAsync</c> abstractions. The framework's run loop applies one cancellation
/// token to the whole run, so it cannot cancel only an in-flight generation while a tool in flight continues
/// normally with its cut-short result (AI-002 TR-39 versus TR-40); it does not provide the strict whole-batch
/// tool-only validation before any execution (AI-002 TR-11/12); its history and function-invocation machinery would
/// append protocol entries beyond assistant tool calls, tool results, and injected messages (AI-002 TR-14) and
/// imposes iteration bounds. Where the framework fits without deviation — the stateless transport underneath — it is
/// used unchanged through <see cref="IChatClient"/>.
/// </para>
/// <para>
/// Thread safety: <see cref="QueueInjection"/>, <see cref="QueueFreshInjection"/>,
/// <see cref="InvalidateForFreshTurn(bool, bool)"/>, and <see cref="AbandonFreshInjection"/> may be called from any
/// thread; the transcript itself is only touched by <see cref="RunAsync"/>.
/// </para>
/// </remarks>
internal sealed class AgentSessionRunner
{
    /// <summary>Canonical cancellation result for every tool call that produced no natural result (AI-002 TR-40).</summary>
    private const string CancelledActionResult = "The action was cancelled before it completed.";

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
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private readonly Lock _stateLock = new();
    private TaskCompletionSource _freshInjectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<FreshInjectionExpectation, ExpectationState> _expectations = [];
    private readonly List<PendingInjection> _nextUserTurn = [];
    private readonly List<PendingInjection> _currentUserTurn = [];
    private readonly Dictionary<AgentSessionInjectionKey, long> _acceptedRevisions = [];
    private readonly Dictionary<AgentSessionInjectionKey, long> _reconciledRevisions = [];
    private readonly List<ChatMessage> _acceptedTranscript = [];
    private int _legacyFreshExpectationCount;
    private long _nextExpectationId;
    private long _invalidationEpoch;
    private ActivePhaseState? _activePhase;
    private int _freshInvalidation;
    private volatile bool _ended;

    public AgentSessionRunner(
        IChatClient chatClient,
        string instructions,
        IReadOnlyList<ChatMessage> runInputMessages,
        IList<AITool> productionTools,
        bool allowMultipleToolCalls,
        ILogger logger,
        bool enableReasoningLogging = true,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        IInvalidResponseRecoveryPolicy? invalidResponseRecoveryPolicy = null)
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
        _functions = ResolveFunctions(productionTools);
        _chatOptions = new ChatOptions
        {
            Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions)),
            Tools = [.. _functions.Values],
            ToolMode = ChatToolMode.RequireAny,
            AllowMultipleToolCalls = allowMultipleToolCalls,
            ResponseFormat = null,
        };
    }

    /// <summary>
    /// Queues an ordinary injected user message (AI-002 TR-39): the payload coalesces in FIFO order with every
    /// other pending payload into one injected user message appended at the next natural model-request boundary the
    /// session reaches. Ordinary injection never cancels an in-flight request, tool, backoff, or speech, never
    /// consumes a recovery budget, and never issues a model request of its own.
    /// </summary>
    /// <param name="injectedMessage">Concise rendered summary of the ordinary notable observations.</param>
    public void QueueInjection(string injectedMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(injectedMessage);
        if (_ended)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!_ended)
            {
                _nextUserTurn.Add(PendingInjection.Anonymous(injectedMessage));
            }
        }
    }

    /// <summary>
    /// Records a fresh-turn invalidation (AI-002 TR-40) that immediately supersedes every piece of work originating
    /// from the current stale response: the active phase — generation, invalid-response backoff, or the active tool
    /// call of a validated batch — is cancelled co-operatively at once, and the stale latch persists across
    /// generation, validation, backoff, and the complete remaining tool batch until the replacement request is
    /// issued. A response returned by a non-cooperative provider after its generation was invalidated is discarded.
    /// </summary>
    /// <param name="expectFreshInjection">
    /// Whether the caller will deliver the fresh payload through <see cref="QueueFreshInjection"/> — establishing a
    /// rendering barrier that keeps the replacement request behind the asynchronously rendered payload — and release
    /// it through <see cref="AbandonFreshInjection"/> when delivery is impossible. Wait-owned deliveries, whose wait
    /// result is the sole delivery channel, pass false.
    /// </param>
    public void InvalidateForFreshTurn(bool expectFreshInjection)
        => InvalidateForFreshTurn(expectFreshInjection, cancelActivePhase: true);

    /// <summary>
    /// Records a fresh-turn invalidation (AI-002 TR-40) whose cancellation of the active phase is optional: a
    /// wait-owned fresh signal passes <paramref name="cancelActivePhase"/> as false because the woken wait — the
    /// active phase and the sole delivery channel for its window — must complete naturally with its delivery
    /// (AI-002 TR-41), after which the stale latch persists across the wait's natural result, skips the batch's
    /// remaining calls with canonical cancellation results, and precedes exactly one replacement request. Every
    /// other caller cancels the active phase co-operatively at once.
    /// </summary>
    public void InvalidateForFreshTurn(bool expectFreshInjection, bool cancelActivePhase)
        => InvalidateForFreshTurnCore(expectFreshInjection, cancelActivePhase, freshSpeechKeys: null);

    /// <summary>
    /// Records a fresh-turn invalidation (AI-002 TR-40) whose active-phase cancellation is scoped by the fresh
    /// delivery's speech keys (TR-26): an admitted arbitrated phase whose admission beat every supplied key is
    /// never cancelled by the matching player-speech lifecycle, while an empty, absent, or only partially matching
    /// key set is unrelated freshness and cancels with ordinary pre-hand-off authority. Keying never changes the
    /// epoch, stale-batch latch, or rendering-barrier semantics.
    /// </summary>
    /// <param name="expectFreshInjection">Whether the caller will deliver the fresh payload through
    /// <see cref="QueueFreshInjection"/> and release it through <see cref="AbandonFreshInjection"/>.</param>
    /// <param name="freshSpeechKeys">Continuation keys of every pending speech hold the fresh window satisfies, or
    /// null/empty when the window carries no matching speech lifecycle.</param>
    internal void InvalidateForFreshTurn(
        bool expectFreshInjection,
        IReadOnlySet<AgentSessionContinuationKey>? freshSpeechKeys)
        => InvalidateForFreshTurnCore(expectFreshInjection, cancelActivePhase: true, freshSpeechKeys);

    private void InvalidateForFreshTurnCore(
        bool expectFreshInjection,
        bool cancelActivePhase,
        IReadOnlySet<AgentSessionContinuationKey>? freshSpeechKeys)
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
            if (expectFreshInjection)
            {
                bool wasHeld = HasPendingFreshExpectationLocked();
                _legacyFreshExpectationCount++;
                if (!wasHeld)
                {
                    ResetFreshInjectionGateLocked();
                }
            }

            cancel = cancelActivePhase && !IsProtectedByFreshSpeechKeysLocked(freshSpeechKeys);
        }

        if (cancel)
        {
            CancelActivePhase();
        }
    }

    /// <summary>
    /// Queues the fresh replacement payload (AI-002 TR-40) and releases one rendering-barrier expectation: the
    /// payload coalesces in FIFO order with every pending ordinary payload into the single injected user message
    /// preceding the replacement request.
    /// </summary>
    /// <param name="injectedMessage">Concise rendered summary of the fresh observations and their accumulation.</param>
    public void QueueFreshInjection(string injectedMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(injectedMessage);
        if (_ended)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_ended)
            {
                return;
            }

            _nextUserTurn.Add(PendingInjection.Anonymous(injectedMessage));
            ReleaseLegacyFreshExpectationLocked();
        }
    }

    /// <summary>
    /// Releases one rendering-barrier expectation without queueing a payload: the fresh delivery failed — its
    /// observations were restored to Mind for later delivery — so the replacement request proceeds without an
    /// injected message while the invalidation stays in force.
    /// </summary>
    public void AbandonFreshInjection()
    {
        if (_ended)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!_ended)
            {
                ReleaseLegacyFreshExpectationLocked();
            }
        }
    }

    /// <summary>
    /// Registers a keyed continuation barrier, invalidating the current phase before the matching rendered input is
    /// known. The returned lease belongs to this runner and may settle exactly once.
    /// </summary>
    /// <remarks>
    /// A cue that linearises after an admission-arbitrated submission was admitted does not cancel that phase
    /// (AI-002 TR-25/26): its exact speech key is associated with the admitted phase so the protected submission
    /// settles naturally, while every other phase — including an arbitrated submission still awaiting admission —
    /// is cancelled co-operatively at once.
    /// </remarks>
    internal FreshInjectionExpectation RegisterSpeechContinuation(AgentSessionContinuationKey continuation)
    {
        if (continuation.Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(continuation));
        }

        FreshInjectionExpectation expectation;
        CancellationTokenSource? cancellationToFire = null;
        lock (_stateLock)
        {
            expectation = new FreshInjectionExpectation(++_nextExpectationId, continuation);
            if (_ended)
            {
                return expectation;
            }

            bool wasHeld = HasPendingFreshExpectationLocked();
            _expectations.Add(expectation, ExpectationState.Pending);
            AdvanceInvalidationLocked();
            if (!wasHeld)
            {
                ResetFreshInjectionGateLocked();
            }

            if (_activePhase is { } phase)
            {
                if (phase.Admission == ToolPhaseAdmission.Admitted)
                {
                    // Admission completed before this cue linearised (AI-002 TR-56): the submission is protected
                    // for its whole pipeline life, so the cue holds the next phase without cancelling it.
                    _ = phase.ProtectedBySpeechKeys.Add(continuation);
                }
                else
                {
                    cancellationToFire = phase.Cancellation;
                }
            }
        }

        // Cancellation always happens outside the state lock (AI-002 TR-56 normative onset path).
        if (cancellationToFire is not null)
        {
            TryCancelQuietly(cancellationToFire);
        }

        return expectation;
    }

    /// <summary>
    /// Arbitrates the admission transaction of an admission-arbitrated tool phase (AI-002 TR-25/56, TR-62)
    /// atomically under the state lock: admission is refused — committing nothing — while any attended cue hold is
    /// pending or the session no longer owns a pending-arbitration phase; otherwise the caller's queue admission
    /// commits inside this critical section and the active phase is marked admitted, so later matching cues protect
    /// rather than cancel it.
    /// </summary>
    /// <param name="commit">Arbitrated pipeline's queue admission to run inside the lock; it must not throw.</param>
    /// <returns>Whether the submission was admitted.</returns>
    internal bool TryAdmitToolPhase(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (_stateLock)
        {
            if (_ended
                || _activePhase is not { Admission: ToolPhaseAdmission.Pending } arbitratedPhase
                || HasPendingFreshExpectationLocked())
            {
                return false;
            }

            commit();
            arbitratedPhase.Admission = ToolPhaseAdmission.Admitted;
            return true;
        }
    }

    /// <summary>Queues a generic injection, replacing a still-mutable keyed turn or reconciling accepted history.</summary>
    internal void QueueOrReplaceInjection(AgentSessionInjection injection)
    {
        ArgumentNullException.ThrowIfNull(injection);
        ValidateInjection(injection);
        lock (_stateLock)
        {
            if (!_ended)
            {
                _ = QueueOrReplaceInjectionLocked(injection);
            }
        }
    }

    /// <summary>Settles an owned continuation lease only when the supplied injection covers its awaited revision.</summary>
    internal void CompleteFreshExpectation(FreshInjectionExpectation expectation, AgentSessionInjection injection)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(injection);
        ValidateInjection(injection);
        lock (_stateLock)
        {
            if (_ended
                || !_expectations.TryGetValue(expectation, out ExpectationState state)
                || state != ExpectationState.Pending
                || injection.Kind != AgentSessionInjectionKind.CurrentUserTurn
                || injection.Key != expectation.Continuation.Key
                || injection.Revision < expectation.Continuation.Revision)
            {
                return;
            }

            _ = QueueOrReplaceInjectionLocked(injection);
            // The caller owns identity matching. A projection can cover several continuations, but each opaque lease
            // must be settled explicitly so a later segment cannot release an unrelated outstanding hold merely
            // because its model-facing revision is newer.
            _expectations[expectation] = ExpectationState.Settled;

            ReleaseFreshGateIfSettledLocked();
        }
    }

    /// <summary>Abandons an owned lease without fabricating input; duplicate and foreign abandonment is harmless.</summary>
    internal void AbandonFreshExpectation(FreshInjectionExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        lock (_stateLock)
        {
            if (!_ended
                && _expectations.TryGetValue(expectation, out ExpectationState state)
                && state == ExpectationState.Pending)
            {
                _expectations[expectation] = ExpectationState.Abandoned;
                ReleaseFreshGateIfSettledLocked();
            }
        }
    }

    /// <summary>
    /// Settles an owned continuation lease whose rendered observation text reached the model through a wait result
    /// (AI-002 TR-57): the wait result is the sole delivery channel, so no replacement injection is queued, and the
    /// fresh gate releases once every pending expectation has settled. Duplicate and foreign settlement is
    /// harmless.
    /// </summary>
    internal void SettleFreshExpectationThroughWaitDelivery(FreshInjectionExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        lock (_stateLock)
        {
            if (!_ended
                && _expectations.TryGetValue(expectation, out ExpectationState state)
                && state == ExpectationState.Pending)
            {
                _expectations[expectation] = ExpectationState.Settled;
                ReleaseFreshGateIfSettledLocked();
            }
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
    /// outside it (AI-002 TR-56: the onset path takes only the runner state lock and cancels after releasing it).
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
    /// Registers one active phase atomically under the state lock (AI-002 TR-56): registration and cue
    /// linearisation become mutually exclusive, closing the write-then-observe race where a phase began after a
    /// pending speech hold or fresh invalidation was recorded but before its cancellation source was observable.
    /// A phase refuses to start while any pending expectation or stale latch blocks the session's next work.
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

            if (_ended || Volatile.Read(ref _freshInvalidation) != 0 || HasPendingFreshExpectationLocked())
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
    /// delivery (AI-002 TR-26/40): protection applies only to the matching player-speech lifecycle, so an absent,
    /// empty, or partially matching key set is unrelated freshness and keeps ordinary cancellation authority.
    /// </summary>
    /// <remarks>Must be called while holding <see cref="_stateLock"/>.</remarks>
    private bool IsProtectedByFreshSpeechKeysLocked(IReadOnlySet<AgentSessionContinuationKey>? freshSpeechKeys)
        => _activePhase is { Admission: ToolPhaseAdmission.Admitted, ProtectedBySpeechKeys.Count: > 0 } phase
            && freshSpeechKeys is { Count: > 0 }
            && freshSpeechKeys.All(phase.ProtectedBySpeechKeys.Contains);

    private void AdvanceInvalidationLocked()
    {
        _invalidationEpoch++;
        Volatile.Write(ref _freshInvalidation, 1);
    }

    private bool HasPendingFreshExpectationLocked()
        => _legacyFreshExpectationCount > 0 || _expectations.Values.Any(state => state == ExpectationState.Pending);

    private void ResetFreshInjectionGateLocked()
        => _freshInjectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ReleaseLegacyFreshExpectationLocked()
    {
        if (_legacyFreshExpectationCount > 0)
        {
            _legacyFreshExpectationCount--;
            ReleaseFreshGateIfSettledLocked();
        }
    }

    private void ReleaseFreshGateIfSettledLocked()
    {
        if (!HasPendingFreshExpectationLocked())
        {
            _ = _freshInjectionGate.TrySetResult();
        }
    }

    /// <summary>
    /// Blocks the replacement request while any fresh payload is still expected but not yet queued (AI-002 TR-40
    /// rendering barrier). Node-lifetime cancellation observed here ends the session without a replacement request.
    /// </summary>
    private async Task WaitForFreshInjectionsAsync(CancellationToken lifetimeToken)
    {
        Task gate;
        lock (_stateLock)
        {
            gate = _legacyFreshExpectationCount > 0 || _expectations.Values.Any(state => state == ExpectationState.Pending)
                ? _freshInjectionGate.Task
                : Task.CompletedTask;
        }

        if (gate.IsCompleted)
        {
            return;
        }

        _logger.LogDebug("Agent session waiting for pending fresh-observation rendering before its next request.");
        await gate.WaitAsync(lifetimeToken);
    }

    /// <summary>
    /// Runs the session until node-lifetime cancellation or a fatal, contained failure.
    /// </summary>
    /// <param name="lifetimeToken">Node-lifetime cancellation that ends the session quietly.</param>
    public async Task RunAsync(CancellationToken lifetimeToken)
    {
        // Session-scoped transient protocol state, discarded at session end (AI-002 TR-15).
        lock (_stateLock)
        {
            _acceptedTranscript.AddRange(_runInputMessages);
        }

        int requestCount = 0;
        int consecutiveInvalidResponseCount = 0;
        _logger.LogInformation("Agent session starting with {ToolCount} tool(s).", _functions.Count);
        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                while (Interlocked.Exchange(ref _freshInvalidation, 0) != 0)
                {
                    // A fresh invalidation makes this boundary's request the replacement request (AI-002 TR-40):
                    // it waits behind every still-rendering fresh payload, then drains the coalesced injection
                    // before leaving. A second signal during the drain re-enters the loop.
                    await WaitForFreshInjectionsAsync(lifetimeToken);
                }

                requestCount++;
                IReadOnlyList<ChatMessage> requestTranscript = PrepareRequestTranscript(out long requestEpoch);

                ChatResponse? response = await RequestWithTransportRetryAsync(
                    requestTranscript,
                    requestCount,
                    lifetimeToken);
                if (response is null)
                {
                    // Interrupted mid-generation — by fresh-turn invalidation or lifetime — so partial assistant
                    // output was discarded; the drained injection and a fresh request replay the transcript
                    // (AI-002 TR-40).
                    continue;
                }

                FunctionCallContent[] calls;
                try
                {
                    calls = ValidateResponse(response, requestCount);
                }
                catch (AgentSessionException)
                {
                    int nextInvalidResponseCount = consecutiveInvalidResponseCount + 1;
                    bool recoveryCompleted = await TryBackoffInvalidResponseRecoveryAsync(
                        requestCount,
                        nextInvalidResponseCount,
                        lifetimeToken);
                    if (!recoveryCompleted)
                    {
                        continue;
                    }

                    consecutiveInvalidResponseCount = nextInvalidResponseCount;
                    continue;
                }

                // A response only resets the recovery streak after every shape, call-ID, and argument check passed.
                consecutiveInvalidResponseCount = 0;
                if (!TryAcceptResponse(response, requestEpoch, lifetimeToken))
                {
                    // Registration won while validation was running. The mutable turn remains available for its
                    // replacement request and the response never reaches accepted history (AI-002 TR-59).
                    continue;
                }

                List<AIContent> results = new(calls.Length);
                foreach (FunctionCallContent call in calls)
                {
                    lifetimeToken.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _freshInvalidation) != 0)
                    {
                        // The originating response is stale (AI-002 TR-40): this and every remaining call is never
                        // invoked, and each call ID without a natural result receives exactly one canonical
                        // cancellation result so the appended exchange stays protocol-valid. Node lifetime is
                        // checked first and stays terminal without synthetic results.
                        _logger.LogDebug(
                            "Agent session tool '{ToolName}' skipped: its response batch was invalidated.",
                            call.Name);
                        results.Add(new FunctionResultContent(call.CallId, CancelledActionResult));
                        continue;
                    }

                    results.Add(new FunctionResultContent(call.CallId, await InvokeToolAsync(call, lifetimeToken)));
                }

                lock (_stateLock)
                {
                    _acceptedTranscript.Add(new ChatMessage(ChatRole.Tool, results));
                }
            }

            _logger.LogInformation("Agent session ended after {RequestCount} request(s).", requestCount);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            // Expected node-lifetime interruption is never a backend failure (AI-002 TR-44).
            _logger.LogInformation("Agent session ended by cancellation after {RequestCount} request(s).", requestCount);
        }
        finally
        {
            lock (_stateLock)
            {
                _ended = true;
                _nextUserTurn.Clear();
                _currentUserTurn.Clear();
                _acceptedTranscript.Clear();
            }
        }
    }

    private async Task<bool> TryBackoffInvalidResponseRecoveryAsync(
        int requestCount,
        int nextInvalidResponseCount,
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
            // Superseded by a pending hold or fresh invalidation before the backoff began (AI-002 TR-40 versus
            // TR-43): recovery yields without consuming the invalid-response budget.
            return false;
        }

        try
        {
            lifetimeToken.ThrowIfCancellationRequested();
            if (recoveryCancellation.IsCancellationRequested || Volatile.Read(ref _freshInvalidation) != 0)
            {
                // Superseded by a fresh-turn invalidation (AI-002 TR-40 versus TR-43): the fresh request replaces
                // recovery without consuming the invalid-response budget or issuing an extra recovery request.
                return false;
            }

            if (nextInvalidResponseCount >= _invalidResponseRecoveryPolicy.ConsecutiveFailureBudget)
            {
                throw InvalidResponseRecoveryExhausted(requestCount, nextInvalidResponseCount);
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
        IReadOnlyList<ChatMessage> transcript,
        int requestCount,
        CancellationToken lifetimeToken)
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
                // A pending speech hold or fresh-turn invalidation superseded this attempt before or between
                // transport retries (AI-002 TR-40/56 versus TR-43): abandon it — the loop boundary drains the
                // fresh payload and issues the replacement request — without consuming the transport-retry budget.
                _logger.LogDebug(
                    "Agent session request {RequestCount} superseded by a fresh-turn invalidation.",
                    requestCount);
                return null;
            }

            try
            {
                _logger.LogDebug("Agent session request {RequestCount} starting.", requestCount);
                ChatResponse response = await _chatClient.GetResponseAsync(transcript, _chatOptions, phaseCancellation.Token);
                lifetimeToken.ThrowIfCancellationRequested();
                if (phaseCancellation.IsCancellationRequested || Volatile.Read(ref _freshInvalidation) != 0)
                {
                    // Cancelled generation — or a non-cooperative provider returning after invalidation — is
                    // discarded whole: the stale response is never validated, appended, or executed (AI-002 TR-40).
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
                // invalidation path: never a backend failure, never retried (AI-002 TR-40/41). Node-lifetime
                // cancellation is checked first and stays terminal (TR-44).
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
                    // The pending hold or invalidation superseded the pending retry (AI-002 TR-40/56): the stale
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
                    // A fresh-turn invalidation superseded the pending retry (AI-002 TR-40): the stale request is
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
                // Retry exhaustion ends the session through the contained failure path (AI-002 TR-43): the failure
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
        // per-function phase policy alone (AI-002 TR-62) — never by inspecting the function's identity. An
        // arbitrated invocation registers as a pending-admission phase (AI-002 TR-25/56; SPCH-005 TR-37): the
        // submission's pipeline admission transaction admits exactly this phase, and a cue linearising first
        // either cancels it here — tool selection alone stays ordinary cancellable work — or refuses that
        // admission outright.
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
            // The originating response is stale (AI-002 TR-40/56): this call never starts and its assistant call
            // ID still receives exactly one canonical cancellation result so the appended exchange stays
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
            // cancelled result (AI-002 TR-39/40); production tools normally report their own cut-short wording, and
            // a tool that crossed its commit boundary keeps its natural result above.
            _logger.LogDebug("Agent session tool '{ToolName}' interrupted.", function.Name);
            return CancelledActionResult;
        }
        catch (Exception exception)
        {
            // Tool errors surface through the tool result so the agent decides whether and how to retry
            // (AI-002 TR-42); they never end the session.
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
    /// Freezes the accepted transcript plus the current mutable user turn for one provider request. A keyed update
    /// can still replace that turn until <see cref="TryAcceptResponse"/> linearises the assistant append.
    /// </summary>
    private IReadOnlyList<ChatMessage> PrepareRequestTranscript(out long requestEpoch)
    {
        lock (_stateLock)
        {
            if (_currentUserTurn.Count == 0 && _nextUserTurn.Count > 0)
            {
                _currentUserTurn.AddRange(_nextUserTurn);
                _nextUserTurn.Clear();
            }

            List<ChatMessage> request = [.. _acceptedTranscript];
            if (_currentUserTurn.Count > 0)
            {
                request.Add(new ChatMessage(ChatRole.User, RenderTurn(_currentUserTurn)));
            }

            requestEpoch = _invalidationEpoch;
            return request;
        }
    }

    /// <summary>
    /// Atomically decides whether validated assistant output belongs to the request's epoch. If it does, the mutable
    /// current user turn and assistant batch become append-only accepted history together (AI-002 TR-58/59).
    /// </summary>
    private bool TryAcceptResponse(ChatResponse response, long requestEpoch, CancellationToken lifetimeToken)
    {
        lock (_stateLock)
        {
            if (lifetimeToken.IsCancellationRequested || _ended || requestEpoch != _invalidationEpoch)
            {
                return false;
            }

            if (_currentUserTurn.Count > 0)
            {
                _acceptedTranscript.Add(new ChatMessage(ChatRole.User, RenderTurn(_currentUserTurn)));
                foreach (PendingInjection injection in _currentUserTurn)
                {
                    if (injection.Key is AgentSessionInjectionKey key)
                    {
                        _acceptedRevisions[key] = injection.Revision;
                    }
                }

                _currentUserTurn.Clear();
            }

            _acceptedTranscript.AddRange(response.Messages);
            return true;
        }
    }

    private bool QueueOrReplaceInjectionLocked(AgentSessionInjection injection)
    {
        bool hasAcceptedRevision = _acceptedRevisions.TryGetValue(injection.Key, out long acceptedRevision);
        if (injection.Kind == AgentSessionInjectionKind.Reconciliation || hasAcceptedRevision)
        {
            if ((hasAcceptedRevision && injection.Revision <= acceptedRevision)
                || (_reconciledRevisions.TryGetValue(injection.Key, out long reconciledRevision)
                    && injection.Revision <= reconciledRevision))
            {
                return false;
            }

            _nextUserTurn.Add(PendingInjection.Reconciliation(injection));
            _reconciledRevisions[injection.Key] = injection.Revision;
            return true;
        }

        return TryReplaceMutableInjectionLocked(_currentUserTurn, injection, out bool replacedCurrent)
            ? replacedCurrent
            : ReplaceOrAppendMutableInjectionLocked(_nextUserTurn, injection);
    }

    private static bool TryReplaceMutableInjectionLocked(
        List<PendingInjection> turn,
        AgentSessionInjection injection,
        out bool replaced)
    {
        for (int index = 0; index < turn.Count; index++)
        {
            PendingInjection existing = turn[index];
            if (existing.Key != injection.Key)
            {
                continue;
            }

            replaced = injection.Revision > existing.Revision;
            if (replaced)
            {
                turn[index] = PendingInjection.Keyed(injection);
            }

            return true;
        }

        replaced = false;
        return false;
    }

    private static bool ReplaceOrAppendMutableInjectionLocked(
        List<PendingInjection> turn,
        AgentSessionInjection injection)
    {
        if (TryReplaceMutableInjectionLocked(turn, injection, out bool replaced))
        {
            return replaced;
        }

        turn.Add(PendingInjection.Keyed(injection));
        return true;
    }

    private static string RenderTurn(List<PendingInjection> turn)
        => string.Join("\n", turn.Select(injection => injection.Text));

    private static void ValidateInjection(AgentSessionInjection injection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(injection.Key.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(injection.Key.Turn);
        ArgumentOutOfRangeException.ThrowIfNegative(injection.Revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(injection.RenderedText);
    }

    private enum ExpectationState
    {
        Pending,
        Settled,
        Abandoned,
    }

    /// <summary>Classifies one registered active phase of the session loop (AI-002 TR-56).</summary>
    private enum AgentSessionPhaseKind
    {
        ModelRequest,
        TransportRetryBackoff,
        InvalidResponseBackoff,
        Tool,
    }

    /// <summary>Admission state of an active tool phase under its composition-registered policy (AI-002 TR-25/26, TR-62).</summary>
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
    /// Lock-guarded active-phase state (AI-002 TR-56): every model request, retry or backoff delay, and tool
    /// invocation registers itself under the runner's state lock, carrying its cancellation source, its admission
    /// state under the composition-registered phase policy, and the speech keys whose cues its admission beat.
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
        public HashSet<AgentSessionContinuationKey> ProtectedBySpeechKeys { get; } = [];
    }

    private readonly record struct PendingInjection(
        AgentSessionInjectionKey? Key,
        long Revision,
        string Text)
    {
        public static PendingInjection Anonymous(string text) => new(null, 0, text);

        public static PendingInjection Keyed(AgentSessionInjection injection)
            => new(injection.Key, injection.Revision, injection.RenderedText);

        public static PendingInjection Reconciliation(AgentSessionInjection injection)
            => new(
                injection.Key,
                injection.Revision,
                $"The input continued. Its complete current content is:\n{injection.RenderedText}\n"
                + "Earlier responses or actions may already have occurred.");
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
    /// Validates the complete response batch — every call, identifier, argument, and content item — before any tool
    /// in the batch executes (AI-002 TR-11/12).
    /// </summary>
    private FunctionCallContent[] ValidateResponse(ChatResponse response, int requestCount)
    {
        if (response.Messages.Count == 0)
        {
            throw InvalidResponse(requestCount);
        }

        List<FunctionCallContent> calls = [];
        HashSet<string> batchCallIds = new(StringComparer.Ordinal);
        foreach (ChatMessage message in response.Messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                throw InvalidResponse(requestCount);
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

                if (content is not FunctionCallContent call)
                {
                    throw InvalidResponse(requestCount);
                }

                if (call.Exception is not null
                    || call.InformationalOnly
                    || string.IsNullOrWhiteSpace(call.CallId)
                    || _callIds.Contains(call.CallId)
                    || !batchCallIds.Add(call.CallId)
                    || !_functions.ContainsKey(call.Name))
                {
                    throw InvalidResponse(requestCount);
                }

                calls.Add(call);
            }
        }

        if (calls.Count == 0)
        {
            throw InvalidResponse(requestCount);
        }

        foreach (FunctionCallContent call in calls)
        {
            ValidateArguments(call, requestCount);
        }

        _callIds.UnionWith(batchCallIds);

        return [.. calls];
    }

    private void ValidateArguments(FunctionCallContent call, int requestCount)
    {
        AIFunction function = _functions[call.Name];
        if (call.Arguments is null || !ArgumentsMatchSchema(call.Arguments, function))
        {
            throw InvalidResponse(requestCount);
        }
    }

    private static bool ArgumentsMatchSchema(IDictionary<string, object?> arguments, AIFunction function)
    {
        try
        {
            JsonElement value = JsonSerializer.SerializeToElement(arguments, function.JsonSerializerOptions);
            return MatchesSchema(value, function.JsonSchema);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool MatchesSchema(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.Undefined)
        {
            return true;
        }

        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(value, type))
        {
            return false;
        }

        if (schema.TryGetProperty("enum", out JsonElement enumValues)
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            return false;
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
                    return false;
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
                    if (!MatchesSchema(property.Value, propertySchema))
                    {
                        return false;
                    }
                }
                else if (!allowAdditional)
                {
                    return false;
                }
            }
        }

        return value.ValueKind != JsonValueKind.Array
            || !schema.TryGetProperty("items", out JsonElement itemSchema)
            || !value.EnumerateArray().Any(item => !MatchesSchema(item, itemSchema));
    }

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
    /// Classifies transport failures requiring bounded transparent retry (AI-002 TR-43). Provider and HTTP
    /// timeouts — the OpenAI SDK's network timeout and <c>HttpClient</c> timeouts — surface as
    /// <see cref="TaskCanceledException" />/<see cref="OperationCanceledException" /> on a linked token rather than as
    /// <see cref="TimeoutException" /> and without cancelling the phase token, so a cancellation that cancelled
    /// neither the lifetime token nor the phase token is by elimination such a transport timeout: a cancelling
    /// <see cref="InvalidateForFreshTurn(bool, bool)" /> always cancels the phase token before its own cancellation
    /// can arrive, the wait-owned no-cancel invalidation cancels nothing at all, and ordinary
    /// <see cref="QueueInjection(string)" /> cancels nothing, making phase-cancellation state
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

    private static AgentSessionException InvalidResponse(int requestCount)
        => new($"The agent session received an invalid response shape at request {requestCount}.");

    private static AgentSessionException InvalidResponseRecoveryExhausted(int requestCount, int invalidResponseCount)
        => new(
            $"The agent session exhausted its invalid response recovery budget after "
            + $"{invalidResponseCount} consecutive invalid response shapes at request {requestCount}.");
}

/// <summary>
/// Contained, session-ending failure of an agent session: logged without crashing the scene and never retried
/// automatically (AI-002 TR-43, TR-12).
/// </summary>
internal sealed class AgentSessionException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
