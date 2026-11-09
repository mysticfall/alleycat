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
/// Derivation contract of the character-derived llama-server slot pin (AI-002 TR-38): deterministic, stable
/// across sessions, process launches, and machines, non-negative, and based on a stable hash of the identity's
/// UTF-8 bytes — never the per-process randomised <c>string.GetHashCode()</c> — plus the TR-37 switch
/// resolution that keeps the default provider injection-free.
/// </summary>
public sealed class SlotIdDerivationTests
{
    /// <inheritdoc/>
    public static TheoryData<string> CharacterFullIds =>
    [
        "char:owner",
        "char:newcomer",
        "char:sophia",
        "npc:moss",
        // Canonical identities are not restricted to Latin script (SCN identity text permits non-Latin ids).
        "char:안녕",
    ];

    /// <inheritdoc/>
    [Theory]
    [MemberData(nameof(CharacterFullIds))]
    public void DeriveSlotId_YieldsTheSameNonNegativeValueAcrossRepeatedDerivations(string fullId)
    {
        int first = OpenAIClientProvider.DeriveSlotId(fullId);
        Assert.InRange(first, 0, int.MaxValue);

        // Simulated sessions and launches: re-derivation through independent expressions over time must keep
        // yielding the identical value, because the mapping is a pure function of the identity's bytes.
        for (int derivation = 0; derivation < 5; derivation++)
        {
            Assert.Equal(first, OpenAIClientProvider.DeriveSlotId(fullId));
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void DeriveSlotId_GoldenValueIsStableAcrossLaunchesAndMachines() =>
        // Golden value computed independently of the production expression: any change to the mapping —
        // including a regression to per-process randomised hashing, which yields a different number in every
        // launch — changes this value and fails here.
        Assert.Equal(313875300, OpenAIClientProvider.DeriveSlotId("char:owner"));

    /// <inheritdoc/>
    [Theory]
    [MemberData(nameof(CharacterFullIds))]
    public void DeriveSlotId_MatchesTheIndependentStableByteHashReference(string fullId) =>
        // Independent oracle: FNV-1a 64 over the identity's UTF-8 bytes, folded to a non-negative 31-bit
        // integer, exactly as the production contract documents. This distinguishes a genuine stable byte
        // hash from any per-process or culture-varying hash.
        Assert.Equal(ReferenceStableFold(fullId), OpenAIClientProvider.DeriveSlotId(fullId));

    /// <inheritdoc/>
    [Fact]
    public void DeriveSlotId_DistinctCharactersProduceTheirOwnStableValues()
    {
        // This chosen pair is proven distinct; collisions between other characters remain permitted and
        // harmless (llama-server wraps the id modulo its slot count — AI-002 TR-38).
        Assert.NotEqual(
            OpenAIClientProvider.DeriveSlotId("char:owner"),
            OpenAIClientProvider.DeriveSlotId("char:newcomer"));
    }

    /// <inheritdoc/>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void DeriveSlotId_RejectsAMissingIdentity(string? fullId)
    {
        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(
            () => OpenAIClientProvider.DeriveSlotId(fullId!));
        Assert.Contains("fullId", exception.Message, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    [Fact]
    public void ResolveSlotId_DisabledSwitchResolvesNoInjection()
        => Assert.Null(OpenAIClientProvider.ResolveSlotId(enableSlotPinning: false, subjectFullId: "char:owner"));

    /// <inheritdoc/>
    [Fact]
    public void ResolveSlotId_EnabledSwitchFollowsTheSubjectCharacter()
    {
        Assert.Equal(
            OpenAIClientProvider.DeriveSlotId("char:owner"),
            OpenAIClientProvider.ResolveSlotId(enableSlotPinning: true, subjectFullId: "char:owner"));
        Assert.Equal(
            OpenAIClientProvider.DeriveSlotId("char:newcomer"),
            OpenAIClientProvider.ResolveSlotId(enableSlotPinning: true, subjectFullId: "char:newcomer"));
    }

    /// <summary>
    /// Reference implementation of the documented derivation, written independently of the production code so
    /// the oracle cannot drift with an implementation regression.
    /// </summary>
    private static int ReferenceStableFold(string fullId)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        ulong hash = OffsetBasis;
        foreach (byte octet in Encoding.UTF8.GetBytes(fullId))
        {
            hash ^= octet;
            hash *= Prime;
        }

        return (int)((hash ^ (hash >> 32)) & 0x7FFF_FFFF);
    }
}

/// <summary>
/// Wire-level proof of the provider slot pin (AI-002 TR-37) on both shipped OpenAI protocol paths: an enabled
/// pin stamps every outgoing request body with a top-level <c>id_slot</c> field carrying the value derived for
/// the constructed-for character — fixed for the client's lifetime, no per-call variation — while the default
/// disabled provider sends bodies containing no <c>id_slot</c> anywhere.
/// </summary>
public sealed class SlotPinningTransportTests
{
    private const string Instructions = "Test the slot pinning transport.";

    private const string ToolName = "ping_tool";

    /// <inheritdoc/>
    [Theory]
    [InlineData(OpenAIChatClientKind.ChatCompletions)]
    [InlineData(OpenAIChatClientKind.Responses)]
    public async Task EnabledPin_StampedEveryRequestBody_WithTheCharacterDerivedSlot(OpenAIChatClientKind kind)
    {
        const string subjectFullId = "char:sophia";
        int expectedSlot = OpenAIClientProvider.DeriveSlotId(subjectFullId);
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateResponse(kind, "first", "call-1"),
            CreateResponse(kind, "second", "call-2"));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreatePinnedClient(kind, httpClient, subjectFullId);

        await RunSessionAsync(client, sessionCancellation.Token);

        string expectedPath = kind == OpenAIChatClientKind.ChatCompletions
            ? "/v1/chat/completions"
            : "/v1/responses";
        Assert.Equal(3, handler.Requests.Count);
        // No per-call variation: every request body of the constructed client carries the same fixed derived
        // value, and the rewrite preserves the endpoint's JSON media type.
        foreach (CapturedRequest captured in handler.Requests)
        {
            Assert.Equal(expectedPath, captured.Path);
            Assert.Equal("application/json", captured.ContentType);
            (int slotCount, int slotValue) = CountTopLevelIdSlot(captured.Body);
            Assert.Equal(1, slotCount);
            Assert.Equal(expectedSlot, slotValue);
        }
    }

    /// <inheritdoc/>
    [Theory]
    [InlineData(OpenAIChatClientKind.ChatCompletions)]
    [InlineData(OpenAIChatClientKind.Responses)]
    public async Task DisabledPin_LeavesEveryRequestBodyWithoutAnyIdSlotField(OpenAIChatClientKind kind)
    {
        CancellationTokenSource sessionCancellation = new();
        var handler = new CancellingHandler(
            sessionCancellation,
            CreateResponse(kind, "first", "call-1"),
            CreateResponse(kind, "second", "call-2"));
        using var httpClient = new HttpClient(handler);
        // Default construction: no slot pin, byte-identical bodies to an unconfigured provider.
        using IChatClient client = OpenAIClientProvider.CreateChatClient(
            kind,
            CreateSettings(),
            CreateClientOptions(httpClient));

        await RunSessionAsync(client, sessionCancellation.Token);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            captured => Assert.DoesNotContain("id_slot", captured.Body, StringComparison.Ordinal));
    }

    /// <summary>
    /// The pin follows the subject character, not the provider resource (AI-002 TR-37): two clients sharing
    /// one provider's settings — two minds on one provider — each stamp their own character-derived value.
    /// </summary>
    [Fact]
    public async Task SharedProviderSettings_PinEachCharacterToItsOwnDerivedSlot()
    {
        OpenAIClientProvider.OpenAIClientProviderSettings settings = CreateSettings();
        (string FullId, int ExpectedSlot)[] subjects =
        [
            ("char:owner", OpenAIClientProvider.DeriveSlotId("char:owner")),
            ("char:newcomer", OpenAIClientProvider.DeriveSlotId("char:newcomer")),
        ];

        foreach ((string fullId, int expectedSlot) in subjects)
        {
            CancellationTokenSource sessionCancellation = new();
            var handler = new CancellingHandler(
                sessionCancellation,
                CreateToolCallResponse("chatcmpl-tool", "call-1"),
                CreateToolCallResponse("chatcmpl-second", "call-2"));
            using var httpClient = new HttpClient(handler);
            using IChatClient client = OpenAIClientProvider.CreateChatClient(
                OpenAIChatClientKind.ChatCompletions,
                settings,
                CreateClientOptions(httpClient),
                slotId: OpenAIClientProvider.ResolveSlotId(enableSlotPinning: true, subjectFullId: fullId));

            await RunSessionAsync(client, sessionCancellation.Token);

            Assert.Equal(3, handler.Requests.Count);
            Assert.All(
                handler.Requests,
                captured => Assert.Equal(expectedSlot, CountTopLevelIdSlot(captured.Body).SlotValue));
        }
    }

    private static IChatClient CreatePinnedClient(
        OpenAIChatClientKind kind,
        HttpClient httpClient,
        string subjectFullId)
        => OpenAIClientProvider.CreateChatClient(
            kind,
            CreateSettings(),
            CreateClientOptions(httpClient),
            slotId: OpenAIClientProvider.ResolveSlotId(enableSlotPinning: true, subjectFullId));

    private static (int SlotCount, int SlotValue) CountTopLevelIdSlot(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        int count = 0;
        int value = -1;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "id_slot", StringComparison.Ordinal))
            {
                count++;
                value = property.Value.GetInt32();
            }
        }

        return (count, value);
    }

    private static async Task RunSessionAsync(IChatClient client, CancellationToken sessionCancellation)
    {
        AgentSessionRunner runner = new(
            client,
            Instructions,
            [new ChatMessage(ChatRole.User, AgenticMind.SessionBootstrapInput)],
            [CreateSessionTool()],
            NullLogger.Instance);
        await runner.RunAsync(sessionCancellation);
    }

    private static AIFunction CreateSessionTool()
        => AIFunctionFactory.Create(
            () => "pong",
            new AIFunctionFactoryOptions
            {
                Name = ToolName,
                Description = "Answer the ping.",
                ExcludeResultSchema = true,
            });

    private static OpenAIClientProvider.OpenAIClientProviderSettings CreateSettings()
        => new("https://unit.test/v1", "not-a-live-key", "test-model", null);

    private static OpenAIClientOptions CreateClientOptions(HttpClient httpClient)
        => new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };

    /// <summary>
    /// Builds the scripted tool-call response in the given endpoint kind's documented shape, so the session's
    /// runner accepts it and replays its call result on the next request.
    /// </summary>
    private static string CreateResponse(OpenAIChatClientKind kind, string responseId, string callId)
        => kind == OpenAIChatClientKind.ChatCompletions
            ? CreateToolCallResponse($"chatcmpl-{responseId}", callId)
            : CreateFunctionCallResponse($"resp-{responseId}", callId);

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
    /// Captures every wire request — body, path, and content type — and ends the long-running session by
    /// cancelling its token once the scripted responses are exhausted.
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
            string body = await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri.AbsolutePath,
                body,
                request.Content.Headers.ContentType?.ToString()));

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

    private sealed record CapturedRequest(string Path, string Body, string? ContentType);
}
