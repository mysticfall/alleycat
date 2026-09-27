using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// The API scenario code uses to drive one bounded, traced execution: send target requests, generate
/// continuations under an isolated history, and supply scripted continuations and simulated tool results.
/// </summary>
/// <remarks>
/// <para>
/// Every send performs one non-streaming request through a borrowed client with the caller-supplied
/// messages and options — tool definitions, tool mode, and model settings included — and never judges,
/// retries, or automatically executes model-selected functions. Responses containing tool calls and/or
/// text are captured equally as legitimate experimental output; whether a response is valid for the
/// scenario is decided by scenario-specific checks, typically by throwing
/// <see cref="LiveLLMScenarioException" />.
/// </para>
/// <para>
/// The context owns no clients: it borrows the clients injected into the executor and never disposes
/// them. Each send receives exactly the message list the scenario passes, so generated continuations run
/// under a separate, isolated history and contexts cannot bleed between the target and the continuation
/// generator.
/// </para>
/// <para>
/// Declared bounds are enforced per operation and the declared timeout budget applies to the whole
/// execution; violations fail the execution with a clear classification recorded in the trace.
/// </para>
/// </remarks>
public sealed class LiveLLMScenarioContext
{
    private readonly IChatClient _target;
    private readonly IChatClient? _continuationGenerator;
    private readonly LiveLLMScenarioBounds _bounds;
    private readonly List<LiveLLMTraceEntry> _entries = [];
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationToken _callerToken;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _completed;

    internal LiveLLMScenarioContext(
        IChatClient target,
        IChatClient? continuationGenerator,
        LiveLLMScenarioBounds bounds,
        CancellationToken callerToken)
    {
        _target = target;
        _continuationGenerator = continuationGenerator;
        _bounds = bounds;
        _callerToken = callerToken;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _deadline.CancelAfter(_bounds.Timeout);
    }

    /// <summary>Whether the scenario's own timeout deadline has fired.</summary>
    internal bool TimeoutFired => _deadline.IsCancellationRequested && !_callerToken.IsCancellationRequested;

    /// <summary>Trace entries captured so far, in execution order.</summary>
    internal IReadOnlyList<LiveLLMTraceEntry> Entries => _entries;

    /// <summary>Number of target requests made so far.</summary>
    public int TargetRequests
    {
        get; private set;
    }

    /// <summary>Number of generated continuation requests made so far.</summary>
    public int ContinuationRequests
    {
        get; private set;
    }

    /// <summary>Number of simulated tool results supplied so far.</summary>
    public int ToolResults
    {
        get; private set;
    }

    /// <summary>
    /// Sends one non-streaming request to the target client with the caller-supplied messages and options.
    /// </summary>
    /// <param name="messages">Complete message list for this request.</param>
    /// <param name="options">Caller-supplied request settings, including tool definitions and tool mode.</param>
    /// <param name="cancellationToken">Optional additional token for this request.</param>
    /// <returns>The provider's response, carrying text and/or tool calls.</returns>
    public Task<ChatResponse> SendTargetAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();

        if (TargetRequests >= _bounds.MaxTargetRequests)
        {
            throw new LiveLLMScenarioException(
                LiveLLMFailureClassification.BoundExceeded,
                $"The scenario exceeded its declared bound of {_bounds.MaxTargetRequests} target request(s).");
        }

