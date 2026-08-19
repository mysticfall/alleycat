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
/// Verifies wire-level stateless Responses request composition for the agent session without a live backend.
/// </summary>
public sealed class OpenAIResponsesTransportTests
{
    private const string Instructions = "Test the tool-only protocol.";
    private const string CallID = "call_sanitised_1";

    /// <summary>
    /// The session uses stateless Responses calls with required tools and full local replay.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionResponses_WithTwoSpeakCalls_EmitsRequiredStatelessRequests(
        bool allowMultipleToolCalls)
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateSpeakCallResponse("resp_sanitised_tool", CallID, "Hello"),
            CreateSpeakCallResponse("resp_sanitised_second", "call_sanitised_2", "Again"));
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
            "Speak through the character-owned voice.");

        await RunSessionAsync(client, speak, allowMultipleToolCalls, sessionCancellation);

        Assert.Equal(["Hello", "Again"], admittedSpeech);
        // Two scripted responses served, plus the third request whose cancellation ends the session.
        Assert.Equal(3, handler.Requests.Count);
        for (int index = 0; index < 2; index++)
        {
            CapturedRequest capture = handler.Requests[index];
            Assert.Equal(HttpMethod.Post, capture.Method);
            Assert.Equal("/v1/responses", capture.Path);
            using var request = JsonDocument.Parse(capture.Body);
            JsonElement root = request.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("previous_response_id", out _));
            Assert.Equal(Instructions, root.GetProperty("instructions").GetString());
            Assert.Equal("required", root.GetProperty("tool_choice").GetString());
            Assert.Equal(allowMultipleToolCalls, root.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.False(root.TryGetProperty("text", out JsonElement text)
                && text.TryGetProperty("format", out JsonElement format)
                && format.GetProperty("type").GetString() == "json_schema");
            Assert.Equal(
                ["speak"],
                root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
            JsonElement bootstrapMessage = Assert.Single(
                root.GetProperty("input").EnumerateArray(),
                item => item.GetProperty("type").GetString() == "message"
                    && item.GetProperty("role").GetString() == "user");
            Assert.Equal(
                AgenticMind.SessionBootstrapInput,
                Assert.Single(bootstrapMessage.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        }

        using var secondRequest = JsonDocument.Parse(handler.Requests[1].Body);
        JsonElement[] replay = [.. secondRequest.RootElement.GetProperty("input").EnumerateArray()];
        JsonElement functionCall = Assert.Single(
            replay,
            item => item.GetProperty("type").GetString() == "function_call");
        Assert.Equal("fc_sanitised_1", functionCall.GetProperty("id").GetString());
        Assert.Equal(CallID, functionCall.GetProperty("call_id").GetString());
        Assert.Equal("speak", functionCall.GetProperty("name").GetString());
        Assert.Equal(/*lang=json,strict*/ "{\"speech\":\"Hello\"}", functionCall.GetProperty("arguments").GetString());
        JsonElement functionResult = Assert.Single(
            replay,
            item => item.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal(CallID, functionResult.GetProperty("call_id").GetString());
        Assert.Equal(
            "Spoken.",
            JsonSerializer.Deserialize<string>(functionResult.GetProperty("output").GetString()!));
    }

    /// <summary>
    /// The provider defaults to Responses and rejects unknown adapter values instead of silently falling back.
    /// </summary>
    [Fact]
    public void ProviderSelection_DefaultsToResponsesAndUnsupportedKindFailsClosed()
    {
        Assert.Equal(OpenAIChatClientKind.Responses, OpenAIClientProvider.DefaultChatClientKind);

        var handler = new CancellingHandler(
            CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None));
        using var httpClient = new HttpClient(handler);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            OpenAIClientProvider.CreateChatClient(
                (OpenAIChatClientKind)int.MaxValue,
                CreateSettings(),
                CreateClientOptions(httpClient)));

        Assert.Contains("Unsupported OpenAI chat client kind", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    private static async Task RunSessionAsync(
        IChatClient client,
        AIFunction speak,
        bool allowMultipleToolCalls,
        CancellationTokenSource sessionCancellation)
    {
        AgentSessionRunner runner = new(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, AgenticMind.SessionBootstrapInput)],
            [speak],
            allowMultipleToolCalls,
            NullLogger.Instance);
        await runner.RunAsync(sessionCancellation.Token);
    }

    private static IChatClient CreateClient(HttpClient httpClient)
    {
        Assert.Equal(OpenAIChatClientKind.Responses, OpenAIClientProvider.DefaultChatClientKind);
        return OpenAIClientProvider.CreateChatClient(
            OpenAIClientProvider.DefaultChatClientKind,
            CreateSettings(),
            CreateClientOptions(httpClient));
    }

    private static OpenAIClientProvider.OpenAIClientProviderSettings CreateSettings()
        => new("https://unit.test/v1", "not-a-live-key", "test-model", null);

    private static OpenAIClientOptions CreateClientOptions(HttpClient httpClient)
        => new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };

    private static string CreateSpeakCallResponse(string responseId, string callId, string speech)
        => $$"""
            {
              "id": "{{responseId}}",
              "object": "response",
              "created_at": 1,
              "status": "completed",
              "error": null,
              "incomplete_details": null,
              "instructions": null,
              "model": "test-model",
              "output": [{
                "type": "function_call",
                "id": "fc_sanitised_1",
                "call_id": "{{callId}}",
                "name": "speak",
                "arguments": {{JsonSerializer.Serialize(JsonSerializer.Serialize(new Dictionary<string, string> { ["speech"] = speech }))}},
                "status": "completed"
              }],
              "parallel_tool_calls": true,
              "previous_response_id": null,
              "store": false,
              "tool_choice": "auto",
              "tools": [],
              "usage": {
                "input_tokens": 1,
                "input_tokens_details": { "cached_tokens": 0 },
                "output_tokens": 1,
                "output_tokens_details": { "reasoning_tokens": 0 },
                "total_tokens": 2
              }
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

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content);
            Assert.NotNull(request.RequestUri);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri.AbsolutePath,
                await request.Content.ReadAsStringAsync(cancellationToken)));

            if (_responses.Count == 0)
            {
                sessionCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string Body);
}
