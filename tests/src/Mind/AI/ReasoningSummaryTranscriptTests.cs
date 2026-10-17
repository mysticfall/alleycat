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
/// Deterministic end-to-end regressions for the reasoning request controls and parsed reasoning-content
/// surfacing — both summary text and captured-shape provider-exposed <c>reasoning_text</c> content — where a
/// complete settled agent session cycle runs offline against a captured transport through the production
/// provider construction path, with the real AI-011 recorder writing turns (AI-002 TR-28–TR-31).
/// </summary>
[Collection(MindSessionTranscriptSeamCollection.Name)]
public sealed class ReasoningSummaryTranscriptTests : IDisposable
{
    private const string Instructions = "Test the tool-only protocol.";
    private const string CallID = "call_summary_1";

    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "AlleyCat.ReasoningSummaryTranscriptTests",
        Guid.NewGuid().ToString("N"));

    /// <summary>Resets the process-wide turn counters and redirects the recorder root to a fresh temporary tree.</summary>
    public ReasoningSummaryTranscriptTests()
    {
        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(_rootDirectory);
    }

    /// <summary>Restores the recorder seams and deletes the temporary transcript tree.</summary>
    public void Dispose()
    {
        MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
        MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written; the root never existed.
        }
    }

    /// <summary>
    /// The representative configured cycle: every request — including the transcript-replay continuation —
    /// carries exactly <c>reasoning.effort=medium</c> and <c>reasoning.summary=detailed</c> with the stateless
    /// payload preserved and neither answer-text verbosity field, and the supplied parsed summary is distinctly
    /// visible in the settled AI-011 turn.
    /// </summary>
    [Fact]
    public async Task RepresentativeCycle_WithEffortAndSummaryControls_RequestExactAndSummaryVisible()
    {
        string summaryText = "The bootstrap asks for a first action; greet the player briefly.";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: ReasoningEffort.Medium,
            reasoningSummary: "detailed",
            summaryText: summaryText,
            reasoningTokens: 2344);

        Assert.Equal(2, capture.Requests.Count);
        foreach (string body in capture.Requests)
        {
            using var request = JsonDocument.Parse(body);
            JsonElement root = request.RootElement;
            Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("detailed", root.GetProperty("reasoning").GetProperty("summary").GetString());
            Assert.False(root.TryGetProperty("text", out _));
            Assert.False(root.TryGetProperty("verbosity", out _));
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("previous_response_id", out _));
            Assert.Equal(Instructions, root.GetProperty("instructions").GetString());
            Assert.Equal("required", root.GetProperty("tool_choice").GetString());
            Assert.Equal(
                ["speak"],
                root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        }

        // The SDK-parsed reasoning summary is non-empty and surfaces distinctly in the settled turn.
        Assert.Contains(capture.Response.Messages, message => message.Contents
            .OfType<TextReasoningContent>()
            .Any(reasoning => reasoning.Text?.Contains(summaryText, StringComparison.Ordinal) == true));
        Assert.Contains("> **Reasoning**", capture.TurnMarkdown, StringComparison.Ordinal);
        Assert.Contains(summaryText, capture.TurnMarkdown, StringComparison.Ordinal);
        Assert.Contains("**Function Call:** speak", capture.TurnMarkdown, StringComparison.Ordinal);
        AssertIndexLinksTurn(capture.CharacterDirectory, "probe_char");
    }

    /// <summary>
    /// A supplied non-empty summary is distinctly visible in a settled turn even without configured controls.
    /// </summary>
    [Fact]
    public async Task SettledCycle_SuppliedSummaryText_RendersDistinctlyWithoutControls()
    {
        string summaryText = "Checked the scene status and chose a short greeting.";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: summaryText,
            reasoningTokens: 2344);

        Assert.Contains(capture.Response.Messages, message => message.Contents
            .OfType<TextReasoningContent>()
            .Any(reasoning => reasoning.Text?.Contains(summaryText, StringComparison.Ordinal) == true));
        Assert.Contains("> **Reasoning**", capture.TurnMarkdown, StringComparison.Ordinal);
        Assert.Contains(summaryText, capture.TurnMarkdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reasoning item whose summary list is empty — with positive reasoning-token usage — produces an empty
    /// parsed summary that renders as the empty marker variant only: no reasoning text is ever fabricated from
    /// token counts or other metadata.
    /// </summary>
    [Fact]
    public async Task SettledCycle_EmptySummaryWithPositiveReasoningUsage_NeverFabricates()
    {
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 2344);

        Assert.All(
            capture.Response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>(),
            reasoning => Assert.True(string.IsNullOrWhiteSpace(reasoning.Text),
                "no reasoning text may be produced from token counts alone"));
        int markerIndex = capture.TurnMarkdown.IndexOf("> **Reasoning**", StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "the empty reasoning marker must still be distinctly marked");
        string tail = capture.TurnMarkdown[(markerIndex + "> **Reasoning**".Length)..];
        int blankBoundary = tail.IndexOf("\n\n", StringComparison.Ordinal);
        string block = blankBoundary < 0 ? tail : tail[..blankBoundary];
        Assert.All(
            block.Split('\n'),
            line => Assert.True(
                line.Length == 0 || line.Trim() == ">" || string.IsNullOrWhiteSpace(line.Trim()[1..]),
                $"reasoning block must stay empty; observed line: '{line}'"));
    }

    /// <summary>
    /// A response with no reasoning item at all — but positive reasoning-token usage — produces no reasoning
    /// content and no fabricated reasoning text in the turn.
    /// </summary>
    [Fact]
    public async Task SettledCycle_NoReasoningItemWithPositiveReasoningUsage_NeverFabricates()
    {
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 2344,
            includeReasoningItem: false);

        Assert.DoesNotContain(
            capture.Response.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent);
        Assert.DoesNotContain("> **Reasoning**", capture.TurnMarkdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Captured-shape regression for the confirmed provider-exposed Responses reasoning-content loss
    /// (live probe 2, Arm A): the provider returned its reasoning as a <c>content</c> part of type
    /// <c>reasoning_text</c> with an empty <c>summary</c>, and the installed non-streaming adapter maps
    /// summary text only. The exposed reasoning text must appear distinctly, in order, and exactly once in
    /// the settled AI-011 turn, while the configured request controls stay exact and the stateless payload
    /// and tool inventory remain preserved.
    /// </summary>
    /// <remarks>
    /// Sanitised from the live capture: the reasoning text is a synthetic marker in place of the provider's
    /// 153-character reasoning (never copied into the repository), and ids, model, and token totals are
    /// fixture values. The captured response structure is preserved exactly: one reasoning item carrying a
    /// single <c>reasoning_text</c> content part with an empty summary list, including
    /// <c>status:"completed"</c>, <c>format:"unknown"</c>, and 74 reasoning tokens, followed by one
    /// <c>speak</c> function call.
    /// </remarks>
    [Fact]
    public async Task SettledCycle_CapturedContentReasoningText_AppearsDistinctlyInOrderWithoutDuplication()
    {
        const string reasoningText =
            "Sanitised captured-shape reasoning marker: the provider exposed its reasoning text in a "
            + "content part of type reasoning_text while the summary list stayed empty.";

        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: ReasoningEffort.Medium,
            reasoningSummary: "detailed",
            summaryText: null,
            reasoningTokens: 74,
            responseBody: CreateCapturedContentReasoningResponse(reasoningText));

        // Request invariants come first: a failure here is a setup or configuration defect, never the
        // response-mapping symptom this regression pins.
        Assert.Equal(2, capture.Requests.Count);
        foreach (string body in capture.Requests)
        {
            using var request = JsonDocument.Parse(body);
            JsonElement root = request.RootElement;
            Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("detailed", root.GetProperty("reasoning").GetProperty("summary").GetString());
            Assert.False(root.TryGetProperty("text", out _));
            Assert.False(root.TryGetProperty("verbosity", out _));
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("previous_response_id", out _));
            Assert.Equal(Instructions, root.GetProperty("instructions").GetString());
            Assert.Equal("required", root.GetProperty("tool_choice").GetString());
            Assert.Equal(
                ["speak"],
                root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        }

        // The complete observation window: the entire request cycle has settled and the AI-011 turn is
        // recorded. The captured-shape reasoning text must survive the provider→SDK→runner→recorder path.
        int markerIndex = capture.TurnMarkdown.IndexOf("> **Reasoning**", StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "the reasoning block must be distinctly marked in the settled turn");
        Assert.True(
            capture.TurnMarkdown.Contains(reasoningText, StringComparison.Ordinal),
            "provider-exposed reasoning text (reasoning content part) is missing from the settled AI-011 turn");
        int textIndex = capture.TurnMarkdown.IndexOf(reasoningText, StringComparison.Ordinal);
        int callIndex = capture.TurnMarkdown.IndexOf("**Function Call:** speak", StringComparison.Ordinal);
        Assert.True(
            callIndex < 0 || (markerIndex < callIndex && textIndex < callIndex),
            "reasoning content must precede the function call in the response section");
        Assert.Equal(1, CountOccurrences(capture.TurnMarkdown, reasoningText));
        AssertIndexLinksTurn(capture.CharacterDirectory, "probe_char");
    }

    /// <summary>
    /// Mixed sources assemble without loss: already-surfaced summary text keeps its place first and the
    /// recognised content part follows, explicitly separated, in order, and exactly once — regardless of
    /// configured request controls (AI-002 TR-33).
    /// </summary>
    [Fact]
    public async Task SettledCycle_MixedSummaryAndContent_SummaryKeepsPlaceBeforeContentParts()
    {
        const string summaryText = "Summary keeps its place before appended content parts.";
        const string contentText = "Recognised content part follows the summary in order.";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 74,
            responseBody: CreateReasoningItemsAndCallResponse($$"""
                {
                  "type": "reasoning",
                  "id": "rs_mixed_1",
                  "status": "completed",
                  "content": [ { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(contentText)}} } ],
                  "summary": [ { "type": "summary_text", "text": {{JsonSerializer.Serialize(summaryText)}} } ]
                }
                """));

        TextReasoningContent reasoning = Assert.Single(
            capture.Response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
        Assert.Equal(summaryText + "\n" + contentText, reasoning.Text);

        Assert.Contains("> **Reasoning**", capture.TurnMarkdown, StringComparison.Ordinal);
        int summaryIndex = capture.TurnMarkdown.IndexOf(summaryText, StringComparison.Ordinal);
        int contentIndex = capture.TurnMarkdown.IndexOf(contentText, StringComparison.Ordinal);
        int callIndex = capture.TurnMarkdown.IndexOf("**Function Call:** speak", StringComparison.Ordinal);
        Assert.True(
            summaryIndex >= 0 && contentIndex > summaryIndex && (callIndex < 0 || contentIndex < callIndex),
            "summary text must precede the content part, which precedes the function call");
        Assert.Equal(1, CountOccurrences(capture.TurnMarkdown, summaryText));
        Assert.Equal(1, CountOccurrences(capture.TurnMarkdown, contentText));
    }

    /// <summary>
    /// Whole-text identity: when the complete recognised content text is exactly the already-surfaced
    /// summary text, no second copy is added (AI-002 TR-33).
    /// </summary>
    [Fact]
    public async Task SettledCycle_ContentWholeTextIdenticalToSummary_NotDuplicated()
    {
        const string identicalText = "Identical summary and content text marker.";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 74,
            responseBody: CreateReasoningItemsAndCallResponse($$"""
                {
                  "type": "reasoning",
                  "id": "rs_identical_1",
                  "status": "completed",
                  "content": [ { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(identicalText)}} } ],
                  "summary": [ { "type": "summary_text", "text": {{JsonSerializer.Serialize(identicalText)}} } ]
                }
                """));

        TextReasoningContent reasoning = Assert.Single(
            capture.Response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
        Assert.Equal(identicalText, reasoning.Text);
        Assert.Equal(1, CountOccurrences(capture.TurnMarkdown, identicalText));
    }

    /// <summary>
    /// Intentionally repeated parts, substring overlap with the summary, and multi-part content are never
    /// deduplicated, paraphrased, or reordered: every part keeps its own occurrence in order (AI-002
    /// TR-33).
    /// </summary>
    [Fact]
    public async Task SettledCycle_RepeatedSubstringAndMultiPartContent_NeverDeduplicated()
    {
        const string summaryText = "Substring summary mentions the repeated reasoning marker tail end.";
        const string repeatedPart = "Repeated reasoning marker.";
        const string substringPart = "the repeated reasoning marker tail end";
        const string distinctPart = "Distinct final content part.";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 74,
            responseBody: CreateReasoningItemsAndCallResponse($$"""
                {
                  "type": "reasoning",
                  "id": "rs_repeat_1",
                  "status": "completed",
                  "content": [
                    { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(repeatedPart)}} },
                    { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(repeatedPart)}} },
                    { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(substringPart)}} },
                    { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(distinctPart)}} }
                  ],
                  "summary": [ { "type": "summary_text", "text": {{JsonSerializer.Serialize(summaryText)}} } ]
                }
                """));

        TextReasoningContent reasoning = Assert.Single(
            capture.Response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
        Assert.Equal(
            summaryText + "\n" + string.Join("\n", repeatedPart, repeatedPart, substringPart, distinctPart),
            reasoning.Text);

        // Both intentional repeats stay, the substring overlap keeps both its summary and content
        // occurrences, and the distinct part stays last.
        Assert.Equal(2, CountOccurrences(capture.TurnMarkdown, repeatedPart));
        Assert.Equal(2, CountOccurrences(capture.TurnMarkdown, substringPart));
        Assert.Equal(1, CountOccurrences(capture.TurnMarkdown, distinctPart));
        Assert.True(
            capture.TurnMarkdown.IndexOf(distinctPart, StringComparison.Ordinal)
                > capture.TurnMarkdown.IndexOf(substringPart, StringComparison.Ordinal),
            "parts must keep their original order");
    }

    /// <summary>
    /// Multiple reasoning items keep their response order, each surfacing its own recognised content
    /// (AI-002 TR-33).
    /// </summary>
    [Fact]
    public async Task SettledCycle_MultipleReasoningItems_ContentKeepsResponseOrder()
    {
        const string firstItemText = "First reasoning item content marker.";
        const string secondItemText = "Second reasoning item content marker.";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 74,
            responseBody: CreateReasoningItemsAndCallResponse(
                $$"""
                {
                  "type": "reasoning",
                  "id": "rs_multi_1",
                  "status": "completed",
                  "content": [ { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(firstItemText)}} } ],
                  "summary": []
                }
                """,
                $$"""
                {
                  "type": "reasoning",
                  "id": "rs_multi_2",
                  "status": "completed",
                  "content": [ { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(secondItemText)}} } ],
                  "summary": []
                }
                """));

        TextReasoningContent[] reasoningItems =
        [
            .. capture.Response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>(),
        ];
        Assert.Equal(2, reasoningItems.Length);
        Assert.Equal(firstItemText, reasoningItems[0].Text);
        Assert.Equal(secondItemText, reasoningItems[1].Text);

        Assert.Equal(2, CountOccurrences(capture.TurnMarkdown, "> **Reasoning**"));
        Assert.True(
            capture.TurnMarkdown.IndexOf(firstItemText, StringComparison.Ordinal) >= 0
                && capture.TurnMarkdown.IndexOf(firstItemText, StringComparison.Ordinal)
                    < capture.TurnMarkdown.IndexOf(secondItemText, StringComparison.Ordinal),
            "reasoning items must keep their response order");
    }

    /// <summary>
    /// Protected data and item identity survive normalisation in place, and encrypted content never
    /// surfaces as reasoning text (AI-002 TR-31–TR-33).
    /// </summary>
    [Fact]
    public async Task SettledCycle_ProtectedDataAndItemIdentity_PreservedAndNeverRenderedAsReasoningText()
    {
        const string contentText = "Content part alongside protected data.";
        const string encryptedContent = "enc-protected-7f3a-marker";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 74,
            responseBody: CreateReasoningItemsAndCallResponse($$"""
                {
                  "type": "reasoning",
                  "id": "rs_protected_1",
                  "status": "completed",
                  "content": [ { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(contentText)}} } ],
                  "summary": [],
                  "encrypted_content": {{JsonSerializer.Serialize(encryptedContent)}}
                }
                """));

        TextReasoningContent reasoning = Assert.Single(
            capture.Response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
        Assert.Equal(contentText, reasoning.Text);
        Assert.Equal(encryptedContent, reasoning.ProtectedData);
        Assert.Equal("rs_protected_1", reasoning.AdditionalProperties?["reasoningItemId"]);

        Assert.Contains(contentText, capture.TurnMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(encryptedContent, capture.TurnMarkdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unrecognised content-part types and parts without a string text are contained: only recognised
    /// <c>reasoning_text</c> parts surface, and the summary and the settled speak call survive unchanged
    /// (AI-002 TR-32).
    /// </summary>
    [Fact]
    public async Task SettledCycle_UnknownAndMalformedContentParts_ContainedToRecognisedText()
    {
        const string summaryText = "Summary survives unrecognised content siblings.";
        const string recognisedText = "Recognised content sibling.";
        const string unknownTypeText = "Unrecognised type sibling.";
        SettledCycleCapture capture = await RunSettledCycleAsync(
            reasoningEffort: null,
            reasoningSummary: null,
            summaryText: null,
            reasoningTokens: 74,
            responseBody: CreateReasoningItemsAndCallResponse($$"""
                {
                  "type": "reasoning",
                  "id": "rs_malformed_1",
                  "status": "completed",
                  "content": [
                    { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(recognisedText)}} },
                    { "type": "other_kind", "text": {{JsonSerializer.Serialize(unknownTypeText)}} },
                    { "type": "reasoning_text", "text": 7 },
                    { "type": "reasoning_text" }
                  ],
                  "summary": [ { "type": "summary_text", "text": {{JsonSerializer.Serialize(summaryText)}} } ]
                }
                """));

        TextReasoningContent reasoning = Assert.Single(
            capture.Response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
        Assert.Equal(summaryText + "\n" + recognisedText, reasoning.Text);

        Assert.Contains(summaryText, capture.TurnMarkdown, StringComparison.Ordinal);
        Assert.Contains(recognisedText, capture.TurnMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(unknownTypeText, capture.TurnMarkdown, StringComparison.Ordinal);
        Assert.Contains("**Function Call:** speak", capture.TurnMarkdown, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void AssertIndexLinksTurn(string characterDirectory, string characterId)
    {
        string indexPath = Path.Combine(characterDirectory, "index.md");
        Assert.True(File.Exists(indexPath), "the character invocation index must exist for a recorded cycle");
        string index = File.ReadAllText(indexPath);
        Assert.Contains(characterId, index, StringComparison.Ordinal);
        Assert.Contains("(turn-0001.md)", index, StringComparison.Ordinal);
    }

    private sealed record SettledCycleCapture(
        IReadOnlyList<string> Requests,
        ChatResponse Response,
        string TurnMarkdown,
        string CharacterDirectory);

    private async Task<SettledCycleCapture> RunSettledCycleAsync(
        ReasoningEffort? reasoningEffort,
        string? reasoningSummary,
        string? summaryText,
        int reasoningTokens,
        bool includeReasoningItem = true,
        string? responseBody = null)
    {
        MindSessionTranscriptRecorder recorder = new("probe_char", () => _rootDirectory, NullLogger.Instance);
        var sessionCancellation = new CancellationTokenSource();
        var handler = new CancellingHandler(
            sessionCancellation,
            responseBody ?? CreateSummaryAndCallResponse(summaryText, reasoningTokens, includeReasoningItem));
        using var httpClient = new HttpClient(handler);
        var settings = new OpenAIClientProvider.OpenAIClientProviderSettings(
            "https://unit.test/v1",
            "not-a-live-key",
            "test-model",
            null,
            reasoningEffort,
            reasoningSummary);
        using IChatClient providerClient = OpenAIClientProvider.CreateChatClient(
            OpenAIClientProvider.DefaultChatClientKind,
            settings,
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://unit.test/v1"),
                Transport = new HttpClientPipelineTransport(httpClient),
            });
        ResponseCapturingChatClient capturing = new(providerClient);

        AgentSessionRunner runner = new(
            capturing,
            Instructions,
            [new ChatMessage(ChatRole.User, AgenticMind.SessionBootstrapInput)],
            [AIFunctionFactory.Create((string speech) => "Spoken.", "speak")],
            allowMultipleToolCalls: false,
            NullLogger.Instance,
            transcriptSink: recorder);
        await runner.RunAsync(sessionCancellation.Token);

        string characterDirectory = Assert.Single(
            Directory.GetDirectories(Assert.Single(Directory.GetDirectories(_rootDirectory))));
        string turnPath = Assert.Single(Directory.GetFiles(characterDirectory, "turn-0001.md"));
        return new SettledCycleCapture(
            [.. handler.Requests.Select(request => request.Body)],
            CaptureResponse(capturing),
            await File.ReadAllTextAsync(turnPath),
            characterDirectory);
    }

    private static ChatResponse CaptureResponse(ResponseCapturingChatClient capturing)
    {
        Assert.NotNull(capturing.Response);
        return capturing.Response;
    }

    private static string CreateSummaryAndCallResponse(
        string? summaryText,
        int reasoningTokens,
        bool includeReasoningItem)
    {
        string reasoningItem = !includeReasoningItem ? string.Empty
            : summaryText is null
            ? ",\n                { \"type\": \"reasoning\", \"id\": \"rs_summary_1\", \"summary\": [] }"
            : ",\n                { \"type\": \"reasoning\", \"id\": \"rs_summary_1\", \"summary\": [ { \"type\": \"summary_text\", \"text\": "
            + JsonSerializer.Serialize(summaryText) + " } ] }";
        return $$"""
            {
              "id": "resp_summary_1",
              "object": "response",
              "created_at": 1,
              "status": "completed",
              "error": null,
              "incomplete_details": null,
              "instructions": null,
              "model": "test-model",
              "output": [{
                "type": "function_call",
                "id": "fc_summary_1",
                "call_id": "{{CallID}}",
                "name": "speak",
                "arguments": {{JsonSerializer.Serialize(JsonSerializer.Serialize(new Dictionary<string, string> { ["speech"] = "Hello there!" }))}},
                "status": "completed"
              }{{reasoningItem}}],
              "parallel_tool_calls": true,
              "previous_response_id": null,
              "store": false,
              "tool_choice": "auto",
              "tools": [],
              "usage": {
                "input_tokens": 120,
                "input_tokens_details": { "cached_tokens": 0 },
                "output_tokens": 2400,
                "output_tokens_details": { "reasoning_tokens": {{reasoningTokens}} },
                "total_tokens": 2520
              }
            }
            """;
    }

    /// <summary>
    /// Builds the sanitised live-capture shape (live probe 2, Arm A): one reasoning item whose exposed
    /// <c>content</c> carries a single <c>reasoning_text</c> part while <c>summary</c> is empty, followed by
    /// one <c>speak</c> function call. Differences from the live capture: synthetic reasoning text, fixture
    /// ids and model, and captured usage totals apart from the 74 reasoning tokens.
    /// </summary>
    private static string CreateCapturedContentReasoningResponse(string reasoningText)
        => CreateReasoningItemsAndCallResponse($$"""
            {
              "type": "reasoning",
              "id": "rs_content_1",
              "status": "completed",
              "content": [ { "type": "reasoning_text", "text": {{JsonSerializer.Serialize(reasoningText)}} } ],
              "summary": [],
              "format": "unknown"
            }
            """);

    /// <summary>
    /// Builds a completed response carrying the supplied raw reasoning items before one <c>speak</c>
    /// function call, with positive reasoning-token usage (AI-002 TR-33 assembly matrix).
    /// </summary>
    private static string CreateReasoningItemsAndCallResponse(params string[] reasoningItems)
        => $$"""
            {
              "id": "resp_content_1",
              "object": "response",
              "created_at": 1,
              "status": "completed",
              "error": null,
              "incomplete_details": null,
              "instructions": null,
              "model": "test-model",
              "output": [
                {{string.Join(",\n                ", reasoningItems)}},
                {
                  "type": "function_call",
                  "id": "fc_content_1",
                  "call_id": "{{CallID}}",
                  "name": "speak",
                  "arguments": {{JsonSerializer.Serialize(JsonSerializer.Serialize(new Dictionary<string, string> { ["speech"] = "Hello there!" }))}},
                  "status": "completed"
                }
              ],
              "parallel_tool_calls": true,
              "previous_response_id": null,
              "store": false,
              "tool_choice": "auto",
              "tools": [],
              "usage": {
                "input_tokens": 305,
                "input_tokens_details": { "cached_tokens": 128 },
                "output_tokens": 87,
                "output_tokens_details": { "reasoning_tokens": 74 },
                "total_tokens": 392
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

    /// <summary>
    /// Decorates the production provider client to observe the SDK-parsed <see cref="ChatResponse"/> the runner
    /// and AI-011 recorder hold, independent of the transcript rendering.
    /// </summary>
    private sealed class ResponseCapturingChatClient(IChatClient inner) : IChatClient
    {
        public ChatResponse? Response
        {
            get; private set;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ChatResponse response = await inner.GetResponseAsync(messages, options, cancellationToken);
            Response = response;
            return response;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ResponseCapturingChatClient) ? this : inner.GetService(serviceType, serviceKey);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (ChatResponseUpdate update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }

        void IDisposable.Dispose()
        {
            inner.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
