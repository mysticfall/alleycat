using System.Text.Json;
using AlleyCat.TestFramework;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Support.AI;

/// <summary>
/// Deterministic fake-client coverage for <see cref="LiveLLMScenarioExecutor" />: end-to-end trace
/// fidelity (roles, settings, tool schemas, tool-call ids and arguments), legitimate tool-call output,
/// scenario-specific rejection, failure classification, bound enforcement, continuation isolation, trace
/// stability, sanitisation, and borrowed-client ownership. No live backend is contacted.
/// </summary>
[Headless]
public sealed class LiveLLMScenarioExecutorIntegrationTests
{
    private const string SystemPrompt = "You are Vadim, a records officer. Answer briefly from the supplied records.";

    private const string PlayerQuestion = "What does Ally's record say about prior breaches?";

    private const string Sentinel = "sk-TRACESENTINEL0123456789abcdef";

    private static readonly TimeSpan _timeoutBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A two-request tool-bearing scenario preserves roles, texts, settings, tool schemas, tool-call ids and
    /// arguments, simulated tool results, scripted continuations, and usage end-to-end in its trace.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CapturesMessagesSettingsToolSchemasAndToolCallsEndToEnd()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(ToolCallResponse("call-1", "lookup_record"));
        _ = target.Enqueue(TextResponse("Her record is clear of prior breaches.", totalTokens: 12));

        LiveLLMScenarioExecutor executor = new(target);
        LiveLLMScenarioBounds bounds = new(MaxTargetRequests: 2, _timeoutBudget, MaxToolResults: 1);
        ChatOptions options = new()
        {
            ModelId = "test-model",
            Temperature = 0.25f,
            ToolMode = ChatToolMode.Auto,
            Tools =
            [
                AIFunctionFactory.Create(
                    (int userId) => "ok",
                    name: "lookup_record",
                    description: "Looks up a record by identifier."),
            ],
        };

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "records-lookup",
            bounds,
            async context =>
            {
                List<ChatMessage> conversation = [new(ChatRole.System, SystemPrompt), new(ChatRole.User, PlayerQuestion)];
                ChatResponse first = await context.SendTargetAsync(conversation, options);
                conversation.AddRange(first.Messages);

                FunctionCallContent call = first.Messages
                    .SelectMany(static message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .Single();
                conversation.Add(context.RecordSimulatedToolResult(call, new
                {
                    found = true
                }));
                conversation.Add(context.RecordScriptedContinuation(
                    new ChatMessage(ChatRole.User, "Thanks, summarise the finding.")));
                _ = await context.SendTargetAsync(conversation, options);
            });

        Assert.True(
            execution.Succeeded,
            execution.Failure is { } failure ? $"{failure.Classification}: {failure.ExceptionType}: {failure.SafeMessage}" : string.Empty);
        Assert.Equal(LiveLLMScenarioExecutionStatus.Completed, execution.Status);
        Assert.Null(execution.Failure);
        Assert.Equal(LiveLLMTrace.CurrentVersion, ParseTraceVersion(execution.Trace));
        Assert.Equal(2, target.Requests.Count);
        // Borrowed client: execution must never dispose it.
        Assert.False(target.Disposed);

        IReadOnlyList<LiveLLMTraceEntry> entries = execution.Trace.Entries;
        Assert.Equal(
            [LiveLLMTraceEntryKind.Exchange, LiveLLMTraceEntryKind.SuppliedToolResult,
                LiveLLMTraceEntryKind.SuppliedContinuation, LiveLLMTraceEntryKind.Exchange],
            entries.Select(static entry => entry.Kind));
        Assert.Equal(0, entries[0].Index);
        Assert.Equal(3, entries[^1].Index);
        Assert.All(entries, entry => Assert.True(entry.Duration is null || entry.Duration >= TimeSpan.Zero));

        // First request: roles, texts, and settings preserved.
        LiveLLMRequestRecord firstRequest = entries[0].Request!;
        Assert.Equal(["system", "user"], firstRequest.Messages.Select(static message => message.Role));
        Assert.Equal(SystemPrompt, firstRequest.Messages[0].Contents.Single().Text);
        Assert.Equal(PlayerQuestion, firstRequest.Messages[1].Contents.Single().Text);
        LiveLLMOptionsRecord optionsRecord = firstRequest.Options!;
        Assert.Equal("test-model", optionsRecord.ModelId);
        Assert.Equal(0.25, optionsRecord.Temperature);
        Assert.NotNull(optionsRecord.ToolMode);
        LiveLLMToolRecord tool = Assert.Single(optionsRecord.Tools);
        Assert.Equal("lookup_record", tool.Name);
        Assert.Equal("Looks up a record by identifier.", tool.Description);
        Assert.NotNull(tool.JsonSchema);
        Assert.Contains("userId", tool.JsonSchema.ToJsonString(), StringComparison.Ordinal);

