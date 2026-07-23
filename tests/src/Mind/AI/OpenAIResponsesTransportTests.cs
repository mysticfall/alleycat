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
/// Verifies wire-level stateless Responses request composition without a live backend.
/// </summary>
public sealed class OpenAIResponsesTransportTests
{
    private const string Instructions = "Test the tool-only protocol.";
    private const string CallID = "call_sanitised_1";

    /// <summary>
    /// The production loop uses stateless Responses calls with required tools and full local replay.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDefaultResponses_WithSpeakThenEnd_EmitsRequiredStatelessRequests(
        bool allowMultipleToolCalls)
    {
        var handler = new CapturingHandler(
            CreateToolCallResponse(),
            CreateEndTurnResponse());
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

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            OpenAIClientProvider.CreateRunMessages(OpenAIChatClientKind.Responses),
            [speak],
            allowMultipleToolCalls,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["Hello"], admittedSpeech);
        Assert.Equal(2, handler.Requests.Count);
        const string bootstrap = "Process the observations in your instructions. Use available actions as needed. Call end_turn exactly once in final position, after the actions when their results are not needed, or alone for zero actions. Omit end_turn when waiting for action results.";
        for (int index = 0; index < handler.Requests.Count; index++)
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
                ["speak", ToolOnlyTurnRunner.EndTurnToolName],
                root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
            AssertReservedEndTurnSchema(root);
            JsonElement bootstrapMessage = Assert.Single(
                root.GetProperty("input").EnumerateArray(),
                item => item.GetProperty("type").GetString() == "message"
                    && item.GetProperty("role").GetString() == "user");
            Assert.Equal(
                bootstrap,
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
    /// Responses maps an action and final marker from one wire payload and completes without replay.
    /// </summary>
    [Fact]
    public async Task ProviderDefaultResponses_WithSpeakAndFinalMarker_CompletesAfterOneWireRequest()
    {
        var handler = new CapturingHandler(CreateCombinedToolCallResponse());
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        List<string> admittedSpeech = [];
        AIFunction speak = AIFunctionFactory.Create(
            (string speech) => admittedSpeech.Add(speech),
            "speak",
            "Speak through the character-owned voice.");

        await ToolOnlyTurnRunner.RunAsync(
            client,
            Instructions,
            OpenAIClientProvider.CreateRunMessages(OpenAIChatClientKind.Responses),
            [speak],
            true,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(["Hello"], admittedSpeech);
        CapturedRequest request = Assert.Single(handler.Requests);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("required", payload.RootElement.GetProperty("tool_choice").GetString());
        Assert.True(payload.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
    }

    /// <summary>
    /// The provider defaults to Responses and rejects unknown adapter values instead of silently falling back.
    /// </summary>
    [Fact]
    public void ProviderSelection_DefaultsToResponsesAndUnsupportedKindFailsClosed()
    {
        Assert.Equal(OpenAIChatClientKind.Responses, OpenAIClientProvider.DefaultChatClientKind);

        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            OpenAIClientProvider.CreateChatClient(
                (OpenAIChatClientKind)int.MaxValue,
                CreateSettings(),
                CreateClientOptions(httpClient)));

        Assert.Contains("Unsupported OpenAI chat client kind", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        _ = Assert.Throws<InvalidOperationException>(() =>
            OpenAIClientProvider.CreateRunMessages((OpenAIChatClientKind)int.MaxValue));
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

    private static void AssertReservedEndTurnSchema(JsonElement root)
    {
        JsonElement endTurn = Assert.Single(
            root.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == ToolOnlyTurnRunner.EndTurnToolName);
        Assert.True(endTurn.GetProperty("strict").GetBoolean());
        Assert.Contains("final position", endTurn.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("waiting for action results", endTurn.GetProperty("description").GetString(), StringComparison.Ordinal);
        JsonElement parameters = endTurn.GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Empty(parameters.GetProperty("properties").EnumerateObject());
        Assert.Empty(parameters.GetProperty("required").EnumerateArray());
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
    }

    private static string CreateToolCallResponse()
        => $$"""
            {
              "id": "resp_sanitised_tool",
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
                "call_id": "{{CallID}}",
                "name": "speak",
                "arguments": "{\"speech\":\"Hello\"}",
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

    private static string CreateEndTurnResponse()
        => /*lang=json,strict*/ """
            {
              "id": "resp_sanitised_end",
              "object": "response",
              "created_at": 2,
              "status": "completed",
              "error": null,
              "incomplete_details": null,
              "instructions": null,
              "model": "test-model",
              "output": [{
                "type": "function_call",
                "id": "fc_sanitised_end",
                "call_id": "call_sanitised_end",
                "name": "end_turn",
                "arguments": "{}",
                "status": "completed"
              }],
              "parallel_tool_calls": false,
              "previous_response_id": null,
              "store": false,
              "tool_choice": "required",
              "tools": [],
              "usage": {
                "input_tokens": 2,
                "input_tokens_details": { "cached_tokens": 0 },
                "output_tokens": 1,
                "output_tokens_details": { "reasoning_tokens": 0 },
                "total_tokens": 3
              }
            }
            """;

    private static string CreateCombinedToolCallResponse()
        => $$"""
            {
              "id": "resp_sanitised_combined",
              "object": "response",
              "created_at": 1,
              "status": "completed",
              "error": null,
              "incomplete_details": null,
              "instructions": null,
              "model": "test-model",
              "output": [
                {
                  "type": "function_call",
                  "id": "fc_sanitised_combined_speak",
                  "call_id": "{{CallID}}",
                  "name": "speak",
                  "arguments": "{\"speech\":\"Hello\"}",
                  "status": "completed"
                },
                {
                  "type": "function_call",
                  "id": "fc_sanitised_combined_end",
                  "call_id": "call_sanitised_end",
                  "name": "end_turn",
                  "arguments": "{}",
                  "status": "completed"
                }
              ],
              "parallel_tool_calls": true,
              "previous_response_id": null,
              "store": false,
              "tool_choice": "required",
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

    private sealed class CapturingHandler(params string[] responses) : HttpMessageHandler
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

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string Body);
}
