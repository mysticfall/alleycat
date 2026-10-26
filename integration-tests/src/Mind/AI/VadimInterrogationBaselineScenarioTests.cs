using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Deterministic fake-client smoke for the Vadim interrogation baseline scenario: it proves the scenario drives
/// the real new-pipeline plumbing end-to-end — player line delivered as a runtime-authored synthetic
/// <c>scene_update</c> exchange, observation window completing, per-cycle evidence captured — with zero network
/// access (AI-002 TR-2/3/12/17/18; the parent outcome contract's evidence requirements).
/// </summary>
/// <remarks>
/// <para>
/// The scripted turns mirror the baseline's shape: an opening <c>speak</c>, one invalid response whose recovery
/// proves discard capture, one valid response carrying text alongside its <c>speak</c> call — the tolerated and
/// stripped shape that would have discarded the baseline's engaged turn 6 — and a closing <c>wait</c>. A final
/// hold-forever step stands in for whatever the model requests after the window closes; the scenario's node exit
/// cancels it mid-flight exactly as it will cancel the live run's post-window request.
/// </para>
/// <para>
/// Every provider roundtrip flows through the executor's traced target client — the identical plumbing the live
/// run uses — so trace capture, bounds, and artefact writing are exercised here without a live LLM.
/// </para>
/// </remarks>
[Headless]
public sealed class VadimInterrogationBaselineScenarioTests
{
    private const string OpeningLine =
        "Ally. The recording is running. Please state your full name for the record.";

    private const string EngagedLine =
        "Ellie Onyx. The file lists you as Ally. State your employer for the record.";

    private const string ToleratedText = "She finally answered. I should proceed with the next question.";

