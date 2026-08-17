using AlleyCat.Mind.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the production tool-only protocol without a network backend.
/// </summary>
public sealed class ToolOnlyTurnRunnerTests
{
    private const string Instructions = "Private test instructions.";
    private const string SpeakToolName = "speak";

    /// <summary>
    /// A sole end marker terminates without invocation or another request.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithImmediateEndTurn_StopsAfterOneRequest()
    {
        var client = new ScriptedChatClient(CreateCall("end", ToolOnlyTurnRunner.EndTurnToolName));
        AIFunction speak = CreateSpeakFunction(_ => throw new Xunit.Sdk.XunitException("Speak must not run."));

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, "Run input.")],
            [speak],
            false,
            NullLogger.Instance,
            CancellationToken.None);

        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// A production action followed by the final marker completes without marker invocation, result replay, or another request.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithSpeakAndFinalEndTurn_CompletesInOneRequest()
    {
        var client = new ScriptedChatClient(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "speak-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Hello" }),
                new FunctionCallContent(
                    "end-call",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
            ]));
        List<string> speech = [];

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(speech.Add)],
            false,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["Hello"], speech);
        IReadOnlyList<ChatMessage> request = Assert.Single(client.Requests);
        Assert.Empty(request);
    }

    /// <summary>
    /// A speak result is correlated into exact response replay before a later end marker.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithSpeakThenEnd_ReplaysCallAndResultAndUsesFreshRequiredOptions()
    {
        ChatMessage speakResponse = CreateCall(
            "speak-call",
            SpeakToolName,
            new Dictionary<string, object?> { ["speech"] = "Hello" });
        var client = new ScriptedChatClient(
            speakResponse,
            CreateCall("end-call", ToolOnlyTurnRunner.EndTurnToolName));
        List<string> speech = [];
        AIFunction speak = CreateSpeakFunction(speech.Add);

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, "Run input.")],
            [speak],
            false,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["Hello"], speech);
        Assert.Equal(2, client.Requests.Count);
        Assert.NotSame(client.Options[0], client.Options[1]);
        foreach (ChatOptions options in client.Options)
        {
            Assert.Equal(Instructions, options.Instructions);
            RequiredChatToolMode toolMode = Assert.IsType<RequiredChatToolMode>(options.ToolMode);
            Assert.Null(toolMode.RequiredFunctionName);
            Assert.False(options.AllowMultipleToolCalls);
            Assert.Null(options.ResponseFormat);
            IList<AITool> tools = Assert.IsAssignableFrom<IList<AITool>>(options.Tools);
            Assert.Equal(
                [SpeakToolName, ToolOnlyTurnRunner.EndTurnToolName],
                tools.Cast<AIFunction>().Select(tool => tool.Name));
            Assert.Same(speak, tools[0]);
        }

        IReadOnlyList<ChatMessage> replay = client.Requests[1];
        Assert.Same(speakResponse, replay[1]);
        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(replay[2].Contents));
        Assert.Equal("speak-call", result.CallId);
    }

    /// <summary>
    /// A valid multi-action batch is accepted locally and executes serially regardless of the provider preference.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WithMultiActionBatch_ExecutesAllConfiguredActionsInOrder(
        bool allowMultipleToolCalls)
    {
        List<string> actions = [];
        AIFunction first = AIFunctionFactory.Create(
            () => actions.Add("first"),
            "first_action");
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
        var client = new ScriptedChatClient(
            batch,
            CreateCall("end-call", ToolOnlyTurnRunner.EndTurnToolName));

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [first, second],
            allowMultipleToolCalls,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["first", "second:2"], actions);
        Assert.All(client.Options, options => Assert.Equal(allowMultipleToolCalls, options.AllowMultipleToolCalls));
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// Multiple production actions before a final marker execute serially and complete in the same response.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithMultiActionFinalBatch_ExecutesInProviderOrderAndCompletes()
    {
        List<string> actions = [];
        AIFunction first = AIFunctionFactory.Create(() => actions.Add("first"), "first_action");
        AIFunction second = AIFunctionFactory.Create(
            (int count) => actions.Add($"second:{count}"),
            "second_action");
        var client = new ScriptedChatClient(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent("first-call", "first_action", new Dictionary<string, object?>()),
                new FunctionCallContent(
                    "second-call",
                    "second_action",
                    new Dictionary<string, object?> { ["count"] = 2 }),
                new FunctionCallContent(
                    "end-call",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
            ]));

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [first, second],
            false,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["first", "second:2"], actions);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Invalid output is rejected as a whole before any valid-looking call can cause effects.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task RunAsync_WithInvalidResponse_RejectsWithoutEffects(ChatResponse response)
    {
        int invocationCount = 0;
        var client = new ScriptedChatClient(response);

        _ = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => invocationCount++)],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal(0, invocationCount);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Any non-function content invalidates the complete response before a valid call can cause effects.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonFunctionContents))]
    public async Task RunAsync_WithNonFunctionContent_RejectsWholeResponseWithoutEffects(AIContent content)
    {
        int invocationCount = 0;
        var response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "valid-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Must not run" }),
                content,
            ]));
        var client = new ScriptedChatClient(response);

        _ = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => invocationCount++)],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal(0, invocationCount);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Reasoning content before a valid call and the final marker completes the turn in one request.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithReasoningThenSpeakAndFinalEndTurn_ToleratesReasoningAndCompletesInOneRequest()
    {
        var client = new ScriptedChatClient(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("private reasoning"),
                new FunctionCallContent(
                    "speak-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Hello" }),
                new FunctionCallContent(
                    "end-call",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
            ]));
        List<string> speech = [];

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(speech.Add)],
            false,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["Hello"], speech);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Reasoning alone cannot satisfy the protocol because zero actions require the sole end marker.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithOnlyReasoning_RejectsWholeResponseWithoutEffects()
    {
        int invocationCount = 0;
        var client = new ScriptedChatClient(new ChatMessage(
            ChatRole.Assistant,
            [new TextReasoningContent("private reasoning")]));

        _ = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => invocationCount++)],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal(0, invocationCount);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Reasoning content is logged at trace level only when trace logging is enabled and the dedicated
    /// <c>enableReasoningLogging</c> control is on.
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
                    "end-call",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
            ]));

        CapturingLoggerFactory traceFactory = new(LogLevel.Trace);
        await ToolOnlyTurnRunner.RunAsync(
            new ScriptedChatClient(response),
            Instructions,
            [],
            [CreateSpeakFunction(_ => { })],
            false,
            traceFactory.CreateLogger("test"),
            CancellationToken.None,
            enableReasoningLogging: true);

        Assert.Contains(traceFactory.Entries, entry =>
            entry.Level == LogLevel.Trace
            && entry.Message.Contains($"Reasoning: {reasoning}", StringComparison.Ordinal));

        CapturingLoggerFactory traceDisabledFactory = new(LogLevel.Trace);
        await ToolOnlyTurnRunner.RunAsync(
            new ScriptedChatClient(response),
            Instructions,
            [],
            [CreateSpeakFunction(_ => { })],
            false,
            traceDisabledFactory.CreateLogger("test"),
            CancellationToken.None,
            enableReasoningLogging: false);

        Assert.DoesNotContain(traceDisabledFactory.Entries, entry =>
            entry.Message.Contains("Reasoning:", StringComparison.Ordinal));

        CapturingLoggerFactory infoFactory = new(LogLevel.Information);
        await ToolOnlyTurnRunner.RunAsync(
            new ScriptedChatClient(response),
            Instructions,
            [],
            [CreateSpeakFunction(_ => { })],
            false,
            infoFactory.CreateLogger("test"),
            CancellationToken.None,
            enableReasoningLogging: true);

        Assert.DoesNotContain(infoFactory.Entries, entry =>
            entry.Message.Contains("Reasoning:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Caller cancellation is propagated rather than converted into a protocol failure.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenRequestIsCancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new ScriptedChatClient(CreateCall("unused", ToolOnlyTurnRunner.EndTurnToolName));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => { })],
            false,
            NullLogger.Instance,
            cancellation.Token));

        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// A production action failure stops the loop without a repair request.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenSpeakFails_DoesNotRetry()
    {
        var client = new ScriptedChatClient(CreateCall(
            "speak-call",
            SpeakToolName,
            new Dictionary<string, object?> { ["speech"] = "Hello" }));
        AIFunction speak = AIFunctionFactory.Create(
            ThrowingSpeak,
            SpeakToolName);

        ToolOnlyTurnException error = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [speak],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal("A tool-only action failed.", error.Message);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Call identifiers remain unique across the complete transient turn history.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithCallIDRepeatedOnLaterResponse_StopsWithoutSecondEffect()
    {
        var client = new ScriptedChatClient(
            CreateCall(
                "repeated-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "First" }),
            CreateCall(
                "repeated-call",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Second" }));
        List<string> speech = [];

        _ = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(speech.Add)],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal(["First"], speech);
        Assert.Equal(2, client.Requests.Count);
    }

    /// <summary>
    /// Repeated valid actions cannot exceed the named model-request bound.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithOnlySpeakCalls_FailsAtRequestBound()
    {
        ChatResponse[] responses = [.. Enumerable.Range(0, ToolOnlyTurnRunner.MaxModelRequests)
            .Select(index => new ChatResponse(CreateCall(
                $"call-{index}",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })) )];
        var client = new ScriptedChatClient(responses);
        int invocationCount = 0;

        ToolOnlyTurnException error = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => invocationCount++)],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Contains("model-request limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(ToolOnlyTurnRunner.MaxModelRequests, invocationCount);
        Assert.Equal(ToolOnlyTurnRunner.MaxModelRequests, client.Requests.Count);
    }

    /// <summary>
    /// An oversized response is rejected before any action crosses the total-action bound.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithTooManySpeakCalls_FailsAtActionBoundWithoutEffects()
    {
        List<AIContent> calls = [.. Enumerable.Range(0, ToolOnlyTurnRunner.MaxToolActions + 1)
            .Select(index => (AIContent)new FunctionCallContent(
                $"call-{index}",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" }))];
        var client = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));
        int invocationCount = 0;

        ToolOnlyTurnException error = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => invocationCount++)],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Contains("action limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, invocationCount);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// The final protocol marker does not consume the production-action allowance.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithMaximumActionsAndFinalMarker_DoesNotCountMarkerAsAction()
    {
        List<AIContent> calls = [.. Enumerable.Range(0, ToolOnlyTurnRunner.MaxToolActions)
            .Select(index => (AIContent)new FunctionCallContent(
                $"call-{index}",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = $"Line {index}" }))];
        calls.Add(new FunctionCallContent(
            "end-call",
            ToolOnlyTurnRunner.EndTurnToolName,
            new Dictionary<string, object?>()));
        var client = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));
        int invocationCount = 0;

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => invocationCount++)],
            false,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(ToolOnlyTurnRunner.MaxToolActions, invocationCount);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// A later action failure leaves an earlier action committed and prevents terminal completion.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenLaterActionBeforeFinalMarkerFails_PreservesEarlierEffectAndStops()
    {
        List<string> actions = [];
        AIFunction first = AIFunctionFactory.Create(() => actions.Add("first"), "first_action");
        AIFunction failing = AIFunctionFactory.Create(
            (Action)(() => throw new InvalidOperationException("Later failure.")),
            "failing_action");
        var client = new ScriptedChatClient(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent("first-call", "first_action", new Dictionary<string, object?>()),
                new FunctionCallContent("failing-call", "failing_action", new Dictionary<string, object?>()),
                new FunctionCallContent(
                    "end-call",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
            ]));

        _ = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [first, failing],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal(["first"], actions);
        _ = Assert.Single(client.Requests);
    }

    /// <summary>
    /// Reserved names and duplicate production declarations are rejected before any request.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithReservedOrDuplicateProductionTools_RejectsBeforeRequest()
    {
        var client = new ScriptedChatClient(CreateCall("end", ToolOnlyTurnRunner.EndTurnToolName));
        AIFunction endCollision = AIFunctionFactory.Create(
            () => { },
            ToolOnlyTurnRunner.EndTurnToolName);

        _ = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => { }), endCollision],
            false,
            NullLogger.Instance,
            CancellationToken.None));
        Assert.Empty(client.Requests);

        _ = await Assert.ThrowsAsync<ToolOnlyTurnException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [CreateSpeakFunction(_ => { }), CreateSpeakFunction(_ => { })],
            false,
            NullLogger.Instance,
            CancellationToken.None));
        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// The protocol accepts any set of uniquely named production functions, including none.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithNoOrArbitraryProductionFunctions_RegistersAllActions()
    {
        var client = new ScriptedChatClient(CreateCall("end", ToolOnlyTurnRunner.EndTurnToolName));
        AIFunction otherFunction = AIFunctionFactory.Create(
            () => { },
            "other_action");

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [],
            false,
            NullLogger.Instance,
            CancellationToken.None);
        _ = Assert.Single(client.Requests);

        var secondClient = new ScriptedChatClient(CreateCall("end", ToolOnlyTurnRunner.EndTurnToolName));
        await ToolOnlyTurnRunner.RunAsync(
            secondClient,
            Instructions,
            [],
            [otherFunction],
            true,
            NullLogger.Instance,
            CancellationToken.None);
        ChatOptions options = Assert.Single(secondClient.Options);
        Assert.True(options.AllowMultipleToolCalls);
        Assert.Equal(
            ["other_action", ToolOnlyTurnRunner.EndTurnToolName],
            options.Tools!.Select(tool => tool.Name));
    }

    /// <summary>
    /// Gets representative malformed, text-bearing, mixed, refused, and unsupported responses.
    /// </summary>
    public static TheoryData<ChatResponse> InvalidResponses =>
    [
        new ChatResponse(new ChatMessage(ChatRole.Assistant, "ordinary text")),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Hello" }),
                new TextContent("mixed text"),
            ])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "duplicate",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "One" }),
                new FunctionCallContent(
                    "duplicate",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Two" }),
            ])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new ErrorContent("refusal or adapter error")])),
        new ChatResponse(new ChatMessage(
            ChatRole.User,
            [new FunctionCallContent("call", ToolOnlyTurnRunner.EndTurnToolName, new Dictionary<string, object?>())])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "end",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
                new FunctionCallContent(
                    "call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Must not run" }),
            ])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "valid-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Must not run" }),
                new FunctionCallContent(
                    "end-one",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
                new FunctionCallContent(
                    "end-two",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?>()),
            ])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "valid-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Must not run" }),
                new FunctionCallContent(
                    "end",
                    ToolOnlyTurnRunner.EndTurnToolName,
                    new Dictionary<string, object?> { ["unexpected"] = true }),
            ])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(
                "call",
                "Speak",
                new Dictionary<string, object?> { ["speech"] = "Hello" })])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(
                "end",
                ToolOnlyTurnRunner.EndTurnToolName,
                new Dictionary<string, object?> { ["unexpected"] = true })])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(
                " ",
                SpeakToolName,
                new Dictionary<string, object?> { ["speech"] = "Hello" })])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(
                "call",
                SpeakToolName,
                new Dictionary<string, object?>())
            {
                Exception = new InvalidOperationException("Malformed adapter arguments."),
            }])),
        new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "valid-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["speech"] = "Must not run" }),
                new FunctionCallContent(
                    "malformed-call",
                    SpeakToolName,
                    new Dictionary<string, object?> { ["unexpected"] = true }),
            ])),
    ];

    /// <summary>
    /// Gets non-function content that the strict tool-only response contract rejects.
    /// </summary>
    public static TheoryData<AIContent> NonFunctionContents =>
    [
        new TextContent(string.Empty),
        new TextContent("   "),
    ];

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
        string callID,
        string name,
        IDictionary<string, object?>? arguments = null)
        => new(
            ChatRole.Assistant,
            [new FunctionCallContent(callID, name, arguments ?? new Dictionary<string, object?>())]);

    private sealed class ScriptedChatClient(params object[] responses) : IChatClient
    {
        private readonly Queue<object> _responses = new(responses);

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add([.. messages]);
            Options.Add(Assert.IsType<ChatOptions>(options));
            object response = _responses.Dequeue();
            return Task.FromResult(response is ChatResponse chatResponse
                ? chatResponse
                : new ChatResponse(Assert.IsType<ChatMessage>(response)));
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
