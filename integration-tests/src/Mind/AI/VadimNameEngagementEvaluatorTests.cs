using System.Text.Json;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.TestFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Offline checks of the corrected evidence and judging contract: explicit chronology anchors, per-response
/// settlement pairing, blindness-preserving redaction, and fail-closed categorical parsing where only explicit
/// engagement passes and ambiguity never passes.
/// </summary>
[Headless]
public sealed class VadimNameEngagementEvaluatorTests
{
    private const string OpeningQuestion =
        "Ally. The recording is running. Please state your full name for the record.";

    private const string EngagedLine =
        "Ellie Onyx. The file lists you as Ally. State your employer for the record.";

    private const string PrivateReasoning = "She finally answered, but the file says Ally. I should press.";

    /// <summary>Malformed, unsupported, and incomplete categorical replies never produce a passing metric.</summary>
    [Fact]
    public void Parse_AcceptsOnlyTheSixDeclaredCategoriesWithNonEmptyReasons()
    {
        foreach (string category in new[]
        {
            VadimNameEngagementEvaluator.EngagementCategory,
            VadimNameEngagementEvaluator.GenericResponseCategory,
            VadimNameEngagementEvaluator.GenericNameRequestCategory,
            VadimNameEngagementEvaluator.AmbiguousNameRelatedCategory,
            VadimNameEngagementEvaluator.UnansweredCategory,
            VadimNameEngagementEvaluator.InsufficientEvidenceCategory,
        })
        {
            Assert.Equal(category, VadimNameEngagementEvaluator.Parse(
                $$"""{"category":"{{category}}","reason":"Recorded words."}""")?.Category);
        }

        foreach (string invalid in new[]
        {
            "not json",
            "[]",
            "{}",
            /*lang=json,strict*/
            "{\"category\":\"invalid\",\"reason\":\"Cannot tell.\"}",
            /*lang=json,strict*/
            "{\"category\":\"engagement-with-supplied-name\",\"reason\":\" \"}",
            /*lang=json,strict*/
            "{\"category\":\"unknown\",\"reason\":\"Words.\"}",
            /*lang=json,strict*/
            "{\"category\":\"grounded-name-engagement\",\"reason\":\"Retired v1 category.\"}",
            /*lang=json,strict*/
            "{\"category\":\"unanswered\",\"reason\":42}",
            /*lang=json,strict*/
            "{\"category\":\"unanswered\",\"reason\":\"Words.\",\"extra\":true}",
        })
        {
            Assert.Null(VadimNameEngagementEvaluator.Parse(invalid));
        }
    }

    /// <summary>
    /// A scripted judge's categorical reply maps fail-closed: engagement scores 5, every other declared category
    /// scores 1 without passing, and explicit insufficiency or malformed output yields no metric at all.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_MapsCategoriesFailClosed()
    {
        await AssertCategoryMapsToScoreAsync(
            VadimNameEngagementEvaluator.EngagementCategory,
            expectedScore: 5,
            expectMetric: true);
        foreach (string nonPassing in new[]
        {
            VadimNameEngagementEvaluator.GenericResponseCategory,
            VadimNameEngagementEvaluator.GenericNameRequestCategory,
            VadimNameEngagementEvaluator.AmbiguousNameRelatedCategory,
            VadimNameEngagementEvaluator.UnansweredCategory,
        })
        {
            await AssertCategoryMapsToScoreAsync(nonPassing, expectedScore: 1, expectMetric: true);
        }

        // Explicit insufficiency and malformed output are recorded invalid evaluations, not scores.
        await AssertCategoryMapsToScoreAsync(
            VadimNameEngagementEvaluator.InsufficientEvidenceCategory,
            expectedScore: null,
            expectMetric: false);
        await AssertJudgeReplyYieldsNoMetricAsync("not json at all");
    }

