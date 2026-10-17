using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Provider;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Verifies wire-level OpenAI chat-completions request composition for the agent session without a live backend.
/// </summary>
public sealed class OpenAIChatCompletionsTransportTests
{
    private const string Instructions = "Test the tool-only protocol.";

    /// <summary>
    /// The rollback transport carries the session bootstrap input and replays the complete ordered transcript with
    /// strict tool-only requests.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionChatCompletions_WithTwoSpeakCalls_ReplaysTranscriptWithStrictToolOnlyRequests(
        bool allowMultipleToolCalls)
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateToolCallResponse("chatcmpl-tool", "call-1", /*lang=json,strict*/ "{\"speech\":\"Hello\"}"),
            CreateToolCallResponse("chatcmpl-second", "call-2", /*lang=json,strict*/ "{\"speech\":\"Again\"}"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        List<string> admittedSpeech = [];
        AIFunction speak = AIFunctionFactory.Create(
            (string speech) =>
            {
                admittedSpeech.Add(speech);
                return "Spoken.";
            },
            "speak",
            "Speak aloud.");

        await RunSessionAsync(client, speak, allowMultipleToolCalls, sessionCancellation);

        Assert.Equal(["Hello", "Again"], admittedSpeech);
        // Two scripted responses served, plus the third request whose cancellation ends the session.
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.All(handler.Paths, path => Assert.Equal("/v1/chat/completions", path));
        // Unset request controls are omitted from the wire entirely: no control field is sent.
        Assert.All(
            handler.RequestBodies,
            body =>
            {
                using var document = JsonDocument.Parse(body);
                AssertNoChatCompletionsSummaryOrVerbosityField(document.RootElement);
                Assert.False(document.RootElement.TryGetProperty("reasoning_effort", out _));
            });
        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        JsonElement[] firstMessages = [.. firstRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal(["system", "user"], firstMessages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal(Instructions, firstMessages[0].GetProperty("content").GetString());
        // Both chat-client kinds carry the session-owner bootstrap input message (AI-002 TR-2).
        Assert.Equal(AgenticMind.SessionBootstrapInput, firstMessages[1].GetProperty("content").GetString());
        AssertStrictToolOnlyRequest(firstRequest.RootElement, allowMultipleToolCalls);
        using var secondRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement[] messages = [.. secondRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal(
            ["system", "user", "assistant", "tool"],
            messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("call-1", messages[3].GetProperty("tool_call_id").GetString());
        AssertStrictToolOnlyRequest(secondRequest.RootElement, allowMultipleToolCalls);
    }

    /// <summary>
    /// Additional speak arguments are rejected before the production function can be invoked or retried.
    /// </summary>
    [Fact]
    public async Task SessionChatCompletions_WithAdditionalSpeakArgument_EndsSessionWithoutInvokingFunction()
    {
        using CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateToolCallResponse("chatcmpl-tool", "call-1", /*lang=json,strict*/ "{\"speech\":\"Hello\",\"unexpected\":true}"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        int invocationCount = 0;
        AIFunction speak = AIFunctionFactory.Create(
            (string speech) =>
            {
                invocationCount++;
                return speech;
            },
            "speak");

        await RunSessionAsync(
            client,
            speak,
            allowMultipleToolCalls: false,
            sessionCancellation,
            new InvalidResponseRecoveryPolicy(2, [TimeSpan.Zero]));

        Assert.Equal(0, invocationCount);
        // The malformed response is discarded, then the one bounded recovery request is cancelled by this session.
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    /// <summary>
    /// A configured reasoning effort is forwarded on every chat-completions request as the documented top-level
    /// <c>reasoning_effort</c> field alongside the preserved strict tool-only payload, with no verbosity or summary
    /// field of any shape.
    /// </summary>
    [Fact]
    public async Task SessionChatCompletions_WithConfiguredReasoningEffort_EmitsEffortOnEveryRequest()
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateToolCallResponse("chatcmpl-tool", "call-1", /*lang=json,strict*/ "{\"speech\":\"Hello\"}"),
            CreateToolCallResponse("chatcmpl-second", "call-2", /*lang=json,strict*/ "{\"speech\":\"Again\"}"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient, ReasoningEffort.Medium);

        await RunSessionAsync(
            client,
            AIFunctionFactory.Create((string speech) => "Spoken.", "speak"),
            allowMultipleToolCalls: false,
            sessionCancellation);

        // Two scripted responses served, plus the third request whose cancellation ends the session: every request
        // through the configured client — including the transcript-replay continuation — carries the control.
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.All(handler.Paths, path => Assert.Equal("/v1/chat/completions", path));
        foreach (string body in handler.RequestBodies)
        {
            using var request = JsonDocument.Parse(body);
            JsonElement root = request.RootElement;
            Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
            AssertNoChatCompletionsSummaryOrVerbosityField(root);
            AssertStrictToolOnlyRequest(root, allowMultipleToolCalls: false);
            Assert.Equal(Instructions, root.GetProperty("messages")[0].GetProperty("content").GetString());
        }

        using var secondRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement[] messages = [.. secondRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal(
            ["system", "user", "assistant", "tool"],
            messages.Select(message => message.GetProperty("role").GetString()));
    }

    /// <summary>
    /// Every permitted reasoning-effort value reaches the Chat Completions wire exactly as top-level
    /// <c>reasoning_effort</c>.
    /// </summary>
    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public async Task SessionChatCompletions_EveryEffortValue_EmitsExactWireValue(
        ReasoningEffort configured,
        string expectedWireValue)
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateToolCallResponse("chatcmpl-tool", "call-1", /*lang=json,strict*/ "{\"speech\":\"Hello\"}"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient, configured);

        await RunSessionAsync(
            client,
            AIFunctionFactory.Create((string speech) => "Spoken.", "speak"),
            allowMultipleToolCalls: false,
            sessionCancellation);

        Assert.Equal(2, handler.RequestBodies.Count);
        foreach (string body in handler.RequestBodies)
        {
            using var request = JsonDocument.Parse(body);
            Assert.Equal(
                expectedWireValue,
                request.RootElement.GetProperty("reasoning_effort").GetString());
        }
    }

    /// <summary>
    /// The Chat Completions endpoint documents no reasoning-summary setting, so a configured summary value fails
    /// closed at client construction — before any request — with a diagnostic naming the setting and the
    /// unsupported path.
    /// </summary>
    [Fact]
    public void CreateChatClient_ConfiguredSummaryOnChatCompletions_FailsClosedBeforeDispatch()
    {
        var handler = new CancellingHandler(CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None));
        using var httpClient = new HttpClient(handler);
        OpenAIClientProvider.OpenAIClientProviderSettings settings = CreateSettings(ReasoningEffort.Medium, "detailed");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            CreateClient(httpClient, ReasoningEffort.Medium, "detailed"));

        Assert.Equal(
            "Config key 'AI/ReasoningVerbosity' ('detailed') is not supported on the Chat Completions endpoint, "
            + "which has no reasoning-summary setting. Unset it to use Chat Completions, or use the Responses "
            + "endpoint.",
            error.Message);
        Assert.Empty(handler.RequestBodies);
        Assert.Equal(ReasoningEffort.Medium, settings.ReasoningEffortLevel);
    }

    /// <summary>
    /// A custom configuration section's name reaches the Chat Completions unsupported-summary diagnostic.
    /// </summary>
    [Fact]
    public void CreateChatClient_ConfiguredSummaryOnChatCompletions_NamesCustomSection()
    {
        var handler = new CancellingHandler(CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None));
        using var httpClient = new HttpClient(handler);
        OpenAIClientProvider.OpenAIClientProviderSettings settings = CreateSettings(ReasoningEffort.Medium, "concise") with
        {
            SectionName = "CustomAI"
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            OpenAIClientProvider.CreateChatClient(
                OpenAIChatClientKind.ChatCompletions,
                settings,
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://unit.test/v1"),
                    Transport = new HttpClientPipelineTransport(httpClient),
                }));

        Assert.StartsWith(
            "Config key 'CustomAI/ReasoningVerbosity' ('concise') is not supported",
            error.Message,
            StringComparison.Ordinal);
        Assert.Empty(handler.RequestBodies);
    }

    /// <summary>
    /// A configured reasoning effort persists across an exact transport retry: the retried request is
    /// byte-identical and every request still carries the control and the strict tool-only payload.
    /// </summary>
    [Fact]
    public async Task SessionChatCompletions_ConfiguredReasoningEffort_PersistsAcrossExactTransportRetries()
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateToolCallResponse("chatcmpl-tool", "call-1", /*lang=json,strict*/ "{\"speech\":\"Hello\"}"),
            CreateToolCallResponse("chatcmpl-second", "call-2", /*lang=json,strict*/ "{\"speech\":\"Again\"}"))
        {
            LeadingTransportFailures = 1,
        };
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient, ReasoningEffort.High);

        await RunSessionAsync(
            client,
            AIFunctionFactory.Create((string speech) => "Spoken.", "speak"),
            allowMultipleToolCalls: false,
            sessionCancellation,
            retryDelays: [TimeSpan.Zero]);

        // One reset connection retried, two scripted responses served, plus the cancelled third request.
        Assert.Equal(4, handler.RequestBodies.Count);
        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
        foreach (string body in handler.RequestBodies)
        {
            using var request = JsonDocument.Parse(body);
            Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
        }
    }

    private static void AssertNoChatCompletionsSummaryOrVerbosityField(JsonElement root)
    {
        Assert.False(root.TryGetProperty("verbosity", out _));
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("reasoning_summary", out _));
        Assert.False(root.TryGetProperty("summary", out _));
    }

    private static async Task RunSessionAsync(
        IChatClient client,
        AIFunction speak,
        bool allowMultipleToolCalls,
        CancellationTokenSource sessionCancellation,
        IInvalidResponseRecoveryPolicy? invalidResponseRecoveryPolicy = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        AgentSessionRunner runner = new(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, AgenticMind.SessionBootstrapInput)],
            [speak],
            allowMultipleToolCalls,
            NullLogger.Instance,
            retryDelays: retryDelays,
            invalidResponseRecoveryPolicy: invalidResponseRecoveryPolicy);
        await runner.RunAsync(sessionCancellation.Token);
    }

    private static IChatClient CreateClient(
        HttpClient httpClient,
        ReasoningEffort? reasoningEffort = null,
        string? reasoningSummary = null)
    {
        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        var settings = new OpenAIClientProvider.OpenAIClientProviderSettings(
            "https://unit.test/v1",
            "not-a-live-key",
            "test-model",
            null,
            reasoningEffort,
            reasoningSummary);
        return OpenAIClientProvider.CreateChatClient(
            OpenAIChatClientKind.ChatCompletions,
            settings,
            clientOptions);
    }

    private static OpenAIClientProvider.OpenAIClientProviderSettings CreateSettings(
        ReasoningEffort? reasoningEffort = null,
        string? reasoningSummary = null)
        => new("https://unit.test/v1", "not-a-live-key", "test-model", null, reasoningEffort, reasoningSummary);

    private static void AssertStrictToolOnlyRequest(JsonElement root, bool allowMultipleToolCalls)
    {
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.Equal(allowMultipleToolCalls, root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(root.TryGetProperty("response_format", out _));
        Assert.Equal(
            ["speak"],
            root.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()));
    }

    private static string CreateToolCallResponse(string responseId, string callId, string arguments)
        => $$"""
            {
              "id": "{{responseId}}",
              "object": "chat.completion",
              "created": 1,
              "model": "test-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{
                    "id": "{{callId}}",
                    "type": "function",
                    "function": { "name": "speak", "arguments": {{JsonSerializer.Serialize(arguments)}} }
                  }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """;

    /// <summary>
    /// Captures every wire request and ends the long-running session by cancelling its token once the scripted
    /// responses are exhausted.
    /// </summary>
    private sealed class CancellingHandler(
        CancellationTokenSource sessionCancellation,
        params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        /// <summary>
        /// Number of leading requests that fail with a transient transport error before scripted responses serve.
        /// </summary>
        public int LeadingTransportFailures
        {
            get; init;
        }

        public List<string> RequestBodies { get; } = [];

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content);
            Assert.NotNull(request.RequestUri);
            Paths.Add(request.RequestUri.AbsolutePath);
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            if (RequestBodies.Count <= LeadingTransportFailures)
            {
                throw new HttpRequestException("reset");
            }

            if (_responses.Count == 0)
            {
                sessionCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }

            string response = _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