        // Client boundary: the very same options instance — tool definitions included — reached GetResponseAsync.
        Assert.All(target.Requests, request => Assert.Same(options, request.Options));
        _ = Assert.Single(target.Requests[0].Options!.Tools!);

        // First response: tool-call id and arguments preserved as legitimate output.
        LiveLLMContentRecord callContent = entries[0].Response!.Messages[0].Contents.Single();
        Assert.Equal("functionCall", callContent.Kind);
        Assert.Equal("call-1", callContent.CallId);
        Assert.Equal("lookup_record", callContent.Name);
        Assert.NotNull(callContent.Data);
        Assert.Contains("7", callContent.Data.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("ally", callContent.Data.ToJsonString(), StringComparison.Ordinal);

        // Simulated tool result: labelled simulated, bound to the call id.
        LiveLLMToolResultRecord toolResult = entries[1].ToolResult!;
        Assert.Equal("call-1", toolResult.CallId);
        Assert.Equal("lookup_record", toolResult.Name);
        Assert.True(toolResult.Simulated);
        Assert.NotNull(toolResult.Result);
        Assert.Contains("found", toolResult.Result.ToJsonString(), StringComparison.Ordinal);

        // Scripted continuation: purpose-labelled message evidence without a provider call.
        Assert.Equal(LiveLLMPurpose.Continuation, entries[2].Purpose);
        LiveLLMMessageRecord suppliedMessage = entries[2].SuppliedMessage!;
        Assert.Equal("user", suppliedMessage.Role);
        Assert.Equal("Thanks, summarise the finding.", suppliedMessage.Contents.Single().Text);

        // Second request: the tool result and continuation reached the provider, and usage was captured.
        LiveLLMRequestRecord secondRequest = entries[3].Request!;
        Assert.Equal(5, secondRequest.Messages.Count);
        Assert.Equal("tool", secondRequest.Messages[3].Role);
        Assert.Equal("functionResult", secondRequest.Messages[3].Contents.Single().Kind);
        Assert.Equal("call-1", secondRequest.Messages[3].Contents.Single().CallId);
        Assert.Equal("Her record is clear of prior breaches.", entries[3].Response!.Messages[0].Contents.Single().Text);
        Assert.Equal(12, entries[3].Response!.Usage!.TotalTokenCount);
    }

    /// <summary>
    /// A tool-calls-only response is captured as legitimate experimental output without failing the execution.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AcceptsToolCallsOnlyResponseAsLegitimateOutput()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(ToolCallResponse("call-tool-only", "speak"));

