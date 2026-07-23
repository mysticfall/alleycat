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
/// Verifies wire-level OpenAI chat-completions request composition without a live backend.
/// </summary>
public sealed class OpenAIChatCompletionsTransportTests
{
    private const string Instructions = "Test the tool-only protocol.";

    /// <summary>
    /// The tool-only route accepts one speak call through the production function, then requires end_turn.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderChatCompletionsRollback_MapsMultipleToolCallsAndPreservesStrictToolOnlyRequests(
        bool allowMultipleToolCalls)
    {
        var handler = new CapturingHandler(CreateToolCallResponse(/*lang=json,strict*/ "{\"speech\":\"Hello\"}"), CreateEndTurnCallResponse());
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

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [speak],
            allowMultipleToolCalls,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["Hello"], admittedSpeech);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.All(handler.Paths, path => Assert.Equal("/v1/chat/completions", path));
        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        JsonElement[] firstMessages = [.. firstRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal("system", Assert.Single(firstMessages).GetProperty("role").GetString());
        Assert.Equal(Instructions, Assert.Single(firstMessages).GetProperty("content").GetString());
        AssertStrictToolOnlyRequest(firstRequest.RootElement, allowMultipleToolCalls);
        using var secondRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement[] messages = [.. secondRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal(["system", "assistant", "tool"], messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("call-1", messages[2].GetProperty("tool_call_id").GetString());
        AssertStrictToolOnlyRequest(secondRequest.RootElement, allowMultipleToolCalls);
    }

    /// <summary>
    /// Chat Completions maps an action and final marker from one wire payload and completes without replay.
    /// </summary>
    [Fact]
    public async Task ProviderChatCompletionsRollback_WithSpeakAndFinalMarker_CompletesAfterOneWireRequest()
    {
        var handler = new CapturingHandler(CreateCombinedToolCallResponse());
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        List<string> admittedSpeech = [];
        AIFunction speak = AIFunctionFactory.Create(
            (string speech) => admittedSpeech.Add(speech),
            "speak",
            "Speak aloud.");

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [speak],
            true,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["Hello"], admittedSpeech);
        string requestBody = Assert.Single(handler.RequestBodies);
        using var payload = JsonDocument.Parse(requestBody);
        AssertStrictToolOnlyRequest(payload.RootElement, true);
    }

    /// <summary>
    /// Additional speak arguments are rejected before the production function can be invoked or retried.
    /// </summary>
    [Fact]
    public async Task ToolOnlyRoute_WithAdditionalSpeakArgument_RejectsWithoutInvokingFunction()
    {
        var handler = new CapturingHandler(
            CreateToolCallResponse(/*lang=json,strict*/ "{\"speech\":\"Hello\",\"unexpected\":true}"));
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

        _ = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            [],
            [speak],
            false,
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal(0, invocationCount);
        _ = Assert.Single(handler.RequestBodies);
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
            ["speak", ToolOnlyTurnRunner.EndTurnToolName],
            root.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()));

        JsonElement endTurn = Assert.Single(
            root.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("function").GetProperty("name").GetString()
                == ToolOnlyTurnRunner.EndTurnToolName).GetProperty("function");
        Assert.True(endTurn.GetProperty("strict").GetBoolean());
        Assert.Contains("final position", endTurn.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("waiting for action results", endTurn.GetProperty("description").GetString(), StringComparison.Ordinal);
        JsonElement parameters = endTurn.GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Empty(parameters.GetProperty("properties").EnumerateObject());
        Assert.Empty(parameters.GetProperty("required").EnumerateArray());
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
    }

    private static string CreateToolCallResponse(string arguments)
        => $$"""
            {
              "id": "chatcmpl-tool",
              "object": "chat.completion",
              "created": 1,
              "model": "test-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{
                    "id": "call-1",
                    "type": "function",
                    "function": { "name": "speak", "arguments": {{JsonSerializer.Serialize(arguments)}} }
                  }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """;

    private static string CreateEndTurnCallResponse()
        => /*lang=json,strict*/ """
            {
              "id": "chatcmpl-end",
              "object": "chat.completion",
              "created": 1,
              "model": "test-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{
                    "id": "end-call",
                    "type": "function",
                    "function": { "name": "end_turn", "arguments": "{}" }
                  }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """;

    private static string CreateCombinedToolCallResponse()
        => /*lang=json,strict*/ """
            {
              "id": "chatcmpl-combined",
              "object": "chat.completion",
              "created": 1,
              "model": "test-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [
                    {
                      "id": "call-1",
                      "type": "function",
                      "function": { "name": "speak", "arguments": "{\"speech\":\"Hello\"}" }
                    },
                    {
                      "id": "end-call",
                      "type": "function",
                      "function": { "name": "end_turn", "arguments": "{}" }
                    }
                  ]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """;

    private sealed class CapturingHandler(params string[] responses) : HttpMessageHandler
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

            string response = _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
