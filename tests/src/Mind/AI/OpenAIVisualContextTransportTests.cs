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
/// Wire-level proof of the AI-013 transport semantic (TR-10) on both shipped OpenAI protocol paths: a generated
/// tool image is model-visible as an image part at its exchange's position. The adjacent presentation mapping is
/// the mandatory fallback — the Chat Completions adapter maps tool-role messages to text-only tool results and
/// drops other content parts in them, while both adapters carry data image parts inside user messages.
/// </summary>
public sealed class OpenAIVisualContextTransportTests
{
    private const string Instructions = "Test the visual-context transport.";

    private const string ToolName = "look_around";

    private static readonly byte[] _imageBytes = [1, 3, 3, 7, 13];

    /// <summary>
    /// The Chat Completions wire carries the generated image at its exchange's position: after the text-only tool
    /// result message, the exchange's presentation message renders as a user message whose content array carries
    /// the label text part followed by an <c>image_url</c> part holding the exact PNG bytes as a data URI.
    /// </summary>
    [Fact]
    public async Task SessionChatCompletions_ImageCarryingExchange_IsModelVisibleAsAnImagePart()
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateToolCallResponse("chatcmpl-tool", "call-1"),
            CreateToolCallResponse("chatcmpl-second", "call-2"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = OpenAIClientProvider.CreateChatClient(
            OpenAIChatClientKind.ChatCompletions,
            CreateSettings(),
            CreateClientOptions(httpClient));

        await RunSessionAsync(client, sessionCancellation);

        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.All(handler.Paths, path => Assert.Equal("/v1/chat/completions", path));
        using var secondRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement[] messages = [.. secondRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal(
            ["system", "user", "assistant", "tool", "user"],
            messages.Select(static message => message.GetProperty("role").GetString()));
        // The tool result itself stays the textual channel (AI-013 TR-5).
        Assert.Equal("call-1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("Here is the map.", messages[3].GetProperty("content").GetString());
        // The presentation message carries the image part on the wire at the exchange's position.
        JsonElement[] presentationParts = [.. messages[4].GetProperty("content").EnumerateArray()];
        Assert.Equal("text", presentationParts[0].GetProperty("type").GetString());
        Assert.Contains(
            "Visual context from your tool action:",
            presentationParts[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("image_url", presentationParts[1].GetProperty("type").GetString());
        AssertDataUriPng(presentationParts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    /// <summary>
    /// The Responses wire — the shipped default client kind — carries the same semantic: after the
    /// <c>function_call_output</c> item of the exchange, the presentation message renders as a user message item
    /// whose content carries the label input text followed by an <c>input_image</c> part holding the exact PNG
    /// bytes as a data URI.
    /// </summary>
    [Fact]
    public async Task SessionResponses_ImageCarryingExchange_IsModelVisibleAsAnImagePart()
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateFunctionCallResponse("resp-1", "call-1"),
            CreateFunctionCallResponse("resp-2", "call-2"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = OpenAIClientProvider.CreateChatClient(
            OpenAIChatClientKind.Responses,
            CreateSettings(),
            CreateClientOptions(httpClient));

        await RunSessionAsync(client, sessionCancellation);

        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.All(handler.Requests, capture => Assert.Equal("/v1/responses", capture.Path));
        using var secondRequest = JsonDocument.Parse(handler.Requests[1].Body);
        JsonElement[] input = [.. secondRequest.RootElement.GetProperty("input").EnumerateArray()];
        Assert.Equal(
            ["message", "function_call", "function_call_output", "message"],
            input.Select(static item => item.GetProperty("type").GetString()));
        // The function output itself stays the textual channel (AI-013 TR-5).
        Assert.Equal("Here is the map.", input[2].GetProperty("output").GetString());
        JsonElement[] presentationParts = [.. input[3].GetProperty("content").EnumerateArray()];
        Assert.Equal("user", input[3].GetProperty("role").GetString());
        Assert.Equal("input_text", presentationParts[0].GetProperty("type").GetString());
        Assert.Contains(
            "Visual context from your tool action:",
            presentationParts[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("input_image", presentationParts[1].GetProperty("type").GetString());
        AssertDataUriPng(presentationParts[1].GetProperty("image_url").GetString());
    }

    private static void AssertDataUriPng(string? dataUri)
    {
        Assert.StartsWith("data:image/png;base64,", dataUri, StringComparison.Ordinal);
        byte[] decoded = Convert.FromBase64String(dataUri!["data:image/png;base64,".Length..]);
        Assert.Equal(_imageBytes, decoded);
    }

    private static async Task RunSessionAsync(IChatClient client, CancellationTokenSource sessionCancellation)
    {
        AgentSessionRunner runner = new(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, AgenticMind.SessionBootstrapInput)],
            [CreateImageFunction()],
            NullLogger.Instance);
        await runner.RunAsync(sessionCancellation.Token);
    }

    private static AIFunction CreateImageFunction()
        => AIFunctionFactory.Create(
            () => new VisualContextDelivery(
                "Here is the map.",
                VisualContextImage.Minimap(
                    _imageBytes,
                    centreX: 1.5,
                    centreY: 2.5,
                    radiusMetres: 10,
                    gameTimeSeconds: 42.5)),
            new AIFunctionFactoryOptions
            {
                Name = ToolName,
                Description = "Look around.",
                ExcludeResultSchema = true,
                MarshalResult = (result, _, _) => ValueTask.FromResult(result),
            });

    private static OpenAIClientProvider.OpenAIClientProviderSettings CreateSettings()
        => new("https://unit.test/v1", "not-a-live-key", "test-model", null);

    private static OpenAIClientOptions CreateClientOptions(HttpClient httpClient)
        => new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };

    private static string CreateToolCallResponse(string responseId, string callId)
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
                    "function": { "name": "{{ToolName}}", "arguments": "{}" }
                  }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """;

    private static string CreateFunctionCallResponse(string responseId, string callId)
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
                "name": "{{ToolName}}",
                "arguments": "{}",
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

        public List<string> RequestBodies { get; } = [];

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content);
            Assert.NotNull(request.RequestUri);
            string body = await request.Content.ReadAsStringAsync(cancellationToken);
            Paths.Add(request.RequestUri.AbsolutePath);
            RequestBodies.Add(body);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri.AbsolutePath, body));

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