        LiveLLMScenarioExecutor executor = new(target);

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "tool-only",
            new LiveLLMScenarioBounds(1, _timeoutBudget),
            context => context.SendTargetAsync(
                [new ChatMessage(ChatRole.User, "Greet the player.")]));

        Assert.True(execution.Succeeded);
        LiveLLMContentRecord content = execution.Trace.Entries.Single().Response!.Messages[0].Contents.Single();
        Assert.Equal("functionCall", content.Kind);
        Assert.Equal("call-tool-only", content.CallId);
    }

    /// <summary>
    /// Scenario-specific rejection of an unexpected response shape classifies the failure as malformed while the
    /// rejected exchange remains captured evidence.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenScenarioRejectsResponse_ClassifiesMalformedAndKeepsPartialTrace()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(ToolCallResponse("call-unexpected", "speak"));

        LiveLLMScenarioExecutor executor = new(target);

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "text-only-fixture",
            new LiveLLMScenarioBounds(1, _timeoutBudget),
            async context =>
            {
                ChatResponse response = await context.SendTargetAsync(
                    [new ChatMessage(ChatRole.User, "Introduce yourself in prose.")]);
                if (response.Messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .Any())
                {
                    throw new LiveLLMScenarioException(
                        LiveLLMFailureClassification.Malformed,
                        "This fixture requires a text-only response and received a function call.");
                }
            });

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.Malformed, execution.Failure!.Classification);
        Assert.Contains("text-only response", execution.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(LiveLLMTraceStatus.Failed, execution.Trace.Status);
        // The rejected exchange remains captured evidence.
        LiveLLMTraceEntry entry = Assert.Single(execution.Trace.Entries);
        Assert.NotNull(entry.Response);
        Assert.Equal("call-unexpected", entry.Response!.Messages[0].Contents.Single().CallId);
        Assert.False(target.Disposed);
    }

    /// <summary>
    /// A transport failure on a later request preserves the earlier exchanges as partial evidence and records a
    /// sanitised, credential-free failure classification.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTransportFailsOnLaterRequest_PreservesPartialTraceSafely()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse("first answer"));
        _ = target.EnqueueError(new HttpRequestException($"ApiKey={Sentinel} provider unreachable"));

        LiveLLMScenarioExecutor executor = new(target);
        List<ChatMessage> messages = [new(ChatRole.User, "first"), new(ChatRole.User, "second")];

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "transport-failure",
            new LiveLLMScenarioBounds(2, _timeoutBudget),
            async context =>
            {
                _ = await context.SendTargetAsync(messages);
                _ = await context.SendTargetAsync(messages);
            });

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.Transport, execution.Failure!.Classification);
        Assert.Equal("HttpRequestException", execution.Failure.ExceptionType);
        // Safe message: bounded and credential-free.
        Assert.DoesNotContain(Sentinel, execution.Failure.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("[redacted]", execution.Failure.SafeMessage, StringComparison.Ordinal);

        // Partial evidence: the completed exchange survives, the failed one carries its request and failure.
        Assert.Equal(2, execution.Trace.Entries.Count);
        Assert.NotNull(execution.Trace.Entries[0].Response);
        LiveLLMTraceEntry failed = execution.Trace.Entries[1];
        Assert.Null(failed.Response);
        Assert.NotNull(failed.Request);
        Assert.Equal(LiveLLMFailureClassification.Transport, failed.Failure!.Classification);

        string json = execution.Trace.ToJsonString();
        Assert.DoesNotContain(Sentinel, json, StringComparison.Ordinal);
        Assert.False(target.Disposed);
    }

    /// <summary>
    /// Exceeding the declared timeout budget classifies the execution failure as a timeout on the failing entry.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTimeoutExceeds_ClassifiesTimeout()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return TextResponse("late");
        });

        LiveLLMScenarioExecutor executor = new(target);

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "timeout",
            new LiveLLMScenarioBounds(1, TimeSpan.FromMilliseconds(50)),
            context => context.SendTargetAsync([new ChatMessage(ChatRole.User, "slow")]));

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.Timeout, execution.Failure!.Classification);
        LiveLLMTraceEntry entry = Assert.Single(execution.Trace.Entries);
        Assert.Null(entry.Response);
        Assert.Equal(LiveLLMFailureClassification.Timeout, entry.Failure!.Classification);
        Assert.False(target.Disposed);
    }

    /// <summary>
    /// Caller cancellation classifies the execution failure as cancelled rather than a timeout.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_ClassifiesCancelled()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return TextResponse("never");
        });

        LiveLLMScenarioExecutor executor = new(target);

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "cancelled",
            new LiveLLMScenarioBounds(1, _timeoutBudget),
            context => context.SendTargetAsync([new ChatMessage(ChatRole.User, "any")], cancellationToken: cancellation.Token)
                ,
            cancellation.Token);

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.Cancelled, execution.Failure!.Classification);
        Assert.False(target.Disposed);
    }

    /// <summary>
    /// Exceeding the declared target request bound fails the execution with a bound-exceeded classification.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EnforcesDeclaredRequestBounds()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse("one"));

        LiveLLMScenarioExecutor executor = new(target);

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "bound-requests",
            new LiveLLMScenarioBounds(MaxTargetRequests: 1, _timeoutBudget),
            async context =>
            {
                _ = await context.SendTargetAsync([new ChatMessage(ChatRole.User, "first")]);
                _ = await context.SendTargetAsync([new ChatMessage(ChatRole.User, "second")]);
            });

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.BoundExceeded, execution.Failure!.Classification);
        Assert.Contains("1 target request", execution.Failure.SafeMessage, StringComparison.Ordinal);
        _ = Assert.Single(execution.Trace.Entries);
        _ = Assert.Single(target.Requests);
    }

    /// <summary>
    /// Exceeding the declared simulated tool-result bound fails the execution with a bound-exceeded classification.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EnforcesDeclaredToolResultBound()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(ToolCallResponse("call-1", "lookup_record"));

        LiveLLMScenarioExecutor executor = new(target);

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "bound-tool-results",
            new LiveLLMScenarioBounds(1, _timeoutBudget, MaxToolResults: 1),
            context =>
            {
                FunctionCallContent call = new("call-1", "lookup_record", new Dictionary<string, object?>());
                _ = context.RecordSimulatedToolResult(call, "first");
                _ = context.RecordSimulatedToolResult(call, "second");
                return Task.CompletedTask;
            });

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.BoundExceeded, execution.Failure!.Classification);
        Assert.Contains("1 tool result", execution.Failure.SafeMessage, StringComparison.Ordinal);
        // The first supplied result stays recorded.
        _ = Assert.Single(execution.Trace.Entries);
        Assert.True(execution.Trace.Entries[0].ToolResult!.Simulated);
    }

    /// <summary>
    /// Exceeding the declared continuation request bound fails the execution with a bound-exceeded classification.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EnforcesDeclaredContinuationBound()
    {
        LiveLLMScriptedChatClient continuation = new("continuation");
        _ = continuation.Enqueue(TextResponse("generated continuation"));

        LiveLLMScenarioExecutor executor = new(target: new LiveLLMScriptedChatClient("target"), continuation);
        List<ChatMessage> isolatedHistory = [new ChatMessage(ChatRole.System, "isolated continuation context")];

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "bound-continuations",
            new LiveLLMScenarioBounds(1, _timeoutBudget, MaxContinuationRequests: 1),
            async context =>
            {
                _ = await context.SendContinuationAsync(isolatedHistory);
                _ = await context.SendContinuationAsync(isolatedHistory);
            });

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.BoundExceeded, execution.Failure!.Classification);
        Assert.Contains("1 continuation request", execution.Failure.SafeMessage, StringComparison.Ordinal);
        _ = Assert.Single(continuation.Requests);
        Assert.False(continuation.Disposed);
    }

    /// <summary>
    /// A request with no messages classifies the execution failure as malformed before contacting the client.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenRequestHasNoMessages_ClassifiesMalformed()
    {
        LiveLLMScenarioExecutor executor = new(new LiveLLMScriptedChatClient("target"));

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "empty-messages",
            new LiveLLMScenarioBounds(1, _timeoutBudget),
            context => context.SendTargetAsync([]));

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.Malformed, execution.Failure!.Classification);
        Assert.Empty(execution.Trace.Entries);
    }

    /// <summary>
    /// A generated continuation runs on the borrowed continuation client under the isolated history supplied by
    /// the scenario, with no target conversation text leaking into it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GeneratedContinuationUsesIsolatedHistoryAndBorrowedClient()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse("target answer"));
        _ = target.Enqueue(TextResponse("The guard summarises the ledger finding."));
        LiveLLMScriptedChatClient continuation = new("continuation");
        _ = continuation.Enqueue(TextResponse("The guard consults the ledger."));

        LiveLLMScenarioExecutor executor = new(target, continuation);
        List<ChatMessage> targetHistory = [new ChatMessage(ChatRole.System, SystemPrompt)];
        List<ChatMessage> isolatedHistory =
        [
            new ChatMessage(ChatRole.System, "You write NPC continuations from the director's summary only."),
            new ChatMessage(ChatRole.User, "Continue: the guard checks the ledger."),
        ];

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "generated-continuation",
            new LiveLLMScenarioBounds(2, _timeoutBudget, MaxContinuationRequests: 1),
            async context =>
            {
                ChatResponse first = await context.SendTargetAsync(targetHistory);
                targetHistory.AddRange(first.Messages);

                ChatResponse generated = await context.SendContinuationAsync(isolatedHistory);
                targetHistory.Add(new ChatMessage(ChatRole.Assistant, generated.Text));

                _ = await context.SendTargetAsync(targetHistory);
            });

        Assert.True(execution.Succeeded, execution.Failure?.SafeMessage);
        // The continuation client received exactly the isolated history: no target conversation text leaked.
        _ = Assert.Single(continuation.Requests);
        IReadOnlyList<ChatMessage> received = continuation.Requests[0].Messages;
        Assert.Equal(["system", "user"], received.Select(static message => message.Role.Value));
        Assert.DoesNotContain(received, message => message.Text.Contains(SystemPrompt, StringComparison.Ordinal));
        Assert.DoesNotContain(received, message => message.Text.Contains("target answer", StringComparison.Ordinal));
        Assert.Equal("You write NPC continuations from the director's summary only.", received[0].Text);

        LiveLLMTraceEntry continuationEntry = execution.Trace.Entries.Single(
            static entry => entry.Purpose == LiveLLMPurpose.Continuation);
        Assert.Equal(LiveLLMPurpose.Target, execution.Trace.Entries[0].Purpose);
        Assert.Equal("The guard consults the ledger.", continuationEntry.Response!.Messages[0].Contents.Single().Text);
        // The generated continuation flowed into the next target request as scripted material.
        Assert.Contains(
            execution.Trace.Entries[^1].Request!.Messages,
            message => message.Text.Contains("The guard consults the ledger.", StringComparison.Ordinal));
        // Borrowed clients are never disposed by execution.
        Assert.False(target.Disposed);
        Assert.False(continuation.Disposed);
    }

    /// <summary>
    /// The captured trace is a stable snapshot: mutating the caller's live objects after execution leaves every
    /// recorded value unchanged.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TraceIsStableAgainstLaterMutation()
    {
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(TextResponse("original answer"));

        LiveLLMScenarioExecutor executor = new(target);
        ChatOptions options = new()
        {
            ModelId = "test-model",
            Temperature = 0.25f
        };
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "original question")];
        ChatResponse? response = null;

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "stability",
            new LiveLLMScenarioBounds(1, _timeoutBudget),
            async context => response = await context.SendTargetAsync(messages, options));

        // Mutate every live object the scenario held a reference to.
        options.Temperature = 9f;
        options.ModelId = "mutated-model";
        messages.Add(new ChatMessage(ChatRole.User, "mutated follow-up"));
        response!.Messages.Add(new ChatMessage(ChatRole.Assistant, "mutated answer"));
        response.Usage = new UsageDetails { TotalTokenCount = 999 };

        LiveLLMTraceEntry entry = execution.Trace.Entries.Single();
        Assert.Equal("test-model", entry.Request!.Options!.ModelId);
        Assert.Equal(0.25, entry.Request.Options.Temperature);
        _ = Assert.Single(entry.Request.Messages);
        Assert.Equal("original answer", entry.Response!.Messages[0].Contents.Single().Text);
        _ = Assert.Single(entry.Response.Messages);
        // Usage captured at send time (5/2/7), unaffected by the later mutation to 999.
        Assert.Equal(7, entry.Response.Usage!.TotalTokenCount);
    }

    /// <summary>
    /// Credential-shaped values in responses and tool results are redacted in both the in-memory records and the
    /// serialised JSON trace.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SanitisesCredentialShapedValuesInRecordsAndJson()
    {
        const string sentinelUrl = "https://user:TRACEURLPASSWORD9876543210abcdef@example.test/v1";
        LiveLLMScriptedChatClient target = new("target");
        _ = target.Enqueue(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, $"Check ApiKey={Sentinel} and {sentinelUrl} before answering.")));

        LiveLLMScenarioExecutor executor = new(target);
        FunctionCallContent call = new("call-secret", "lookup_record", new Dictionary<string, object?>());

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "sanitisation",
            new LiveLLMScenarioBounds(1, _timeoutBudget, MaxToolResults: 1),
            context =>
            {
                ChatMessage toolMessage = context.RecordSimulatedToolResult(call, $"token={Sentinel}");
                return context.SendTargetAsync(
                    [new ChatMessage(ChatRole.User, "question"), toolMessage]);
            });

        Assert.True(execution.Succeeded, execution.Failure?.SafeMessage);
        string json = execution.Trace.ToJsonString();
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain("TRACEURLPASSWORD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        // Sanitisation applies to the in-memory records as well, not only to serialisation.
        LiveLLMContentRecord responseContent = execution.Trace.Entries
            .Single(static entry => entry.Kind == LiveLLMTraceEntryKind.Exchange)
            .Response!.Messages[0]
            .Contents.Single();
        Assert.Contains("[redacted]", responseContent.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, responseContent.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Requesting a generated continuation without a configured generator fails with a clear classification.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutContinuationGenerator_FailsWithClearClassification()
    {
        LiveLLMScenarioExecutor executor = new(new LiveLLMScriptedChatClient("target"));

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            "missing-generator",
            new LiveLLMScenarioBounds(1, _timeoutBudget, MaxContinuationRequests: 1),
            context => context.SendContinuationAsync([new ChatMessage(ChatRole.User, "continue")]));

        Assert.False(execution.Succeeded);
        Assert.Equal(LiveLLMFailureClassification.Unknown, execution.Failure!.Classification);
        Assert.Contains("continuation generator", execution.Failure.SafeMessage, StringComparison.Ordinal);
    }

    private static ChatResponse TextResponse(string text, long totalTokens = 7)
        => new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = totalTokens - 5, TotalTokenCount = totalTokens },
        };

    private static ChatResponse ToolCallResponse(string callId, string name)
        => new(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, new Dictionary<string, object?> { ["userId"] = 7, ["query"] = "ally" })]));

    private static int ParseTraceVersion(LiveLLMTrace trace)
    {
        using var document = JsonDocument.Parse(trace.ToJsonString());
        return document.RootElement.GetProperty("version").GetInt32();
    }
}
