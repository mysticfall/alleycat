using System.ClientModel;
using System.ClientModel.Primitives;
using AlleyCat.Mind.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the long-running agent-session protocol — transcript replay, whole-batch validation, ordinary boundary
/// injection, fresh-turn invalidation, transport retry, and contained failure — without a network backend.
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

    private static readonly IInvalidResponseRecoveryPolicy _immediateInvalidResponseRecoveryPolicy =
        new InvalidResponseRecoveryPolicy(InvalidResponseRecoveryPolicy.DefaultConsecutiveFailureBudget, [TimeSpan.Zero]);

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
    /// Invalid output is rejected as a whole before any valid-looking call can cause effects. Consecutive invalid
    /// responses exhaust the bounded recovery budget without appending model-visible protocol entries.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task RunAsync_WithInvalidResponse_ExhaustsRecoveryWithoutEffects(ChatResponse response)
    {
        int invocationCount = 0;
        ScriptedSessionClient client = new(Respond(response), Respond(response), Respond(response));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => invocationCount++)], []);

        AgentSessionException error = await Assert.ThrowsAsync<AgentSessionException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.Contains("invalid response recovery budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
        Assert.Equal(3, client.Requests.Count);
    }

    /// <summary>
    /// A malformed response is discarded and a later valid response resumes the same session without replaying any
    /// assistant content or tool effects from the rejected batch.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithInvalidThenValidResponse_ContinuesWithoutInvalidBatchEffects()
    {
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ordinary text"))),
            Respond(CreateCall("valid-call", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")]);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["Hello"], speech);
        Assert.Equal(3, client.Requests.Count);
        Assert.Single(client.Requests[1], client.Requests[0][0]);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
            client.Requests[2].Select(message => message.Role));
    }

    /// <summary>
    /// A fully valid response resets the consecutive-invalid streak, so separated malformed batches do not combine
    /// into an exhausted recovery budget.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithValidResponseBetweenInvalidBatches_ResetsRecoveryStreak()
    {
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ChatResponse invalid = new(new ChatMessage(ChatRole.Assistant, "ordinary text"));
        ScriptedSessionClient client = new(
            Respond(invalid),
            Respond(invalid),
            Respond(CreateCall("first-valid", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "First" })),
            Respond(invalid),
            Respond(invalid),
            Respond(CreateCall("second-valid", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "Second" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(speech.Add)], []);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["First", "Second"], speech);
        Assert.Equal(7, client.Requests.Count);
    }

    /// <summary>
    /// A bounded run of malformed batches ends through the contained session failure after the established three
    /// consecutive invalid-response limit.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithConsecutiveInvalidResponses_ExhaustsRecoveryBudget()
    {
        ChatResponse invalid = new(new ChatMessage(ChatRole.Assistant, "ordinary text"));
        ScriptedSessionClient client = new(Respond(invalid), Respond(invalid), Respond(invalid));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        AgentSessionException error = await Assert.ThrowsAsync<AgentSessionException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.Contains("3 consecutive invalid response shapes", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, client.Requests.Count);
    }

    /// <summary>
    /// The malformed-response budget is supplied by its own policy, rather than inheriting the transport retry
    /// count or its delays.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithConfiguredInvalidResponseRecoveryPolicy_UsesIndependentBudget()
    {
        ChatResponse invalid = new(new ChatMessage(ChatRole.Assistant, "ordinary text"));
        ScriptedSessionClient client = new(Respond(invalid), Respond(invalid));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(_ => { })],
            [],
            retryDelays: _immediateRetryDelays,
            invalidResponseRecoveryPolicy: new InvalidResponseRecoveryPolicy(2, [TimeSpan.Zero]));

        AgentSessionException error = await Assert.ThrowsAsync<AgentSessionException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.Contains("2 consecutive invalid response shapes", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// Call-ID validation is transactional: a duplicate ID in a rejected batch does not reserve either ID for the
    /// valid response that follows it.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithInvalidDuplicateCallIDs_DoesNotConsumeBatchIDs()
    {
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [
                    ValidSpeakCall("reusable-call", "Must not run"),
                    ValidSpeakCall("reusable-call", "Also must not run"),
                ]))),
            Respond(CreateCall(
                "reusable-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Runs once" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(speech.Add)], []);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["Runs once"], speech);
        Assert.Equal(3, client.Requests.Count);
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
        ChatMessage response = CreateCall("schema-call", SpeakToolName, arguments);
        ScriptedSessionClient client = new(Respond(response), Respond(response), Respond(response));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => invocationCount++)], []);

        _ = await Assert.ThrowsAsync<AgentSessionException>(() => runner.RunAsync(CancellationToken.None));

        Assert.Equal(0, invocationCount);
        Assert.Equal(3, client.Requests.Count);
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
            Respond(CreateCall(
                "repeated-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Second" })),
            Respond(CreateCall(
                "repeated-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Second" })));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(speech.Add)], []);

        _ = await Assert.ThrowsAsync<AgentSessionException>(() => runner.RunAsync(lifetime.Token));

        Assert.Equal(["First"], speech);
        Assert.Equal(4, client.Requests.Count);
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
    /// An ordinary notable observation during model generation never cancels the in-flight request: the response
    /// and its tools complete naturally, and exactly one injected user message lands in the naturally-next request
    /// (AI-002 TR-39).
    /// </summary>
    [Fact]
    public async Task QueueInjection_DuringGeneration_CompletesResponseAndToolsAndInjectsAtNextBoundary()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> speech = [];
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            HoldUntilReleasedStep(
                requestStarted,
                releaseGeneration,
                CreateCall("speak-call", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")]);

        Task runTask = runner.RunAsync(lifetime.Token);
        await requestStarted.Task;
        runner.QueueInjection(injected);
        _ = releaseGeneration.TrySetResult();
        await runTask;

        // No cancellation: the response validated, its tool ran, and no extra request left the session.
        Assert.Equal(["Hello"], speech);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        Assert.Equal("Run input.", client.Requests[1][0].Text);
        Assert.Equal(injected, client.Requests[1][3].Text);
    }

    /// <summary>
    /// An ordinary notable observation during a tool invocation never cancels the tool: it completes naturally
    /// with its real result, and the injected message still lands before the next request (AI-002 TR-39).
    /// </summary>
    [Fact]
    public async Task QueueInjection_DuringToolPhase_CompletesTheToolNaturallyAndInjectsAtNextBoundary()
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
        runner.QueueInjection(injected);
        Assert.False(controlled.Completed);
        _ = controlled.Release.TrySetResult();
        await runTask;

        Assert.True(controlled.Completed, "An ordinary injection must never cancel an in-flight tool.");
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(
            Assert.Single(client.Requests[1][2].Contents));
        Assert.Equal("controlled-call", result.CallId);
        Assert.Equal("controlled result", result.Result?.ToString());
        Assert.Equal(injected, client.Requests[1][3].Text);
    }

    /// <summary>
    /// Multiple ordinary payloads queued during one generation coalesce in FIFO order into exactly one injected
    /// user message, and the session issues no request beyond its normal next request (AI-002 TR-39).
    /// </summary>
    [Fact]
    public async Task QueueInjection_MultipleOrdinaryPayloads_CoalesceFIFOIntoOneInjectedMessage()
    {
        List<string> speech = [];
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            HoldUntilReleasedStep(
                requestStarted,
                releaseGeneration,
                CreateCall("speak-call", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")]);

        Task runTask = runner.RunAsync(lifetime.Token);
        await requestStarted.Task;
        runner.QueueInjection("first notice");
        runner.QueueInjection("second notice");
        _ = releaseGeneration.TrySetResult();
        await runTask;

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        Assert.Equal("first notice\nsecond notice", client.Requests[1][3].Text);
    }

    /// <summary>
    /// A fresh observation during model generation cancels the in-flight request, discards partial assistant
    /// output, appends the injected user message, and resumes with a fresh request replaying the complete
    /// transcript (AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_DuringGeneration_CancelsRequestDiscardsPartialsAndInjectsBeforeFreshRequest()
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
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.QueueFreshInjection(injected);
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
    /// A non-cooperative provider that returns a valid-looking response after its generation was invalidated has
    /// that late response discarded whole: it is never validated or executed, and the fresh request carries only
    /// the injection (AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_DuringGeneration_DiscardsAResponseReturnedAfterCancellation()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> speech = [];
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            HoldUntilReleasedStep(
                requestStarted,
                releaseGeneration,
                CreateCall("late-call", SpeakToolName, new Dictionary<string, object?> { ["speech"] = "Never runs" })),
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
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.QueueFreshInjection(injected);
        _ = releaseGeneration.TrySetResult();
        await runTask;

        Assert.Equal(["Hello"], speech);
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(
            ["Run input.", injected],
            client.Requests[1].Select(message => message.Text));
    }

    /// <summary>
    /// A provider which returns malformed output despite a fresh-turn phase cancellation cannot convert the
    /// invalidated generation into an invalid-response recovery request.
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_WhenCancelledGenerationReturnsInvalidResponse_DiscardsItBeforeRecovery()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        AgentSessionRunner? runner = null;
        ScriptedSessionClient client = new(
            _ =>
            {
                runner!.InvalidateForFreshTurn(expectFreshInjection: true);
                runner.QueueFreshInjection(injected);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ordinary text")));
            },
            Respond(CreateCall(
                "speak-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")]);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(["Hello"], speech);
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(
            ["Run input.", injected],
            client.Requests[1].Select(message => message.Text));
    }

    /// <summary>
    /// Fresh invalidation during the first call of a validated multi-call batch cancels that call co-operatively,
    /// never invokes the remaining calls, and appends the complete assistant exchange with exactly one
    /// protocol-valid result per call ID — every result without a natural outcome carries the canonical
    /// cancellation wording — followed by the injected message before the single replacement request
    /// (AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_DuringFirstCallOfMultiCallBatch_CancelsCallSkipsRemainingAndSynthesisesOneResultPerCallID()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> actions = [];
        ControlledTool controlled = new();
        AIFunction second = AIFunctionFactory.Create(() => actions.Add("second"), "second_action");
        ChatMessage batch = new(
            ChatRole.Assistant,
            [
                new FunctionCallContent("first-call", ControlledTool.ToolName, new Dictionary<string, object?>()),
                new FunctionCallContent("second-call", "second_action", new Dictionary<string, object?>()),
            ]);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(batch),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [controlled.Function, second],
            [new ChatMessage(ChatRole.User, "Run input.")],
            allowMultipleToolCalls: true);

        Task runTask = runner.RunAsync(lifetime.Token);
        await controlled.Started.Task;
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.QueueFreshInjection(injected);
        await runTask;

        Assert.Empty(actions);
        Assert.False(controlled.Completed, "The active call must be cancelled co-operatively.");
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        FunctionResultContent firstResult = Assert.IsType<FunctionResultContent>(client.Requests[1][2].Contents[0]);
        FunctionResultContent secondResult = Assert.IsType<FunctionResultContent>(client.Requests[1][2].Contents[1]);
        Assert.Equal("first-call", firstResult.CallId);
        Assert.Equal("second-call", secondResult.CallId);
        Assert.Equal("The action was cancelled before it completed.", firstResult.Result?.ToString());
        Assert.Equal("The action was cancelled before it completed.", secondResult.Result?.ToString());
        Assert.Equal(injected, client.Requests[1][3].Text);
    }

    /// <summary>
    /// Fresh invalidation landing in the inter-call gap — after the active call completed but before the runner
    /// started the next — never starts the remaining stale call (AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_InTheInterCallGap_NeverStartsTheNextCall()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> actions = [];
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AIFunction first = AIFunctionFactory.Create(
            async () =>
            {
                _ = firstStarted.TrySetResult();
                await releaseFirst.Task;
                return "first natural result";
            },
            "first_action");
        AIFunction second = AIFunctionFactory.Create(() => actions.Add("second"), "second_action");
        ChatMessage batch = new(
            ChatRole.Assistant,
            [
                new FunctionCallContent("first-call", "first_action", new Dictionary<string, object?>()),
                new FunctionCallContent("second-call", "second_action", new Dictionary<string, object?>()),
            ]);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(batch),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [first, second],
            [new ChatMessage(ChatRole.User, "Run input.")],
            allowMultipleToolCalls: true);

        Task runTask = runner.RunAsync(lifetime.Token);
        await firstStarted.Task;
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.QueueFreshInjection(injected);
        _ = releaseFirst.TrySetResult();
        await runTask;

        Assert.Empty(actions);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        FunctionResultContent firstResult = Assert.IsType<FunctionResultContent>(client.Requests[1][2].Contents[0]);
        FunctionResultContent secondResult = Assert.IsType<FunctionResultContent>(client.Requests[1][2].Contents[1]);
        // The first call crossed its completion boundary: its natural result is retained without rollback.
        Assert.Equal("first natural result", firstResult.Result?.ToString());
        Assert.Equal("The action was cancelled before it completed.", secondResult.Result?.ToString());
    }

    /// <summary>
    /// Effects a non-cooperative tool committed before invalidation remain committed: its natural result is
    /// retained — never rolled back or misrepresented — while the remaining calls are still skipped with canonical
    /// cancellation results (AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_AfterCommittedEffects_RetainsTheCommittedNaturalResult()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> actions = [];
        CommittingTool committing = new();
        AIFunction second = AIFunctionFactory.Create(() => actions.Add("second"), "second_action");
        ChatMessage batch = new(
            ChatRole.Assistant,
            [
                new FunctionCallContent("commit-call", CommittingTool.ToolName, new Dictionary<string, object?>()),
                new FunctionCallContent("second-call", "second_action", new Dictionary<string, object?>()),
            ]);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(batch),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [committing.Function, second],
            [new ChatMessage(ChatRole.User, "Run input.")],
            allowMultipleToolCalls: true);

        Task runTask = runner.RunAsync(lifetime.Token);
        await committing.Started.Task;
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.QueueFreshInjection(injected);
        _ = committing.Commit.TrySetResult();
        await runTask;

        Assert.True(committing.Committed, "A committed effect must never be rolled back.");
        Assert.Empty(actions);
        FunctionResultContent committedResult = Assert.IsType<FunctionResultContent>(
            client.Requests[1][2].Contents[0]);
        FunctionResultContent skippedResult = Assert.IsType<FunctionResultContent>(
            client.Requests[1][2].Contents[1]);
        Assert.Equal("commit-call", committedResult.CallId);
        Assert.Equal("committed result", committedResult.Result?.ToString());
        Assert.Equal("The action was cancelled before it completed.", skippedResult.Result?.ToString());
    }

    /// <summary>
    /// A tool completing at the same moment its batch is invalidated produces exactly one deterministic valid
    /// result for its call ID — its natural result — with no duplicate invocation, while every remaining call is
    /// skipped (AI-002 TR-40).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_RacingToolCompletion_ProducesExactlyOneResultWithoutDuplicateInvocation()
    {
        const string injected = "Important scene events require your attention: something happened.";
        List<string> actions = [];
        AgentSessionRunner? runner = null;
        int invocations = 0;
        AIFunction racing = AIFunctionFactory.Create(
            () =>
            {
                invocations++;
                // The fresh invalidation lands as the tool's final act — exactly at its completion boundary.
                runner!.InvalidateForFreshTurn(expectFreshInjection: true);
                runner.QueueFreshInjection(injected);
                return "natural race result";
            },
            "racing_action");
        AIFunction second = AIFunctionFactory.Create(() => actions.Add("second"), "second_action");
        ChatMessage batch = new(
            ChatRole.Assistant,
            [
                new FunctionCallContent("race-call", "racing_action", new Dictionary<string, object?>()),
                new FunctionCallContent("second-call", "second_action", new Dictionary<string, object?>()),
            ]);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(batch),
            EndQuietly(lifetime));
        runner = CreateRunner(
            client,
            [racing, second],
            [new ChatMessage(ChatRole.User, "Run input.")],
            allowMultipleToolCalls: true);

        await runner.RunAsync(lifetime.Token);

        Assert.Equal(1, invocations);
        Assert.Empty(actions);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        Assert.Equal(2, client.Requests[1][2].Contents.Count);
        FunctionResultContent racingResult = Assert.IsType<FunctionResultContent>(client.Requests[1][2].Contents[0]);
        Assert.Equal("race-call", racingResult.CallId);
        Assert.Equal("natural race result", racingResult.Result?.ToString());
        Assert.Equal(injected, client.Requests[1][3].Text);
    }

    /// <summary>
    /// A wait-owned fresh invalidation — signalled while the wait is the in-flight active phase — records the stale
    /// latch without cancelling that phase: the wait completes naturally with its delivery, the batch's remaining
    /// calls are skipped with canonical cancellation results, and the single replacement request carries the
    /// wait's natural result without any injected duplicate (AI-002 TR-40/41).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_WithoutCancellingActivePhase_CompletesWaitNaturallyAndSkipsRemainingCalls()
    {
        List<string> actions = [];
        WaitLikeTool waitLike = new();
        AIFunction second = AIFunctionFactory.Create(() => actions.Add("second"), "second_action");
        ChatMessage batch = new(
            ChatRole.Assistant,
            [
                new FunctionCallContent("wait-call", WaitLikeTool.ToolName, new Dictionary<string, object?>()),
                new FunctionCallContent("second-call", "second_action", new Dictionary<string, object?>()),
            ]);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            Respond(batch),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [waitLike.Function, second],
            [new ChatMessage(ChatRole.User, "Run input.")],
            allowMultipleToolCalls: true);

        Task runTask = runner.RunAsync(lifetime.Token);
        await waitLike.Started.Task;
        // The wait-owned fresh signal arrives mid-wait: the stale latch is recorded without cancelling the wait.
        runner.InvalidateForFreshTurn(expectFreshInjection: false, cancelActivePhase: false);
        _ = waitLike.Release.TrySetResult();
        await runTask;

        Assert.True(waitLike.CompletedNaturally, "The active wait must complete naturally after the no-cancel invalidation.");
        Assert.False(waitLike.ObservedCancellation, "A wait-owned fresh invalidation must not cancel the active phase token.");
        Assert.Empty(actions);
        Assert.Equal(2, client.Requests.Count);
        // The replacement request replays the exchange with the wait's natural result and no injected duplicate.
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
            client.Requests[1].Select(message => message.Role));
        FunctionResultContent waitResult = Assert.IsType<FunctionResultContent>(client.Requests[1][2].Contents[0]);
        FunctionResultContent secondResult = Assert.IsType<FunctionResultContent>(client.Requests[1][2].Contents[1]);
        Assert.Equal("wait-call", waitResult.CallId);
        Assert.Equal("wait delivered its window", waitResult.Result?.ToString());
        Assert.Equal("second-call", secondResult.CallId);
        Assert.Equal("The action was cancelled before it completed.", secondResult.Result?.ToString());
    }

    /// <summary>
    /// Pending ordinary and fresh payloads coalesce in FIFO order into exactly one injected user message carried
    /// by the single fresh replacement request (AI-002 TR-39/40).
    /// </summary>
    [Fact]
    public async Task PendingOrdinaryAndFreshPayloads_CoalesceFIFOIntoOneInjectedMessageOnTheFreshRequest()
    {
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
        runner.QueueInjection("ordinary notice");
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.QueueFreshInjection("fresh notice");
        await runTask;

        Assert.Equal(3, client.Requests.Count);
        Assert.Equal(
            [ChatRole.User, ChatRole.User],
            client.Requests[1].Select(message => message.Role));
        Assert.Equal("ordinary notice\nfresh notice", client.Requests[1][1].Text);
    }

    /// <summary>
    /// Node-lifetime cancellation racing a fresh invalidation wins: the rendering barrier observes it, the session
    /// ends quietly, and no replacement request is issued (AI-002 TR-44 versus TR-40).
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenLifetimeCancelsRacingFreshInvalidation_EndsQuietlyWithoutReplacementRequest()
    {
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(HoldUntilCancelledStep(requestStarted));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        Task runTask = runner.RunAsync(lifetime.Token);
        await requestStarted.Task;
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        lifetime.Cancel();
        await runTask;

        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Queuing or invalidating after the session ended is a quiet no-op.
    /// </summary>
    [Fact]
    public async Task QueueInjection_AfterSessionEnded_IsAQuietNoOp()
    {
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        await runner.RunAsync(lifetime.Token);
        runner.QueueInjection("late notice");
        runner.QueueFreshInjection("late notice");
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.AbandonFreshInjection();

        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Queueing an injected message — ordinary or fresh — requires a nonblank payload.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void QueueInjection_WithBlankMessage_FailsClearly(string? message)
    {
        ScriptedSessionClient client = new();
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        _ = Assert.ThrowsAny<ArgumentException>(() => runner.QueueInjection(message!));
        _ = Assert.ThrowsAny<ArgumentException>(() => runner.QueueFreshInjection(message!));
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
    /// Lifetime cancellation that arrives with an invalid provider payload wins over response recovery, so no fresh
    /// request is made after the session lifetime ends.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenLifetimeCancelsWithInvalidResponse_EndsQuietlyWithoutRecoveryRequest()
    {
        using CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(_ =>
        {
            lifetime.Cancel();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ordinary text")));
        });
        AgentSessionRunner runner = CreateRunner(client, [CreateSpeakFunction(_ => { })], []);

        await runner.RunAsync(lifetime.Token);

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
    /// Lifetime cancellation aborts invalid-response recovery backoff before a fresh request can be issued.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenLifetimeCancelsDuringInvalidResponseRecoveryBackoff_EndsQuietlyWithoutRecoveryRequest()
    {
        ChatResponse invalid = new(new ChatMessage(ChatRole.Assistant, "ordinary text"));
        ScriptedSessionClient client = new(Respond(invalid));
        using CancellationTokenSource lifetime = new();
        var recoveryPolicy = new ObservingInvalidResponseRecoveryPolicy();
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(_ => { })],
            [],
            invalidResponseRecoveryPolicy: recoveryPolicy);
        Task runTask = runner.RunAsync(lifetime.Token);
        await recoveryPolicy.Started.Task;
        lifetime.Cancel();

        await runTask;

        await recoveryPolicy.CancellationObserved.Task;
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// A fresh invalidation during invalid-response backoff supersedes recovery without consuming its budget: the
    /// streak from the superseded response is not carried forward, so the replacement request's own invalid
    /// response still receives a full backoff before any exhaustion, and the replacement request carries the fresh
    /// injection instead of a recovery request (AI-002 TR-40 versus TR-43).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_DuringInvalidResponseRecoveryBackoff_DoesNotConsumeRecoveryBudget()
    {
        const string injected = "Important scene events require your attention: something happened.";
        var recoveryPolicy = new ObservingInvalidResponseRecoveryPolicy(consecutiveFailureBudget: 2);
        using CancellationTokenSource lifetime = new();
        ChatResponse invalid = new(new ChatMessage(ChatRole.Assistant, "ordinary text"));
        ScriptedSessionClient client = new(
            Respond(invalid),
            Respond(invalid));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(_ => { })],
            [new ChatMessage(ChatRole.User, "Run input.")],
            invalidResponseRecoveryPolicy: recoveryPolicy);
        Task runTask = runner.RunAsync(lifetime.Token);
        await recoveryPolicy.Started.Task;
        runner.InvalidateForFreshTurn(expectFreshInjection: true);
        runner.QueueFreshInjection(injected);
        await WaitForBackoffCallsAsync(recoveryPolicy, backoffCallCount: 2);
        lifetime.Cancel();

        await runTask;

        // Backoff #2 for the replacement request's own invalid response proves the superseded streak never
        // consumed the budget of two: consumption would have exhausted recovery before a second backoff.
        Assert.Equal(2, recoveryPolicy.BackoffCallCount);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            ["Run input.", injected],
            client.Requests[1].Select(message => message.Text));
    }

    /// <summary>
    /// A fresh invalidation arriving during a transport-retry delay supersedes the pending retry: the stale
    /// request is never re-issued, the transport-retry budget is not consumed by the invalidation, and the fresh
    /// request replaces it (AI-002 TR-40 versus TR-43).
    /// </summary>
    [Fact]
    public async Task InvalidateForFreshTurn_DuringTransportRetryDelay_SupersedesThePendingRetry()
    {
        List<string> speech = [];
        CancellationTokenSource lifetime = new();
        ScriptedSessionClient client = new(
            FailStep(new HttpRequestException("connection reset")),
            Respond(CreateCall(
                "speak-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })),
            EndQuietly(lifetime));
        AgentSessionRunner runner = CreateRunner(
            client,
            [CreateSpeakFunction(speech.Add)],
            [new ChatMessage(ChatRole.User, "Run input.")],
            retryDelays: [TimeSpan.FromMilliseconds(20)]);

        Task runTask = runner.RunAsync(lifetime.Token);
        await WaitForAttemptsAsync(client, attemptCount: 1);
        runner.InvalidateForFreshTurn(expectFreshInjection: false);
        await runTask;

        Assert.Equal(["Hello"], speech);
        // The failed attempt, the replacement request, and the final replay: no retry of the stale request.
        Assert.Equal(3, client.Requests.Count);
        Assert.Single(client.Requests[1], client.Requests[0][0]);
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
        TaskCompletionSource requestStarted,
        TaskCompletionSource? cancellationObserved = null,
        Task? releaseCancelledRequest = null)
        => async cancellationToken =>
        {
            _ = requestStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = cancellationObserved?.TrySetResult();
                if (releaseCancelledRequest is not null)
                {
                    await releaseCancelledRequest;
                }

                throw;
            }

            return new ChatResponse();
        };

    /// <summary>
    /// Creates a step that holds its request until released and then returns the supplied message — ignoring the
    /// cancellation token — to model a provider whose generation completes naturally or returns late after a
    /// fresh-turn cancellation.
    /// </summary>
    private static Func<CancellationToken, Task<ChatResponse>> HoldUntilReleasedStep(
        TaskCompletionSource requestStarted,
        TaskCompletionSource release,
        ChatMessage message)
        => async cancellationToken =>
        {
            _ = cancellationToken;
            _ = requestStarted.TrySetResult();
            await release.Task;
            return new ChatResponse(message);
        };

    private static AgentSessionRunner CreateRunner(
        ScriptedSessionClient client,
        IList<AITool> tools,
        IReadOnlyList<ChatMessage> runInputMessages,
        bool allowMultipleToolCalls = false,
        ILogger? logger = null,
        bool enableReasoningLogging = true,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        IInvalidResponseRecoveryPolicy? invalidResponseRecoveryPolicy = null)
        => new(
            client,
            Instructions,
            runInputMessages,
            tools,
            allowMultipleToolCalls,
            logger ?? NullLogger.Instance,
            enableReasoningLogging,
            retryDelays,
            invalidResponseRecoveryPolicy ?? _immediateInvalidResponseRecoveryPolicy);

    private static async Task WaitForAttemptsAsync(ScriptedSessionClient client, int attemptCount)
    {
        for (int index = 0; index < 500 && client.Requests.Count < attemptCount; index++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(attemptCount, client.Requests.Count);
    }

    private static async Task WaitForBackoffCallsAsync(ObservingInvalidResponseRecoveryPolicy policy, int backoffCallCount)
    {
        for (int index = 0; index < 500 && policy.BackoffCallCount < backoffCallCount; index++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(backoffCallCount, policy.BackoffCallCount);
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
    /// Deterministic in-flight tool whose completion the test controls: it completes naturally when released and
    /// co-operatively cancels when its token fires, so an invalidation or injection can be signalled while the
    /// tool is provably executing.
    /// </summary>
    private sealed class ControlledTool
    {
        public const string ToolName = "controlled_action";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Completed
        {
            get;
            private set;
        }

        public AIFunction Function => AIFunctionFactory.Create(InvokeAsync, ToolName);

        private async Task<string> InvokeAsync(CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            Task completed = await Task.WhenAny(Release.Task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            if (!ReferenceEquals(completed, Release.Task))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Completed = true;
            return "controlled result";
        }
    }

    /// <summary>
    /// Wait-shaped in-flight tool modelling an observation wait as the active phase: it records whether its phase
    /// token fired — proving whether an invalidation cancelled it — and completes naturally with a delivered-window
    /// result only when released without cancellation (AI-002 TR-41).
    /// </summary>
    private sealed class WaitLikeTool
    {
        public const string ToolName = "wait_like_action";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CompletedNaturally
        {
            get;
            private set;
        }

        public bool ObservedCancellation
        {
            get;
            private set;
        }

        public AIFunction Function => AIFunctionFactory.Create(InvokeAsync, ToolName);

        private async Task<string> InvokeAsync(CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            Task completed = await Task.WhenAny(Release.Task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            if (!ReferenceEquals(completed, Release.Task))
            {
                ObservedCancellation = true;
                cancellationToken.ThrowIfCancellationRequested();
            }

            CompletedNaturally = true;
            return "wait delivered its window";
        }
    }

    /// <summary>
    /// Non-cooperative tool that ignores cancellation and commits its effect once released, modelling a call that
    /// crossed its commit boundary before a fresh-turn invalidation arrived.
    /// </summary>
    private sealed class CommittingTool
    {
        public const string ToolName = "committing_action";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Commit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Committed
        {
            get;
            private set;
        }

        public AIFunction Function => AIFunctionFactory.Create(InvokeAsync, ToolName);

        private async Task<string> InvokeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Started.TrySetResult();
            await Commit.Task;
            Committed = true;
            return "committed result";
        }
    }

    private sealed class ObservingInvalidResponseRecoveryPolicy(
        int consecutiveFailureBudget = 3,
        TimeSpan? backoffDelay = null) : IInvalidResponseRecoveryPolicy
    {
        private readonly InvalidResponseRecoveryPolicy _inner = new(
            consecutiveFailureBudget,
            [backoffDelay ?? Timeout.InfiniteTimeSpan]);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BackoffCallCount
        {
            get;
            private set;
        }

        public int ConsecutiveFailureBudget => _inner.ConsecutiveFailureBudget;

        public async Task BackoffAsync(int consecutiveFailureCount, CancellationToken cancellationToken)
        {
            BackoffCallCount++;
            _ = Started.TrySetResult();
            try
            {
                await _inner.BackoffAsync(consecutiveFailureCount, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = CancellationObserved.TrySetResult();
                throw;
            }
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