        TargetRequests++;
        return SendCoreAsync(LiveLLMPurpose.Target, _target, messages, options, cancellationToken);
    }

    /// <summary>
    /// Sends one non-streaming request to the borrowed continuation-generator client under the isolated
    /// history the scenario supplies. The harness carries no state between requests, so the continuation
    /// context never bleeds into or from the target history.
    /// </summary>
    /// <param name="messages">Complete, isolated message list for this continuation request.</param>
    /// <param name="options">Caller-supplied request settings.</param>
    /// <param name="cancellationToken">Optional additional token for this request.</param>
    /// <returns>The provider's response.</returns>
    public Task<ChatResponse> SendContinuationAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();

        if (ContinuationRequests >= _bounds.MaxContinuationRequests)
        {
            throw new LiveLLMScenarioException(
                LiveLLMFailureClassification.BoundExceeded,
                $"The scenario exceeded its declared bound of {_bounds.MaxContinuationRequests} continuation request(s).");
        }

        if (_continuationGenerator is null)
        {
            throw new LiveLLMScenarioException(
                LiveLLMFailureClassification.Unknown,
                "The scenario attempted a generated continuation without a continuation generator client.");
        }

        ContinuationRequests++;
        return SendCoreAsync(LiveLLMPurpose.Continuation, _continuationGenerator, messages, options, cancellationToken);
    }

    /// <summary>
    /// Records a scripted continuation message supplied by the experiment and returns it unchanged, so the
    /// scenario can append it to the next target request.
    /// </summary>
    /// <param name="message">The continuation message the experiment supplies.</param>
    /// <returns>The same message, for direct inclusion in the next request.</returns>
    public ChatMessage RecordScriptedContinuation(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfCompleted();

        _entries.Add(new LiveLLMTraceEntry(
            _entries.Count,
            LiveLLMTraceEntryKind.SuppliedContinuation,
            LiveLLMPurpose.Continuation,
            DateTimeOffset.UtcNow,
            Duration: null,
            Request: null,
            Response: null,
            SuppliedMessage: LiveLLMTraceCapture.CaptureMessage(message),
            ToolResult: null,
            Failure: null));
        return message;
    }

    /// <summary>
    /// Records a supplied tool result — always labelled as a simulated result, never executed gameplay — and
    /// returns the tool message to append to the next target request.
    /// </summary>
    /// <param name="call">The model-selected function call this result answers.</param>
    /// <param name="result">The simulated result value.</param>
    /// <returns>A tool-role message carrying the simulated result.</returns>
    public ChatMessage RecordSimulatedToolResult(FunctionCallContent call, object? result)
    {
        ArgumentNullException.ThrowIfNull(call);
        ThrowIfCompleted();

        if (ToolResults >= _bounds.MaxToolResults)
        {
            throw new LiveLLMScenarioException(
                LiveLLMFailureClassification.BoundExceeded,
                $"The scenario exceeded its declared bound of {_bounds.MaxToolResults} tool result(s).");
        }

        ToolResults++;
        _entries.Add(new LiveLLMTraceEntry(
            _entries.Count,
            LiveLLMTraceEntryKind.SuppliedToolResult,
            LiveLLMPurpose.Target,
            DateTimeOffset.UtcNow,
            Duration: null,
            Request: null,
            Response: null,
            SuppliedMessage: null,
            ToolResult: new LiveLLMToolResultRecord(
                LiveLLMTraceSanitiser.SanitiseText(call.CallId),
                LiveLLMTraceSanitiser.SanitiseText(call.Name),
                LiveLLMTraceCapture.SafeValue(result),
                Simulated: true),
            Failure: null));
        return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]);
    }

    private async Task<ChatResponse> SendCoreAsync(
        LiveLLMPurpose purpose,
        IChatClient client,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new LiveLLMScenarioException(
                LiveLLMFailureClassification.Malformed,
                "A scenario request carried no messages.");
        }

        if (_clock.Elapsed >= _bounds.Timeout)
        {
            throw new LiveLLMScenarioException(
                LiveLLMFailureClassification.Timeout,
                $"The scenario exceeded its declared timeout of {_bounds.Timeout.TotalMilliseconds:F0} ms before a request.");
        }

        LiveLLMRequestRecord requestRecord = LiveLLMTraceCapture.CaptureRequest(messages, options);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        // Snapshot elapsed <see cref="TimeSpan" />s rather than raw stopwatch ticks: on platforms where
        // <see cref="Stopwatch.Frequency" /> differs from <see cref="TimeSpan.TicksPerSecond" /> (Linux uses
        // nanosecond ticks), interpreting stopwatch ticks as TimeSpan ticks mis-scales recorded durations.
        TimeSpan startedElapsed = _clock.Elapsed;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token, cancellationToken);
            ChatResponse response = await client
                .GetResponseAsync(messages, options, linked.Token)
                .ConfigureAwait(false);

            _entries.Add(new LiveLLMTraceEntry(
                _entries.Count,
                LiveLLMTraceEntryKind.Exchange,
                purpose,
                startedAt,
                _clock.Elapsed - startedElapsed,
                requestRecord,
                LiveLLMTraceCapture.CaptureResponse(response),
                SuppliedMessage: null,
                ToolResult: null,
                Failure: null));
            return response;
        }
        catch (Exception ex)
        {
            _entries.Add(new LiveLLMTraceEntry(
                _entries.Count,
                LiveLLMTraceEntryKind.Exchange,
                purpose,
                startedAt,
                _clock.Elapsed - startedElapsed,
                requestRecord,
                Response: null,
                SuppliedMessage: null,
                ToolResult: null,
                Failure: LiveLLMFailureRecord.FromException(ex, TimeoutFired && ex is OperationCanceledException)));
            throw;
        }
    }

    internal void Complete()
    {
        _completed = true;
        _deadline.Dispose();
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The scenario context is no longer active; scenario code must not outlive its execution delegate.");
        }
    }
}

