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
        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        JsonElement[] firstMessages = [.. firstRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal(["system", "user"], firstMessages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal(Instructions, firstMessages[0].GetProperty("content").GetString());
        // Both chat-client kinds carry the session-owner bootstrap input message (AI-002 TR-7).
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
        var handler = new CancellingHandler(
            CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None),
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

        _ = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => RunSessionAsync(
            client,
            speak,
            allowMultipleToolCalls: false,
            CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None)));

        Assert.Equal(0, invocationCount);
        _ = Assert.Single(handler.RequestBodies);
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
        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        var settings = new OpenAIClientProvider.OpenAIClientProviderSettings(
            "https://unit.test/v1",
            "not-a-live-key",
            "test-model",
            null);
        return OpenAIClientProvider.CreateChatClient(
            OpenAIChatClientKind.ChatCompletions,
            settings,
            clientOptions);
    }

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
