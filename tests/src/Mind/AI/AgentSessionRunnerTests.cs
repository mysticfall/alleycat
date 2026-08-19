using System.ClientModel;
using System.ClientModel.Primitives;
using AlleyCat.Mind.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the long-running agent-session protocol — transcript replay, whole-batch validation, interruption
/// injection, transport retry, and contained failure — without a network backend.
/// </summary>
public sealed class AgentSessionRunnerTests
{
    private const string Instructions = "Private test instructions.";

    private const string SpeakToolName = "speak";

    private static readonly IReadOnlyList<TimeSpan> _immediateRetryDelays =
    [
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
    ];

    /// <summary>
    /// Every request replays the complete ordered transcript and carries the strict tool-only options: required
    /// tool mode without a named function, no response format, and exactly the production tool inventory.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReplaysCompleteOrderedTranscriptWithToolOnlyOptionsOnEveryRequest()
    {
        ChatMessage runInput = new(ChatRole.User, "Begin. Participate in the scene using the available tools.");
        ChatMessage speakResponse = CreateCall(
            "speak-call",
            SpeakToolName,
            new Dictionary<string, object?> { ["speech"] = "Hello" });
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(speakResponse),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(speech.Add)], [runInput]);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["Hello"], speech);
        Assert.Equal(2, client.Requests.Count);
        foreach (ChatOptions options in client.Options)
        {
            Assert.Equal(Instructions, options.Instructions);
            RequiredChatToolMode toolMode = Assert.IsType<RequiredChatToolMode>(options.ToolMode);
            Assert.Null(toolMode.RequiredFunctionName);
            Assert.False(options.AllowMultipleToolCalls);
            Assert.Null(options.ResponseFormat);
            // No synthetic end-turn route exists in the session inventory (AI-002 TR-16).
            Assert.Equal(
                [SpeakToolName],
                options.Tools!.Cast<AIFunction>().Select(tool => tool.Name));
        }

        Assert.Single(client.Requests[0], runInput);
        IReadOnlyList<ChatMessage> replay = client.Requests[1];
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], replay.Select(message => message.Role));
        Assert.Same(runInput, replay[0]);
        Assert.Same(speakResponse, replay[1]);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(replay[2].Contents));
        Assert.Equal("speak-call", result.CallId);
        Assert.Equal("Spoken.", result.Result?.ToString());
    }

    /// <summary>
    /// A valid multi-call batch executes serially in provider order regardless of the provider preference, which
    /// must never make a valid batch fail local validation (AI-002 TR-13).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WithMultiCallBatch_ExecutesAllConfiguredActionsInOrder(
        bool allowMultipleToolCalls)
    {
        List<string> actions = [];
        AIFunction first = AIFunctionFactory.Create(() => actions.Add("first"), "first_action");
        AIFunction second = AIFunctionFactory.Create(
            (int count) => actions.Add($"second:{count}"),
            "second_action");
        ChatMessage batch = new(
            ChatRole.Assistant,
            [
                new FunctionCallContent("first-call", "first_action", new Dictionary<string, object?>()),
                new FunctionCallContent(
                    "second-call",
                    "second_action",
                    new Dictionary<string, object?> { ["count"] = 2 }),
            ]);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(batch),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [first, second],
            [],
            allowMultipleToolCalls: allowMultipleToolCalls);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["first", "second:2"], actions);
        Assert.All(client.Options, options => Assert.Equal(allowMultipleToolCalls, options.AllowMultipleToolCalls));
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// The session is long-running: repeated valid batches never hit a model-request or action bound
    /// (AI-002 TR-3).
    /// </summary>
    [Fact]
    public async Task RunAsync_WithRepeatedValidBatches_RunsIndefinitelyWithoutRequestBound()
    {
        const int rounds = 6;
        int invocationCount = 0;
        CancellationTokenSource lifetime = new();
        List<Func<CancellationToken, Task<ChatResponse>>> steps =
        [
            .. Enumerable.Range(0, rounds).Select(index => Respond(
                CreateCall(
                    $"round-{index}",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = $"Line {index}" }))),
            EndQuietly(lifetime),
        ];
        ScriptedSessionClient client = new([.. steps]);
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => invocationCount++)], []);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(rounds, invocationCount);
        Assert.Equal(rounds + 1, client.Requests.Count);
    }

    /// <summary>
    /// Invalid output is rejected as a whole before any valid-looking call can cause effects, without a model
    /// repair attempt or automatic request retry (AI-002 TR-11/12).
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task RunAsync_WithInvalidResponse_RejectsWholeBatchWithoutEffectsOrRetry(ChatResponse response)
    {
        int invocationCount = 0;
        ScriptedSessionClient client = new(Respond(response));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => invocationCount++)], []);

        AgentSessionException error = await Assert.ThrowsAsync<AgentSessionException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.Contains("invalid response shape", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Arguments that fail the tool's JSON schema — wrong types and missing required parameters — are rejected
    /// before execution like any other invalid response shape.
    /// </summary>
    [Theory]
    [MemberData(nameof(SchemaMismatchedArguments))]
    public async Task RunAsync_WithSchemaMismatchedArguments_RejectsWholeBatchWithoutEffects(
        IDictionary<string, object?> arguments)
    {
        int invocationCount = 0;
        ScriptedSessionClient client = new(Respond(CreateCall("schema-call", SpeakToolName, arguments)));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => invocationCount++)], []);

        _ = await Assert.ThrowsAsync<AgentSessionException>(() => runner.RunAsync(CancellationToken.None));

        Assert.Equal(0, invocationCount);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Reasoning content before a valid call is tolerated and skipped during validation, while remaining transient
    /// session protocol rather than player-visible text (AI-002 TR-53).
    /// </summary>
    [Fact]
    public async Task RunAsync_WithReasoningBeforeCall_ToleratesReasoningAndExecutesTheCall()
    {
        List<string> speech = [];
        ChatMessage reasoningResponse = new(
            ChatRole.Assistant,
            [
                new TextReasoningContent("private reasoning"),
                new FunctionCallContent(
                    "speak-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Hello" }),
            ]);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(reasoningResponse),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(speech.Add)], []);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["Hello"], speech);
        Assert.Equal(2, client.Requests.Count);
        // The reasoning is replayed inside the assistant message — never as ordinary assistant text.
        Assert.Equal(
            [ChatRole.Assistant, ChatRole.Tool],
            client.Requests[1].Select(message => message.Role));
        Assert.Same(reasoningResponse, client.Requests[1][0]);
        Assert.All(
            client.Requests[1],
            message => Assert.DoesNotContain("private reasoning", message.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reasoning text is logged at trace level only when the trace level is enabled and the dedicated
    /// <c>enableReasoningLogging</c> control is on (AI-002 TR-53).
    /// </summary>
    [Fact]
    public async Task RunAsync_WithReasoningContent_LogsReasoningAtTraceLevelOnlyWhenEnabled()
    {
        const string reasoning = "private reasoning";
        ChatResponse response = new(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent(reasoning),
                new FunctionCallContent(
                    "speak-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Hello" }),
            ]));

        CapturingLoggerFactory traceFactory = new(LogLevel.Trace);
        await RunQuietSessionAsync(response, traceFactory.CreateLogger("test"), enableReasoningLogging: true);
        Assert.Contains(traceFactory.Entries, entry =>
            entry.Level == LogLevel.Trace
            && entry.Message.Contains($"Reasoning: {reasoning}", StringComparison.Ordinal));

        CapturingLoggerFactory traceDisabledFactory = new(LogLevel.Trace);
        await RunQuietSessionAsync(response, traceDisabledFactory.CreateLogger("test"), enableReasoningLogging: false);
        Assert.DoesNotContain(traceDisabledFactory.Entries, entry =>
            entry.Message.Contains("Reasoning:", StringComparison.Ordinal));

        CapturingLoggerFactory infoFactory = new(LogLevel.Information);
        await RunQuietSessionAsync(response, infoFactory.CreateLogger("test"), enableReasoningLogging: true);
        Assert.DoesNotContain(infoFactory.Entries, entry =>
            entry.Message.Contains("Reasoning:", StringComparison.Ordinal));

        return;

        static async Task RunQuietSessionAsync(
            ChatResponse scripted,
            ILogger logger,
            bool enableReasoningLogging)
        {
            CancellationTokenSource lifetime = new();
            ScriptedSessionClient client = new(
                Respond(scripted),
                EndQuietly(lifetime));
            AgentSessionRunner runner = CreateRunner(
                client,
                [CreateSpeakFunction(_ => { })],
                [],
                logger: logger,
                enableReasoningLogging: enableReasoningLogging);
            await runner.RunAsync(lifetime.Token);
        }
    }

    /// <summary>
    /// Model calls may omit optional tool arguments: a defaulted parameter stays optional for validation and
    /// empty arguments execute the tool (AI-002 TR-31/36).
    /// </summary>
    [Fact]
    public async Task RunAsync_WithOmittedOptionalArguments_ExecutesTheTool()
    {
        List<float?> received = [];
        AIFunction optional = AIFunctionFactory.Create(
            (float? seconds = null) =>
            {
                received.Add(seconds);
                return "waited";
            },
            "wait");
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(CreateCall("optional-call", "wait")),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [optional], []);

        await runner.RunAsync(lifetime.Token);

        // Whole-batch schema validation accepts the empty argument set, and the tool binds the default.
        Assert.Equal([null], received);
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// Call identifiers remain unique across the complete session: a repeated identifier on a later response is
    /// rejected while earlier effects stay committed (AI-002 TR-12).
    /// </summary>
    [Fact]
    public async Task RunAsync_WithCallIDRepeatedOnLaterResponse_RejectsWithoutSecondEffect()
    {
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(CreateCall(
                "repeated-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "First" })),
            Respond(CreateCall(
                "repeated-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Second" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(speech.Add)], []);

        _ = await Assert.ThrowsAsync<AgentSessionException>(() => runner.RunAsync(lifetime.Token));

        Assert.Equal(["First"], speech);
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// A throwing tool surfaces its error through the tool result so the agent decides whether and how to retry;
    /// the session itself continues (AI-002 TR-42).
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenToolThrows_SurfacesErrorThroughToolResultAndContinues()
    {
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(CreateCall(
                "failing-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [AIFunctionFactory.Create(ThrowingSpeak, SpeakToolName)],
            []);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(2, client.Requests.Count);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(
            Assert.Single(client.Requests[1][1].Contents));
        Assert.Equal("failing-call", result.CallId);
        Assert.Equal("The action failed: Sensitive tool detail: Hello", result.Result?.ToString());
    }

    /// <summary>
    /// Interruption during model generation cancels the in-flight request, discards any partial assistant output,
    /// appends the injected user message, and resumes with a fresh request replaying the complete transcript
    /// (AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task SignalInterruption_DuringGeneration_CancelsRequestDiscardsPartialsAndInjectsBeforeFreshRequest()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> speech = [];
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            HoldUntilCancelledStep(requestStarted),
            Respond(CreateCall(
                "speak-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")]);

        Task runTask = runner.RunAsync(lifetime.Token);
        await requestStarted.Task;
        runner.SignalInterruption(injected);
        await runTask;

        Assert.Equal(["Hello"], speech);
        Assert.Equal(3, client.Requests.Count);
        // The fresh request carries the run input plus the injected message; the cancelled attempt left nothing.
        Assert.Equal(
            [ChatRole.User, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        Assert.Equal("Run input.", client.Requests[1][0].Text);
        Assert.Equal(injected, client.Requests[1][1].Text);
        // The final request replays the complete transcript including the post-interruption exchange.
        Assert.Equal(
            [ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
            client.Requests[2].Select(message => message.Role));
    }

    /// <summary>
    /// Interruption during a tool invocation lets the tool return its cut-short result, and the injected message
    /// still lands before the next request (AI-002 TR-39/40).
    /// </summary>
    [Fact]
    public async Task SignalInterruption_DuringToolPhase_ReturnsCutShortResultAndStillInjects()
    {
        const string injected = "Important scene events require your attention: something happened.";
        ControlledTool controlled = new();
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(CreateCall("controlled-call", ControlledTool.ToolName)),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [controlled.Function],
            [new ChatMessage(ChatRole.User, "Run input.")]);

        Task runTask = runner.RunAsync(lifetime.Token);
        await controlled.Started.Task;
        runner.SignalInterruption(injected);
        await runTask;

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(
            Assert.Single(client.Requests[1][2].Contents));
        Assert.Equal("controlled-call", result.CallId);
        Assert.Equal("The action was interrupted before it completed.", result.Result?.ToString());
        Assert.Equal(injected, client.Requests[1][3].Text);
        Assert.False(controlled.Completed);
    }

    /// <summary>
    /// Multiple interruptions signalled while one request is in flight drain in signalling order before the fresh
    /// request.
    /// </summary>
    [Fact]
    public async Task SignalInterruption_QueuedDuringOneGeneration_DrainsInOrderBeforeFreshRequest()
    {
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            HoldUntilCancelledStep(requestStarted),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        Task runTask = runner.RunAsync(lifetime.Token);
        await requestStarted.Task;
        runner.SignalInterruption("first notice");
        runner.SignalInterruption("second notice");
        await runTask;

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            ["first notice", "second notice"],
            client.Requests[1].Select(message => message.Text));
        Assert.Equal(
            [ChatRole.User, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
    }

    /// <summary>
    /// Signalling after the session ended is a quiet no-op.
    /// </summary>
    [Fact]
    public async Task SignalInterruption_AfterSessionEnded_IsAQuietNoOp()
    {
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        await runner.RunAsync(lifetime.Token);
        runner.SignalInterruption("late notice");

        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Signalling requires a nonblank message.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SignalInterruption_WithBlankMessage_FailsClearly(string? message)
    {
        ScriptedSessionClient client = new();
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        _ = Assert.ThrowsAny<ArgumentException>(() => runner.SignalInterruption(message!));
        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// Transient transport failures — network, I/O, timeout, and retryable provider statuses — are retried
    /// transparently: never surfaced to the agent as a tool result or transcript entry (AI-002 TR-43).
    /// </summary>
    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task RunAsync_WithTransientFailure_RetriesTransparentlyWithoutAgentVisibleSurface(
        Exception failure)
    {
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            FailStep(failure),
            Respond(CreateCall(
                "speak-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")],
            retryDelays: _immediateRetryDelays);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["Hello"], speech);
        Assert.Equal(3, client.Requests.Count);
        // The failed attempt added nothing to the transcript: the retry replays the identical run input.
        Assert.Single(client.Requests[1], client.Requests[0][0]);
        Assert.DoesNotContain(
            failure.Message,
            string.Join(
                '\n',
                client.Requests[2]
                    .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
                    .Select(static result => result.Result?.ToString())),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A provider timeout — a task cancellation on a linked token that is not the phase token — is classified as a
    /// transient transport failure, never as an expected interruption: the retry logs a transient warning, never
    /// takes the interruption path, and lands no injected message (AI-002 TR-43 versus TR-41).
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenProviderTimeoutRecovers_RetriesAsTransientFailureWithoutInterruptionSemantics()
    {
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            FailStep(CreateTransportTimeoutCancellation()),
            Respond(CreateCall(
                "speak-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        CapturingLoggerFactory loggerFactory = new(LogLevel.Debug);
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")],
            logger: loggerFactory.CreateLogger("test"),
            retryDelays: _immediateRetryDelays);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["Hello"], speech);
        Assert.Equal(3, client.Requests.Count);
        // The retry replayed the identical transcript: no interruption semantics landed an injected message.
        Assert.Single(client.Requests[1], client.Requests[0][0]);
        Assert.Contains(loggerFactory.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("failed transiently", StringComparison.Ordinal));
        Assert.DoesNotContain(loggerFactory.Entries, entry =>
            entry.Message.Contains("interrupted", StringComparison.Ordinal));
    }

    /// <summary>
    /// Retry exhaustion ends the session through the contained failure path: one contained exception wrapping
    /// the final transport failure, after exactly the configured number of retries (AI-002 TR-43).
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTransientFailuresExhaustRetries_EndsSessionThroughContainedFailure()
    {
        HttpRequestException failure = new("connection reset");
        ScriptedSessionClient client = new(
            FailStep(failure),
            FailStep(failure),
            FailStep(failure),
            FailStep(failure));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(_ => { })],
            [],
            retryDelays: _immediateRetryDelays);

        AgentSessionException error = await Assert.ThrowsAsync<AgentSessionException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.Contains("exhausted its transport retries", error.Message, StringComparison.Ordinal);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(4, client.Requests.Count);
    }

    /// <summary>
    /// Persistent provider timeouts — task cancellations on a linked token that is not the phase token — exhaust
    /// the bounded transport retries into the contained session end instead of looping on interruption fresh
    /// requests (AI-002 TR-43 versus TR-41).
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenProviderTimeoutsPersist_ExhaustsRetriesIntoContainedSessionEnd()
    {
        OperationCanceledException failure = CreateTransportTimeoutCancellation();
        ScriptedSessionClient client = new(
            FailStep(failure),
            FailStep(failure),
            FailStep(failure),
            FailStep(failure));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(_ => { })],
            [],
            retryDelays: _immediateRetryDelays);

        AgentSessionException error = await Assert.ThrowsAsync<AgentSessionException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.Contains("exhausted its transport retries", error.Message, StringComparison.Ordinal);
        Assert.Same(failure, error.InnerException);
        // The same request retried to exhaustion; the loop never escalated to unbounded fresh requests.
        Assert.Equal(4, client.Requests.Count);
    }

    /// <summary>
    /// Non-retryable provider statuses end the session through the contained failure path on the first attempt,
    /// like every non-transient failure.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task RunAsync_WithNonRetryableProviderStatus_FailsContainedWithoutRetry(int status)
    {
        ScriptedSessionClient client = new(FailStep(CreateProviderException(status)));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(_ => { })],
            [],
            retryDelays: _immediateRetryDelays);

        AgentSessionException error = await Assert.ThrowsAsync<AgentSessionException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.DoesNotContain("exhausted", error.Message, StringComparison.Ordinal);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Node-lifetime cancellation before the first request ends the session quietly without issuing any request.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithPreCancelledLifetime_IssuesNoRequest()
    {
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);
        lifetime.Cancel();

        await runner.RunAsync(lifetime.Token);

        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// Node-lifetime cancellation during an in-flight request ends the session quietly — the cancellation is
    /// never a backend failure and is never retried (AI-002 TR-44).
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenLifetimeCancelsDuringGeneration_EndsQuietlyWithoutRetry()
    {
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            HoldUntilCancelledStep(requestStarted),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        Task runTask = runner.RunAsync(lifetime.Token);
        await requestStarted.Task;
        lifetime.Cancel();
        await runTask;

        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Node-lifetime cancellation during a retry delay ends the session quietly instead of retrying.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenLifetimeCancelsDuringRetryDelay_EndsQuietlyWithoutRetry()
    {
        ScriptedSessionClient client = new(FailStep(new HttpRequestException("connection reset")));
        using CancellationTokenSource lifetime = new();
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(_ => { })],
            [],
            retryDelays: [Timeout.InfiniteTimeSpan]);
        Task runTask = runner.RunAsync(lifetime.Token);
        await WaitForAttemptsAsync(client, attemptCount: 1);
        lifetime.Cancel();

        await runTask;

        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Duplicate production function names and non-function tools are rejected before any request.
    /// </summary>
    [Fact]
    public void Ctor_WithDuplicateOrNonFunctionTools_RejectsBeforeAnyRequest()
    {
        ScriptedSessionClient client = new();
        AIFunction first = AIFunctionFactory.Create(() => { }, "duplicate_action");
        AIFunction second = AIFunctionFactory.Create(() => { }, "duplicate_action");

        _ = Assert.Throws<AgentSessionException>(() => CreateRunner(client, [first, second], []));
        _ = Assert.Throws<AgentSessionException>(
            () => CreateRunner(client, [new NonFunctionTool("opaque_tool")], []));

        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// Gets representative malformed, text-bearing, mixed, duplicate-identifier, unknown-tool, and
    /// schema-invalid responses.
    /// </summary>
    public static TheoryData<ChatResponse> InvalidResponses =>
    [
        new ChatResponse(),
        new ChatResponse(new ChatMessage(ChatRole.Assistant, "ordinary text")),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                ValidSpeakCall("valid-call", "Must not run"),
                new TextContent("mixed text"),
            ])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                ValidSpeakCall("duplicate", "One"),
                ValidSpeakCall("duplicate", "Two"),
            ])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call", "unknown_action", new Dictionary<string, object?>())])),
        new ChatResponse(new ChatMessage(
            ChatRole.User,
            [new FunctionCallContent("call", SpeakToolName, new Dictionary<string, object?>())])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(" ", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "Hello" })])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new ErrorContent("refusal or adapter error")])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "Hello" })
            {
                Exception = new InvalidOperationException("Malformed adapter arguments."),
            }])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new TextReasoningContent("reasoning without any call")])),
    ];

    /// <summary>
    /// Gets argument dictionaries that fail the production tool's JSON schema.
    /// </summary>
    public static TheoryData<IDictionary<string, object?>> SchemaMismatchedArguments =>
    [
        new Dictionary<string, object?> { ["speech"] = 123 },
        new Dictionary<string, object?>(),
    ];

    /// <summary>
    /// Gets transport failures the runtime must retry transparently.
    /// </summary>
    public static TheoryData<Exception> TransientFailures =>
    [
        new HttpRequestException("network unreachable"),
        new IOException("connection dropped"),
        new TimeoutException("provider timed out"),
        CreateProviderException(408),
        CreateProviderException(429),
        CreateProviderException(503),
        new TaskCanceledException("The provider request timed out.", new TimeoutException("timed out")),
        CreateTransportTimeoutCancellation(),
    ];

    private static ClientResultException CreateProviderException(int status)
        => new($"Provider returned status {status}.", new StubPipelineResponse(status));

    /// <summary>
    /// Creates the provider/HTTP timeout shape: an operation cancellation on an unrelated, already-cancelled
    /// token — a linked timeout token rather than the runner's phase token — carrying no inner
    /// <see cref="TimeoutException" />.
    /// </summary>
    private static OperationCanceledException CreateTransportTimeoutCancellation()
    {
        CancellationTokenSource timeoutCancellation = new();
        timeoutCancellation.Cancel();
        return new OperationCanceledException(
            "The request was cancelled by the provider timeout.",
            timeoutCancellation.Token);
    }

    private static FunctionCallContent ValidSpeakCall(string callId, string speech)
        => new(
            callId,
            SpeakToolName,
            new Dictionary<string, object?> { ["speech"] = speech });

    private static AIFunction CreateSpeakFunction(Action<string> action)
        => AIFunctionFactory.Create(
            (string speech) =>
            {
                action(speech);
                return "Spoken.";
            },
            SpeakToolName,
            "Speak aloud.");

    private static string ThrowingSpeak(string speech)
        => throw new InvalidOperationException($"Sensitive tool detail: {speech}");

    private static ChatMessage CreateCall(
        string callId,
        string name,
        IDictionary<string, object?>? arguments = null)
        => new(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, arguments ?? new Dictionary<string, object?>())]);

    private static Func<CancellationToken, Task<ChatResponse>> Respond(ChatMessage message)
        => _ => Task.FromResult(new ChatResponse(message));

    private static Func<CancellationToken, Task<ChatResponse>> Respond(ChatResponse response)
        => _ => Task.FromResult(response);

    private static Func<CancellationToken, Task<ChatResponse>> FailStep(Exception exception)
        => _ => Task.FromException<ChatResponse>(exception);

    /// <summary>
    /// Creates a step that ends the session quietly: the lifetime token cancels and the request throws the
    /// expected cancellation the runtime never treats as a backend failure.
    /// </summary>
    private static Func<CancellationToken, Task<ChatResponse>> EndQuietly(CancellationTokenSource lifetime)
        => cancellationToken =>
        {
            lifetime.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        };

    private static Func<CancellationToken, Task<ChatResponse>> HoldUntilCancelledStep(
        TaskCompletionSource requestStarted)
        => async cancellationToken =>
        {
            _ = requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ChatResponse();
        };

    private static AgentSessionRunner CreateRunner(
        ScriptedSessionClient client,
        IList<AITool> tools,
        IReadOnlyList<ChatMessage> runInputMessages,
        bool allowMultipleToolCalls = false,
        ILogger? logger = null,
        bool enableReasoningLogging = true,
        IReadOnlyList<TimeSpan>? retryDelays = null)
        => new(
            client,
            Instructions,
            runInputMessages,
            tools,
            allowMultipleToolCalls,
            logger ?? NullLogger.Instance,
            enableReasoningLogging,
            retryDelays);

    private static async Task WaitForAttemptsAsync(ScriptedSessionClient client, int attemptCount)
    {
        for (int index = 0; index < 500 && client.Requests.Count < attemptCount; index++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(attemptCount, client.Requests.Count);
    }

    /// <summary>
    /// Minimal response surface for <see cref="ClientResultException" /> status classification; only
    /// <see cref="Status" /> is consulted by the retry policy.
    /// </summary>
    private sealed class StubPipelineResponse(int status) : PipelineResponse
    {
        public override int Status => status;

        public override BinaryData Content => BinaryData.Empty;

        public override Stream? ContentStream
        {
            get => Stream.Null;
            set => _ = value;
        }

        public override string ReasonPhrase => "Stub";

        protected override PipelineResponseHeaders HeadersCore => null!;

        public override BinaryData BufferContent(CancellationToken cancellationToken) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Content);

        public override void Dispose()
        {
        }
    }

    private sealed class NonFunctionTool(string name) : AITool
    {
        public override string Name => name;
    }

    /// <summary>
    /// Deterministic in-flight tool whose completion the test controls, so an interruption can be signalled while
    /// the tool is provably executing.
    /// </summary>
    private sealed class ControlledTool
    {
        public const string ToolName = "controlled_action";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Completed
        {
            get; private set;
        }

        public AIFunction Function => AIFunctionFactory.Create(InvokeAsync, ToolName);

        private async Task<string> InvokeAsync(CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Completed = true;
            return "controlled result";
        }
    }

    private sealed class ScriptedSessionClient(params Func<CancellationToken, Task<ChatResponse>>[] steps)
        : IChatClient
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new(steps);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            Options.Add(Assert.IsType<ChatOptions>(options));
            return _steps.Count == 0
                ? throw new InvalidOperationException("The scripted session client ran out of scripted steps.")
                : _steps.Dequeue()(cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLoggerFactory(LogLevel minimumLevel) : ILoggerFactory
    {
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries => _entries;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(categoryName, minimumLevel, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(
        string categoryName,
        LogLevel minimumLevel,
        List<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed record CapturedLogEntry(string CategoryName, LogLevel Level, string Message);
}