/// <summary>
/// Runs bounded, traced scenario executions for live LLM experiments.
/// </summary>
/// <remarks>
/// The executor is the execution stage of an experiment: it runs the caller's asynchronous scenario code
/// against borrowed clients — which it never disposes — and captures every request, response, supplied
/// continuation, and simulated tool result into a versioned evidence trace. Judging is a separate stage
/// performed over the captured evidence. Scenario failures and harness bound violations never throw out
/// of <see cref="ExecuteAsync" />; they complete the execution as failed with a safe classification, and
/// the partial trace is preserved.
/// </remarks>
public sealed class LiveLLMScenarioExecutor(IChatClient target, IChatClient? continuationGenerator = null)
{
    /// <summary>
    /// Executes one scenario and captures its evidence trace.
    /// </summary>
    /// <param name="scenarioName">Stable name recorded in the trace and used for artefact file names.</param>
    /// <param name="bounds">Declared bounds enforced for the whole execution.</param>
    /// <param name="scenario">Asynchronous scenario code driving a bounded number of requests.</param>
    /// <param name="cancellationToken">Token that cancels the whole execution.</param>
    /// <returns>The execution result with its complete — possibly partial — trace.</returns>
    public async Task<LiveLLMScenarioExecution> ExecuteAsync(
        string scenarioName,
        LiveLLMScenarioBounds bounds,
        Func<LiveLLMScenarioContext, Task> scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(scenario);
        bounds.Validate();

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        LiveLLMScenarioContext context = new(target, continuationGenerator, bounds, cancellationToken);
        LiveLLMFailureRecord? failure = null;
        try
        {
            await scenario(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = LiveLLMFailureRecord.FromException(ex, context.TimeoutFired);
        }
        finally
        {
            context.Complete();
        }

        LiveLLMTraceStatus status = failure is null
            ? LiveLLMTraceStatus.Completed
            : LiveLLMTraceStatus.Failed;
        // Defensive copy: the trace must not alias the context's live entry list, so its immutability is
        // structural rather than only behavioural.
        LiveLLMTrace trace = new(scenarioName, status, startedAt, DateTimeOffset.UtcNow, failure, [.. context.Entries]);
        return new LiveLLMScenarioExecution(
            scenarioName,
            failure is null ? LiveLLMScenarioExecutionStatus.Completed : LiveLLMScenarioExecutionStatus.Failed,
            failure,
            trace);
    }
}