    /// <summary>
    /// The projected evidence anchors chronology explicitly and pairs every window response with its own calls,
    /// accepted/discarded state, and speech settlement, while omitting target instructions, private reasoning,
    /// and model identity.
    /// </summary>
    [Fact]
    public void BuildEvidence_AnchorsChronologyAndPairsResponseSettlementsWithoutPrivateContent()
    {
        string root = CreateScratchRoot("vadim-evidence-projection");
        VadimInterrogationResult result = BuildCompletedWindowResult(root);

        string evidence = VadimNameEngagementEvaluator.BuildEvidence(result);

        using var document = JsonDocument.Parse(evidence);
        JsonElement chronology = document.RootElement.GetProperty("chronology");
        JsonElement opening = chronology.GetProperty("openingQuestion");
        Assert.Equal(0, opening.GetProperty("requestIndex").GetInt32());
        Assert.Equal(OpeningQuestion, opening.GetProperty("acceptedSpeech").GetString());
        Assert.True(opening.GetProperty("settled").GetBoolean());
        Assert.True(opening.GetProperty("settledGameTimeSeconds").GetDouble() < 43d);
        JsonElement delivered = chronology.GetProperty("deliveredLine");
        Assert.Equal("My name is Ellie Onyx", delivered.GetProperty("text").GetString());
        Assert.Equal(43.4d, delivered.GetProperty("deliveredGameTimeSeconds").GetDouble());
        Assert.True(delivered.GetProperty("deliveryPrecedesWindowResponse1").GetBoolean());
        Assert.Equal(1, delivered.GetProperty("deliveredInRequestIndex").GetInt32());

        JsonElement responses = document.RootElement.GetProperty("windowResponses");
        Assert.Equal(3, responses.GetArrayLength());
        Assert.Equal("discarded", responses[0].GetProperty("state").GetString());
        Assert.Equal("accepted", responses[1].GetProperty("state").GetString());
        Assert.Equal("accepted", responses[2].GetProperty("state").GetString());
        Assert.Equal(1, responses[0].GetProperty("requestIndex").GetInt32());
        Assert.Contains("speak", responses[1].GetProperty("actions")[0].GetProperty("name").GetString());
        Assert.Contains("Spoken through the configured voice.", responses[1].GetProperty("settlement").GetString());
        Assert.Contains("Invalid Response", responses[0].GetProperty("settlement").GetString());

        // Blinding: no target instructions, private reasoning, or model identity reaches the judge.
        Assert.DoesNotContain("## Instructions", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("## Response", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateReasoning, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-model", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("threshold", evidence, StringComparison.Ordinal);
    }

    /// <summary>An incomplete or precondition-missing trial has no judgeable window and fails closed.</summary>
    [Fact]
    public void BuildEvidence_WithoutACompletedWindow_FailsClosed()
    {
        VadimInterrogationResult preconditionMissing = new(
            [], -1, [], PreconditionSatisfied: false, VadimOpeningKind.NonQuestion, "Sit down.", 1d);
        _ = Assert.Throws<InvalidOperationException>(
            () => VadimNameEngagementEvaluator.BuildEvidence(preconditionMissing));
    }

    /// <summary>Judge settlement evidence omits model identity, target-facing instructions and private reasoning.</summary>
    [Fact]
    public void BuildSettlement_ExposesOnlyOutcomeAndExecutedTools()
    {
        const string text = """
            # Turn
            - **Model:** hidden-model
            - **Outcome:** Accepted
            ## Instructions
            Hidden target criteria
            ## Response
            Hidden private thought
            ## Tool Invocations
            ### speak
            Spoken through the configured voice.
            """;
        string evidence = VadimNameEngagementEvaluator.BuildSettlement(text);
        Assert.StartsWith("- **Outcome:** Accepted", evidence, StringComparison.Ordinal);
        Assert.Contains("Spoken through the configured voice.", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hidden", evidence, StringComparison.Ordinal);
    }

    private static async Task AssertCategoryMapsToScoreAsync(
        string category,
        double? expectedScore,
        bool expectMetric)
    {
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            $$"""{"category":"{{category}}","reason":"The recorded speech decides it."}""")));
        VadimNameEngagementEvaluator evaluator = new("evidence", CreateScratchRoot("vadim-judge-mapping"));

        EvaluationResult result = await evaluator.EvaluateAsync(
            [],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "target reply")),
            new ChatConfiguration(judge));

        Assert.Equal(category, evaluator.Category);
        if (expectMetric)
        {
            Assert.True(result.Metrics.TryGetValue(VadimNameEngagementEvaluator.MetricName, out EvaluationMetric? metric));
            NumericMetric numeric = Assert.IsType<NumericMetric>(metric);
            Assert.Equal(expectedScore, numeric.Value);
        }
        else
        {
            Assert.Empty(result.Metrics);
        }
    }

    private static async Task AssertJudgeReplyYieldsNoMetricAsync(string judgeReply)
    {
        LiveLLMScriptedChatClient judge = new("judge");
        _ = judge.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, judgeReply)));
        VadimNameEngagementEvaluator evaluator = new("evidence", CreateScratchRoot("vadim-judge-malformed"));

        EvaluationResult result = await evaluator.EvaluateAsync(
            [],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "target reply")),
            new ChatConfiguration(judge));

        Assert.Null(evaluator.Category);
        Assert.Empty(result.Metrics);
    }

    /// <summary>
    /// Builds a completed-window result with the corrected chronology: an opening full-name question, a delivery
    /// request whose synthetic exchange carries the line at 43.4s game time, and three settled window responses
    /// whose transcript pages record the discarded, tolerated-then-accepted, and closing-wait outcomes.
    /// </summary>
    private static VadimInterrogationResult BuildCompletedWindowResult(string root)
    {
        string transcripts = Path.Combine(root, "transcripts");
        string character = Path.Combine(transcripts, "2026-10-26-00-00-00", "vadim");
        _ = Directory.CreateDirectory(character);
        File.WriteAllText(
            Path.Combine(character, "turn-0001.md"),
            $"""
            # Mind Session Transcript — Turn 0001

            - **Character:** vadim
            - **Model:** hidden-model
            - **Outcome:** Accepted

            ## Instructions

            Hidden target criteria

            ## Response

            {PrivateReasoning}

            ## Tool Invocations

            ### speak

            Spoken words: {OpeningQuestion}

            **Result**

            Spoken through the configured voice.
            """);
        File.WriteAllText(
            Path.Combine(character, "turn-0002.md"),
            """
            # Mind Session Transcript — Turn 0002

            - **Outcome:** Discarded Before Response
            """);
        File.WriteAllText(
            Path.Combine(character, "turn-0003.md"),
            """
            # Mind Session Transcript — Turn 0003

            - **Outcome:** Invalid Response — Recovery Scheduled

            ## Response

            I will wait for her answer.
            """);
        File.WriteAllText(
            Path.Combine(character, "turn-0004.md"),
            $"""
            # Mind Session Transcript — Turn 0004

            - **Model:** hidden-model
            - **Outcome:** Accepted

            ## Instructions

            Hidden target criteria

            ## Response

            {PrivateReasoning}

            ## Tool Invocations

            ### speak

            Spoken words: {EngagedLine}

            **Result**

            Spoken through the configured voice.
            """);
        File.WriteAllText(
            Path.Combine(character, "turn-0005.md"),
            """
            # Mind Session Transcript — Turn 0005

            - **Outcome:** Accepted

            ## Tool Invocations

            ### wait

            **Result**

            Wait ended: timeout. Elapsed game time: 0.1 seconds. Current game time: 44.0s.
            """);

        IReadOnlyList<string> turnFiles =
        [
            .. Directory.GetFiles(transcripts, "turn-*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal),
        ];

        VadimInterrogationRequest opening = new(
            0,
            [],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [SpeakCall("opening-speak", OpeningQuestion)])),
            SettledTurnsAtRequestStart: 0);
        VadimInterrogationRequest delivery = new(
            1,
            DeliveryMessages(),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will wait for her answer.")),
            SettledTurnsAtRequestStart: 2);
        VadimInterrogationRequest recovery = new(
            2,
            [],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [SpeakCall("recovery-speak", EngagedLine)])),
            SettledTurnsAtRequestStart: 3);
        VadimInterrogationRequest closing = new(
            3,
            [],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [WaitCall("closing-wait")])),
            SettledTurnsAtRequestStart: 4);

        return new VadimInterrogationResult(
            [opening, delivery, recovery, closing],
            DeliveryRequestIndex: 1,
            turnFiles,
            PreconditionSatisfied: true,
            VadimOpeningKind.FullNameQuestion,
            OpeningQuestion,
            OpeningSettledGameTimeSeconds: 2.5d);
    }

    private static IReadOnlyList<ChatMessage> DeliveryMessages()
    {
        FunctionCallContent sceneUpdate = new(
            "runtime-scene-update-3",
            SyntheticSceneUpdateExchange.DeliveryToolName);
        return
        [
            new ChatMessage(ChatRole.User, "Begin. Participate in the scene using the available tools."),
            new ChatMessage(ChatRole.Assistant, [sceneUpdate]),
            new ChatMessage(
                ChatRole.Tool,
                [
                    new FunctionResultContent(
                        sceneUpdate.CallId,
                        VadimInterrogationBaseline.PlayerLineDeliveryPrefix + " (at 43.4s game time)"),
                ]),
            new ChatMessage(ChatRole.User, "#### Attended Characters\n\nCurrent game time: 43.6s"),
        ];
    }

    private static FunctionCallContent SpeakCall(string callId, string speech)
        => new(callId, "speak", new Dictionary<string, object?> { ["speech"] = speech });

    private static FunctionCallContent WaitCall(string callId)
        => new(callId, "wait", new Dictionary<string, object?> { ["seconds"] = 0.05f });

    /// <summary>Creates a task-specific scratch root under the git-ignored <c>game/temp</c> area.</summary>
    private static string CreateScratchRoot(string name)
    {
        DirectoryInfo liveTraces = LiveLLMTraceWriter.ResolveDefaultRoot();
        DirectoryInfo scratchRoot = new(Path.Combine(
            liveTraces.Parent!.FullName, name, Guid.NewGuid().ToString("N")));
        scratchRoot.Create();
        return scratchRoot.FullName;
    }
}
