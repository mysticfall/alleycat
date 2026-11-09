using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Provider;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Wire-level proof of the AI-013 transport semantic (TR-10) on both shipped OpenAI protocol paths: a generated
/// tool image is model-visible as an image part at its exchange's position. The adjacent presentation mapping is
/// the mandatory fallback — the Chat Completions adapter maps tool-role messages to text-only tool results and
/// drops other content parts in them, while both adapters carry data image parts inside user messages — and the
/// fabricated auto-mode exchange additionally rides its images in-result on the Responses path.
/// </summary>
public sealed class OpenAIVisualContextTransportTests
{
    private const string Instructions = "Test the visual-context transport.";

    private const string ToolName = "look_around";

    private static readonly byte[] _imageBytes = [1, 3, 3, 7, 13];

    private static readonly byte[] _viewBytes = [9, 9, 2, 4];

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

    /// <summary>
    /// The fabricated auto-mode exchange on a Responses-kind endpoint — here selected by a configured
    /// <c>AI:ChatClientKind</c> override beating the resource property (AI-002 TR-35–TR-36) — rides each image
    /// inside its fabricated function-call output (AI-013 TR-8/TR-10): the request carries the assistant
    /// <c>minimap</c>/<c>capture_view</c> calls with empty arguments and synthetic call IDs, each followed by a
    /// <c>function_call_output</c> whose output pairs the TR-11 instructional text with an image part holding the
    /// exact generated bytes, with no adjacent user carrier before the scene-status message.
    /// </summary>
    [Fact]
    public async Task SessionResponses_FabricatedAutoExchange_CarriesItsImagesInsideTheFunctionCallOutputs()
    {
        OpenAIClientProvider.OpenAIClientProviderSettings settings = CreateConfiguredSettings(" Responses ");
        OpenAIChatClientKind effectiveKind = OpenAIClientProvider.ResolveEffectiveChatClientKind(
            settings.ChatClientKind,
            OpenAIChatClientKind.ChatCompletions);
        Assert.Equal(OpenAIChatClientKind.Responses, effectiveKind);
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateFunctionCallResponse("resp-1", "call-1"),
            CreateFunctionCallResponse("resp-2", "call-2"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = OpenAIClientProvider.CreateChatClient(
            effectiveKind,
            settings,
            CreateClientOptions(httpClient));

        await RunSessionAsync(client, sessionCancellation, new FabricatedExchangeContextSource(effectiveKind));

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, capture => Assert.Equal("/v1/responses", capture.Path));
        using var secondRequest = JsonDocument.Parse(handler.Requests[1].Body);
        JsonElement[] input = [.. secondRequest.RootElement.GetProperty("input").EnumerateArray()];
        Assert.Equal(
            [
                "message",
                "function_call",
                "function_call_output",
                "message",
                "function_call",
                "function_call",
                "function_call_output",
                "function_call_output",
                "message",
            ],
            input.Select(static item => item.GetProperty("type").GetString()));
        // The retained real exchange keeps its adjacent user-message presentation on this path (TR-10).
        Assert.Equal("user", input[3].GetProperty("role").GetString());
        // The fabricated assistant message carries both real tool names with zero-argument arguments and
        // synthetic call IDs from the distinct sub-namespace (AI-013 TR-8; AI-002 TR-18).
        Assert.Equal("minimap", input[4].GetProperty("name").GetString());
        Assert.Equal("{}", input[4].GetProperty("arguments").GetString());
        Assert.Equal(
            "runtime-visual-context-3",
            input[4].GetProperty("call_id").GetString());
        Assert.Equal("capture_view", input[5].GetProperty("name").GetString());
        Assert.Equal("{}", input[5].GetProperty("arguments").GetString());
        Assert.Equal(
            "runtime-visual-context-4",
            input[5].GetProperty("call_id").GetString());
        AssertFabricatedOutput(input[6], "runtime-visual-context-3", VisualContextAutoExchange.MinimapInstruction, "image/png", _imageBytes);
        AssertFabricatedOutput(input[7], "runtime-visual-context-4", VisualContextAutoExchange.ViewInstruction, "image/jpeg", _viewBytes);
        // No adjacent user carrier exists on this path: the scene-status message directly follows the outputs.
        Assert.Equal("user", input[8].GetProperty("role").GetString());
        Assert.Equal("scene:1", input[8].GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// The fabricated auto-mode exchange on a Chat-Completions-kind endpoint — here the resource property itself
    /// through an unset configuration key (AI-002 TR-36) — keeps every tool result text-only and rides each image
    /// in its own adjacent user carrier (AI-013 TR-10): the request carries the fabricated assistant tool calls,
    /// text-only tool messages carrying the TR-11 instructional texts, then one user message per image pairing
    /// that image's instructional text with its image part, before the scene-status message.
    /// </summary>
    [Fact]
    public async Task SessionChatCompletions_FabricatedAutoExchange_CarriesItsImagesInAdjacentUserCarriers()
    {
        OpenAIClientProvider.OpenAIClientProviderSettings settings = CreateConfiguredSettings(null);
        OpenAIChatClientKind effectiveKind = OpenAIClientProvider.ResolveEffectiveChatClientKind(
            settings.ChatClientKind,
            OpenAIChatClientKind.ChatCompletions);
        Assert.Equal(OpenAIChatClientKind.ChatCompletions, effectiveKind);
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateToolCallResponse("chatcmpl-tool", "call-1"),
            CreateToolCallResponse("chatcmpl-second", "call-2"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = OpenAIClientProvider.CreateChatClient(
            effectiveKind,
            settings,
            CreateClientOptions(httpClient));

        await RunSessionAsync(client, sessionCancellation, new FabricatedExchangeContextSource(effectiveKind));

        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.All(handler.Paths, path => Assert.Equal("/v1/chat/completions", path));
        using var secondRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement[] messages = [.. secondRequest.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal(
            [
                "system",
                "user",
                "assistant",
                "tool",
                "user",
                "assistant",
                "tool",
                "tool",
                "user",
                "user",
                "user",
            ],
            messages.Select(static message => message.GetProperty("role").GetString()));
        // The fabricated assistant message carries both real tool names with empty arguments and synthetic call
        // IDs from the distinct sub-namespace (AI-013 TR-8; AI-002 TR-18).
        JsonElement[] fabricatedCalls = [.. messages[5].GetProperty("tool_calls").EnumerateArray()];
        Assert.Equal(
            ["minimap", "capture_view"],
            fabricatedCalls.Select(static call => call.GetProperty("function").GetProperty("name").GetString()));
        Assert.All(
            fabricatedCalls,
            call => Assert.Equal("{}", call.GetProperty("function").GetProperty("arguments").GetString()));
        Assert.Equal(
            ["runtime-visual-context-3", "runtime-visual-context-4"],
            fabricatedCalls.Select(static call => call.GetProperty("id").GetString()));
        // The fabricated tool results stay the textual channel: each carries only its TR-11 instructional text.
        Assert.Equal("runtime-visual-context-3", messages[6].GetProperty("tool_call_id").GetString());
        Assert.Equal(VisualContextAutoExchange.MinimapInstruction, messages[6].GetProperty("content").GetString());
        Assert.Equal("runtime-visual-context-4", messages[7].GetProperty("tool_call_id").GetString());
        Assert.Equal(VisualContextAutoExchange.ViewInstruction, messages[7].GetProperty("content").GetString());
        // Each image rides exactly one adjacent user carrier: its instructional text plus its image part, placed
        // after the fabricated exchange and before the scene-status message (AI-013 TR-10).
        JsonElement[] minimapCarrierParts = [.. messages[8].GetProperty("content").EnumerateArray()];
        Assert.Equal("text", minimapCarrierParts[0].GetProperty("type").GetString());
        Assert.Equal(VisualContextAutoExchange.MinimapInstruction, minimapCarrierParts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", minimapCarrierParts[1].GetProperty("type").GetString());
        AssertDataUri(minimapCarrierParts[1].GetProperty("image_url").GetProperty("url").GetString(), "image/png", _imageBytes);
        JsonElement[] viewCarrierParts = [.. messages[9].GetProperty("content").EnumerateArray()];
        Assert.Equal("text", viewCarrierParts[0].GetProperty("type").GetString());
        Assert.Equal(VisualContextAutoExchange.ViewInstruction, viewCarrierParts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", viewCarrierParts[1].GetProperty("type").GetString());
        AssertDataUri(viewCarrierParts[1].GetProperty("image_url").GetProperty("url").GetString(), "image/jpeg", _viewBytes);
        Assert.Equal("scene:1", messages[10].GetProperty("content").GetString());
    }

    private static void AssertDataUri(string? dataUri, string mediaType, byte[] expectedBytes)
    {
        Assert.StartsWith($"data:{mediaType};base64,", dataUri, StringComparison.Ordinal);
        byte[] decoded = Convert.FromBase64String(dataUri![$"data:{mediaType};base64,".Length..]);
        Assert.Equal(expectedBytes, decoded);
    }

    /// <summary>
    /// Asserts one fabricated Responses-path function-call output (AI-013 TR-10): its call ID, the TR-11
    /// instructional text, and the in-result image part holding the exact generated bytes as a data URI.
    /// </summary>
    private static void AssertFabricatedOutput(
        JsonElement output,
        string callId,
        string instruction,
        string mediaType,
        byte[] expectedBytes)
    {
        Assert.Equal(callId, output.GetProperty("call_id").GetString());
        JsonElement[] outputParts = [.. output.GetProperty("output").EnumerateArray()];
        Assert.Equal("input_text", outputParts[0].GetProperty("type").GetString());
        Assert.Equal(instruction, outputParts[0].GetProperty("text").GetString());
        Assert.Equal("input_image", outputParts[1].GetProperty("type").GetString());
        AssertDataUri(outputParts[1].GetProperty("image_url").GetString(), mediaType, expectedBytes);
    }

    private static void AssertDataUriPng(string? dataUri)
    {
        Assert.StartsWith("data:image/png;base64,", dataUri, StringComparison.Ordinal);
        byte[] decoded = Convert.FromBase64String(dataUri!["data:image/png;base64,".Length..]);
        Assert.Equal(_imageBytes, decoded);
    }

    private static async Task RunSessionAsync(
        IChatClient client,
        CancellationTokenSource sessionCancellation,
        IAgentRequestContextSource? requestContextSource = null)
    {
        AgentSessionRunner runner = new(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, AgenticMind.SessionBootstrapInput)],
            [CreateImageFunction()],
            NullLogger.Instance,
            requestContextSource: requestContextSource);
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

    /// <summary>
    /// Loads provider settings from an in-memory <c>AI</c> configuration section, so the fabricated-exchange
    /// transport tests exercise the real <c>AI:ChatClientKind</c> binding and effective-kind resolution
    /// (AI-002 TR-35–TR-36) instead of hand-built settings.
    /// </summary>
    private static OpenAIClientProvider.OpenAIClientProviderSettings CreateConfiguredSettings(string? chatClientKind)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AI:Host"] = "https://unit.test/v1",
                ["AI:ApiKey"] = "not-a-live-key",
                ["AI:Model"] = "test-model",
                ["AI:ChatClientKind"] = chatClientKind,
            }).Build();
        return OpenAIClientProvider.OpenAIClientProviderSettings.Load(configuration, "unit-config");
    }

    private static OpenAIClientOptions CreateClientOptions(HttpClient httpClient)
        => new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };

    /// <summary>
    /// Request-context source fabricating the auto-mode visual-context exchange through the production composer
    /// (AI-013 TR-8): each materialisation rebuilds the exchange with fresh fabricated call IDs and renders the
    /// fresh scene-status message last, immediately after the exchange.
    /// </summary>
    private sealed class FabricatedExchangeContextSource(OpenAIChatClientKind chatClientKind)
        : IAgentRequestContextSource
    {
        private long _callSequence;
        private int _materialisationCount;

        public ValueTask<AgentRequestContext> MaterialiseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = _materialisationCount++;
            IReadOnlyList<ChatMessage> exchange = VisualContextAutoExchange.Compose(
                [
                    VisualContextImage.Minimap(_imageBytes, centreX: 1.5, centreY: 2.5, radiusMetres: 10, gameTimeSeconds: 42.5),
                    VisualContextImage.View(_viewBytes, gameTimeSeconds: 42.5),
                ],
                chatClientKind,
                () => VisualContextAutoExchange.FormatCallId(++_callSequence));
            return ValueTask.FromResult(new AgentRequestContext(
                [],
                [.. exchange, new ChatMessage(ChatRole.User, $"scene:{index}")],
                index));
        }

        public void Retain(object? confirmation)
        {
        }

        public void Confirm(object? confirmation)
        {
        }

        public void Discard(object? confirmation)
        {
        }
    }

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