    /// <summary>
    /// The scenario delivers the player line as a runtime-authored synthetic scene-update exchange, closes its
    /// observation window two requests after delivery, and captures the trace, turn files, and per-cycle
    /// evidence the judged phase consumes.
    /// </summary>
    [Fact]
    public async Task Scenario_DeliversPlayerLineAsSyntheticExchange_AndCapturesTheObservationWindow()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string scenarioRoot = CreateScenarioRoot("vadim-interrogation-smoke");
        string transcriptRoot = Path.Combine(scenarioRoot, "transcripts");
        LiveLLMScriptedChatClient target = new("target");
        ScriptBaselineTurns(target);
        LiveLLMScenarioExecutor executor = new(target);
        VadimInterrogationScenario scenario = new(transcriptRoot, VadimInterrogationBaseline.SmokeBounds.Timeout);
        VadimInterrogationResult? result = null;

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            VadimInterrogationBaseline.ScenarioName,
            VadimInterrogationBaseline.SmokeBounds,
            async context => result = await scenario.RunAsync(context));

        // The scenario delegate completed: the window closed and the session ended by node exit, with the
        // post-window request cancelled quietly rather than failing the execution.
        Assert.True(execution.Succeeded, DescribeFailure(execution));
        Assert.NotNull(result);
        Assert.True(result.WindowCompleted);
        Assert.Equal(4, result.CompletedRequests.Count);
        Assert.Equal(1, result.DeliveryRequestIndex);

        // The delivery request replays the retained opening speak exchange, then makes the player line
        // model-visible through exactly one runtime-authored synthetic exchange before the fresh current-scene
        // message (AI-002 TR-2/17/18), with the opening self-speech never delivered as a scene update (TR-21).
        VadimInterrogationRequest delivery = result.CompletedRequests[result.DeliveryRequestIndex];
        Assert.Equal(
            [
                ChatRole.User,
                ChatRole.Assistant,
                ChatRole.Tool,
                ChatRole.Assistant,
                ChatRole.Tool,
                ChatRole.User,
            ],
            delivery.Messages.Select(static message => message.Role));
        Assert.Equal(AgenticMind.SessionBootstrapInput, delivery.Messages[0].Text);
        FunctionCallContent openingCall = Assert.IsType<FunctionCallContent>(
            Assert.Single(delivery.Messages[1].Contents));
        Assert.Equal("speak", openingCall.Name);
        FunctionCallContent sceneUpdate = Assert.IsType<FunctionCallContent>(
            Assert.Single(delivery.Messages[3].Contents));
        Assert.Equal(SyntheticSceneUpdateExchange.DeliveryToolName, sceneUpdate.Name);
        Assert.StartsWith(
            SyntheticSceneUpdateExchange.SyntheticCallIDPrefix,
            sceneUpdate.CallId,
            StringComparison.Ordinal);
        string delivered = Assert.IsType<FunctionResultContent>(
            Assert.Single(delivery.Messages[4].Contents)).Result?.ToString() ?? string.Empty;
        Assert.Contains(
            VadimInterrogationBaseline.PlayerLineDeliveryPrefix,
            delivered,
            StringComparison.Ordinal);
        Assert.DoesNotContain("I said:", delivered, StringComparison.Ordinal);
        Assert.Contains("Current game time:", delivery.Messages[^1].Text, StringComparison.Ordinal);

        // No retired Event History message or newly-presented marking exists anywhere in the window's requests
        // (AI-002 TR-3, AI-003 TR-10).
        foreach (VadimInterrogationRequest request in result.CompletedRequests)
        {
            string joined = string.Join('\n', request.Messages.Select(static message => message.Text));
            Assert.DoesNotContain("<Event History>", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("[new]", joined, StringComparison.Ordinal);
        }

        // The delivered exchange is retained once and never redelivered: every later request replays exactly one
        // scene_update call, and it is the same call (AI-002 TR-17/19).
        foreach (VadimInterrogationRequest request in result.CompletedRequests.Skip(
            result.DeliveryRequestIndex + 1))
        {
            FunctionCallContent retained = Assert.Single(
                request.Messages
                    .SelectMany(static message => message.Contents)
                    .OfType<FunctionCallContent>(),
                static call => call.Name == SyntheticSceneUpdateExchange.DeliveryToolName);
            Assert.Equal(sceneUpdate.CallId, retained.CallId);
        }

        // Retention: the opening speak exchange replays in the request that follows it (AI-002 TR-17), and the
        // tolerated text-plus-call response settles as a valid, stripped exchange (TR-12) — the exact shape the
        // old pipeline discarded at the baseline's turn 6.
        VadimInterrogationRequest recoveryRequest = result.CompletedRequests[2];
        Assert.Contains(
            recoveryRequest.Messages.SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>(),
            call => call.Name == "speak"
                && call.Arguments!.TryGetValue("speech", out object? speech)
                && speech?.ToString() == OpeningLine);

        // Trace capture: all six roundtrips — the four that settle, the request the injection invalidated, and
        // the post-window request cancelled at session end — are traced target exchanges, and the sanitised
        // artefact is written (verify delivered evidence first).
        LiveLLMTraceEntry[] exchanges = [.. execution.Trace.Entries
            .Where(static entry => entry.Kind == LiveLLMTraceEntryKind.Exchange)];
        Assert.Equal(6, exchanges.Length);
        Assert.All(exchanges, static entry => Assert.Equal(LiveLLMPurpose.Target, entry.Purpose));
        Assert.Equal(4, exchanges.Count(static entry => entry.Response is not null));
        Assert.Equal(2, exchanges.Count(static entry => entry.Failure is not null));
        FileInfo traceFile = new LiveLLMTraceWriter(new DirectoryInfo(scenarioRoot))
            .Write(execution.Trace, "smoke");
        Assert.True(traceFile.Exists);

        // Turn-level evidence: the AI-011 recorder wrote one page per resolved cycle — the opening speak, the
        // request the injection invalidated, the delivery request's invalid-response discard with its reason,
        // the stripped-text recovery, and the settled closing wait (AI-011 TR-6/TR-11). A post-window cycle
        // interrupted by session end may follow.
        Assert.InRange(result.TurnFiles.Count, 5, 6);
        Assert.Equal(
            ["turn-0001.md", "turn-0002.md", "turn-0003.md", "turn-0004.md", "turn-0005.md"],
            result.TurnFiles.Take(5).Select(static file => Path.GetFileName(file)));
        string opening = await File.ReadAllTextAsync(result.TurnFiles[0]);
        Assert.Contains("- **Outcome:** Accepted", opening);
        Assert.Contains(OpeningLine, opening);
        Assert.Contains("char:ally has been detained", opening);
        Assert.Contains("char:vadim has prepared his questions", opening);
        Assert.DoesNotContain("{{player.FullId}}", opening);
        string invalidated = await File.ReadAllTextAsync(result.TurnFiles[1]);
        Assert.Contains("- **Outcome:** Discarded Before Response", invalidated);
        string deliveryTurn = await File.ReadAllTextAsync(result.TurnFiles[2]);
        Assert.Contains("- **Outcome:** Invalid Response — Recovery Scheduled", deliveryTurn);
        Assert.Contains(VadimInterrogationBaseline.PlayerLine, deliveryTurn);
        Assert.Contains("runtime-authored", deliveryTurn);
        Assert.Contains("no function calls", deliveryTurn, StringComparison.Ordinal);
        string engaged = await File.ReadAllTextAsync(result.TurnFiles[3]);
        Assert.Contains("- **Outcome:** Accepted", engaged);
        Assert.Contains(EngagedLine, engaged);
        Assert.Contains("Stripped Text From Retained Exchange", engaged);
        Assert.Contains(ToleratedText, engaged);
        string judgeEvidence = VadimNameEngagementEvaluator.BuildEvidence(result);
        Assert.Contains(EngagedLine, judgeEvidence, StringComparison.Ordinal);
        Assert.Contains("Invalid Response", judgeEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(OpeningLine, judgeEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(ToleratedText, judgeEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("## Instructions", judgeEvidence, StringComparison.Ordinal);
        string closing = await File.ReadAllTextAsync(result.TurnFiles[4]);
        Assert.Contains("- **Outcome:** Accepted", closing);
        Assert.Contains("Wait ended:", closing);
    }

    /// <summary>
    /// Scripts the baseline-shaped turns: opening speak, a hold-forever stand-in for the request the player-line
    /// injection invalidates (its replacement is the delivery request), an invalid delivery-turn response, a
    /// tolerated text-plus-speak recovery, a closing wait, and a final hold-forever for the request that node
    /// exit cancels after the window.
    /// </summary>
    private static void ScriptBaselineTurns(LiveLLMScriptedChatClient target)
    {
        _ = target.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [SpeakCall("turn-1-speak", OpeningLine)])));
        // The scenario injects the player line while this held request is in flight; the fresh observation
        // invalidates it, so this responder only ever observes its own cancellation.
        _ = target.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
        });
        // The delivery request's response is invalid (text without a call): the discard and its recovery are
        // part of the evidence the judge compares against the baseline's discarded engaged turn.
        _ = target.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            "I will wait for her answer.")));
        _ = target.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextContent(ToleratedText),
                SpeakCall("turn-3-speak", EngagedLine),
            ])));
        _ = target.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(
                "turn-4-wait",
                "wait",
                new Dictionary<string, object?> { ["seconds"] = 0.05f })])));
        _ = target.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
        });
    }

    private static FunctionCallContent SpeakCall(string callId, string speech)
        => new(
            callId,
            "speak",
            new Dictionary<string, object?> { ["speech"] = speech });

    private static string DescribeFailure(LiveLLMScenarioExecution execution)
        => execution.Failure is null
            ? "The execution completed with an unexpected failure."
            : $"Classification {execution.Failure.Classification}: {execution.Failure.SafeMessage}";

    /// <summary>
    /// Creates a task-specific scratch root under <c>game/temp</c> — the project's git-ignored scratch area —
    /// preserving earlier evidence and isolating the recorder's turn-file listing for this run.
    /// </summary>
    private static string CreateScenarioRoot(string name)
    {
        DirectoryInfo liveTraces = LiveLLMTraceWriter.ResolveDefaultRoot();
        DirectoryInfo scenarioRoot = new(Path.Combine(
            liveTraces.Parent!.FullName, name, Guid.NewGuid().ToString("N")));

        scenarioRoot.Create();
        return scenarioRoot.FullName;
    }
}
