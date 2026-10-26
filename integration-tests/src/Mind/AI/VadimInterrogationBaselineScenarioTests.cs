using System.Text.Json;
using System.Text.RegularExpressions;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Deterministic fake-client smoke for the representative Vadim interrogation path: the authored Vadim NPC scene
/// drives the real production Mind, tool execution, speech-perception/Hearing delivery, synthetic scene updates,
/// and AI-011 transcript recorder end-to-end — the opening full-name question settles, the scripted player line is
/// published through a real voice into the authored Hearing at the baseline's ~43s game time, the observation
/// window completes two settled requests after delivery, and the fail-closed opening gate consumes the slot
/// without delivering the name when the precondition is missing. Zero network access
/// (AI-002 TR-2/3/12/17/18; the parent outcome contract's evidence requirements).
/// </summary>
/// <remarks>
/// <para>
/// The scripted turns mirror the baseline's shape: an opening <c>speak</c> asking for the full name, one invalid
/// response whose recovery proves discard capture, one valid response carrying text alongside its <c>speak</c>
/// call — the tolerated and stripped shape that would have discarded the baseline's engaged turn 6 — and a
/// closing <c>wait</c>. Hold-forever steps stand in for the request invalidated by the delivery and whatever the
/// model requests after the window closes; the scenario's node exit cancels both mid-flight exactly as it will
/// cancel the live run's requests.
/// </para>
/// <para>
/// Every provider roundtrip flows through the executor's traced target client — the identical plumbing the live
/// run uses — so trace capture, bounds, and artefact writing are exercised here without a live LLM. The
/// representative delivery wait is real: the scenario session clock advances one second per second, so the fact
/// waits out the ~43s question-to-delivery gap.
/// </para>
/// </remarks>
[Headless]
public sealed partial class VadimInterrogationBaselineScenarioTests
{
    private const string OpeningQuestion =
        "Ally. The recording is running. Please state your full name for the record.";

    private const string EngagedLine =
        "Ellie Onyx. The file lists you as Ally. State your employer for the record.";

    private const string ToleratedText = "She finally answered. I should proceed with the next question.";

    /// <summary>
    /// The representative vertical path: the authored scene's accepted, settled full-name question precedes the
    /// player line published through the authored Hearing at ~43s game time; the line becomes model-visible as a
    /// runtime-authored synthetic scene-update exchange before window response 1; the window completes two
    /// settled requests later; and the session ends with the quiet cancellation of the post-window request.
    /// </summary>
    [Fact]
    public async Task RepresentativePath_DeliversPlayerLineThroughAuthoredHearing_AndCapturesTheWindow()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string scenarioRoot = CreateScenarioRoot("vadim-interrogation-smoke");
        string transcriptRoot = Path.Combine(scenarioRoot, "transcripts");
        LiveLLMScriptedChatClient target = new("target");
        ScriptRepresentativeTurns(target);
        LiveLLMScenarioExecutor executor = new(target);
        VadimInterrogationScenario scenario = new(
            transcriptRoot,
            VadimInterrogationBaseline.SmokeBounds.Timeout);
        VadimInterrogationResult? result = null;

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            VadimInterrogationBaseline.ScenarioName,
            VadimInterrogationBaseline.SmokeBounds,
            async context => result = await scenario.RunAsync(context));

        // The scenario delegate completed: the precondition held, the window closed, and the session ended by
        // node exit, with the in-flight requests cancelled quietly rather than failing the execution.
        Assert.True(execution.Succeeded, DescribeFailure(execution));
        Assert.NotNull(result);
        Assert.True(result.PreconditionSatisfied);
        Assert.Equal(VadimOpeningKind.FullNameQuestion, result.OpeningKind);
        Assert.Equal(OpeningQuestion, result.OpeningAcceptedSpeech);
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
        Assert.True(
            openingCall.Arguments!.TryGetValue("speech", out object? openingSpeech)
                && (string?)openingSpeech == OpeningQuestion,
            "The retained opening question must replay in the delivery request.");
        FunctionCallContent sceneUpdate = Assert.IsType<FunctionCallContent>(
            Assert.Single(delivery.Messages[3].Contents));
        Assert.Equal(SyntheticSceneUpdateExchange.DeliveryToolName, sceneUpdate.Name);
        Assert.StartsWith(
            SyntheticSceneUpdateExchange.SyntheticCallIDPrefix,
            sceneUpdate.CallId,
            StringComparison.Ordinal);
        string delivered = Assert.IsType<FunctionResultContent>(
            Assert.Single(delivery.Messages[4].Contents)).Result?.ToString() ?? string.Empty;

        // The delivered text proves both the Hearing-path boundary and the corrected chronology: the authored
        // addressee inference resolved Vadim (say to me), and the line carries the baseline's ~43s game-time
        // stamp, strictly after the settled opening question.
        Assert.Contains(VadimInterrogationBaseline.PlayerLineDeliveryPrefix, delivered, StringComparison.Ordinal);
        Assert.Matches(DeliveredAtBaselineGameTime(), delivered);
        double? deliveredAt = VadimInterrogationScenario.TryReadDeliveredLineGameTimeSeconds(delivery.Messages);
        _ = Assert.NotNull(deliveredAt);
        Assert.InRange(deliveredAt.Value, 43d, 46d);
        Assert.True(
            result.OpeningSettledGameTimeSeconds < deliveredAt.Value,
            "The settled opening question must precede the delivered line's game time.");
        Assert.Contains("Current game time:", delivery.Messages[^1].Text, StringComparison.Ordinal);

        // Delivery precedes window response 1: the delivered exchange is part of the request whose response is
        // the window's first response — the explicit chronology anchor the corrected judge evidence carries.
        Assert.True(VadimInterrogationScenario.DeliversPlayerLine(delivery.Messages));

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

        // Retention: the opening speak exchange replays in the requests that follow it (AI-002 TR-17), and the
        // tolerated text-plus-call response settles as a valid, stripped exchange (TR-12) — the exact shape the
        // old pipeline discarded at the baseline's turn 6 — so its accepted speak is itself retained in the
        // closing request.
        VadimInterrogationRequest recoveryRequest = result.CompletedRequests[2];
        Assert.Contains(
            recoveryRequest.Messages.SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>(),
            call => call.Name == "speak"
                && call.Arguments!.TryGetValue("speech", out object? speech)
                && speech?.ToString() == OpeningQuestion);
        VadimInterrogationRequest closingRequest = result.CompletedRequests[3];
        Assert.Contains(
            closingRequest.Messages.SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>(),
            call => call.Name == "speak"
                && call.Arguments!.TryGetValue("speech", out object? speech)
                && speech?.ToString() == EngagedLine);

        // Trace capture: all six roundtrips — the four that settle, the request the delivery invalidated, and
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

        // Turn-level evidence: the AI-011 recorder wrote one page per resolved cycle — the accepted opening
        // question, the request the delivery invalidated, the delivery request's invalid-response discard with
        // its reason, the stripped-text recovery, and the settled closing wait (AI-011 TR-6/TR-11). A
        // post-window cycle interrupted by session end may follow.
        Assert.InRange(result.TurnFiles.Count, 5, 6);
        Assert.Equal(
            ["turn-0001.md", "turn-0002.md", "turn-0003.md", "turn-0004.md", "turn-0005.md"],
            result.TurnFiles.Take(5).Select(static file => Path.GetFileName(file)));
        string opening = await File.ReadAllTextAsync(result.TurnFiles[0]);
        Assert.Contains("- **Outcome:** Accepted", opening);
        Assert.Contains(OpeningQuestion, opening);
        Assert.Contains("Spoken through the configured voice.", opening);
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
        string closing = await File.ReadAllTextAsync(result.TurnFiles[4]);
        Assert.Contains("- **Outcome:** Accepted", closing);
        Assert.Contains("Wait ended:", closing);

        // The corrected evidence projection over the real captured window: explicit chronology anchors with the
        // delivered game time, per-response settlement pairing, and no target instructions or private reasoning.
        string evidence = VadimNameEngagementEvaluator.BuildEvidence(result);
        Assert.Contains("\"deliveryPrecedesWindowResponse1\": true", evidence, StringComparison.Ordinal);
        using (var parsedEvidence = JsonDocument.Parse(evidence))
        {
            Assert.Equal(
                Math.Round(deliveredAt!.Value, 1),
                parsedEvidence.RootElement
                    .GetProperty("chronology")
                    .GetProperty("deliveredLine")
                    .GetProperty("deliveredGameTimeSeconds")
                    .GetDouble());
        }

        Assert.Contains(EngagedLine, evidence, StringComparison.Ordinal);
        Assert.Contains("Invalid Response", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("## Instructions", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("## Response", evidence, StringComparison.Ordinal);

        // Declared ceilings are preserved unchanged for the paid trial, and the smoke stays within them.
        Assert.Equal(12, VadimInterrogationBaseline.LiveBounds.MaxTargetRequests);
        Assert.Equal(TimeSpan.FromMinutes(8), VadimInterrogationBaseline.LiveBounds.Timeout);
        Assert.True(
            VadimInterrogationBaseline.SmokeBounds.MaxTargetRequests
                <= VadimInterrogationBaseline.LiveBounds.MaxTargetRequests);
        Assert.True(VadimInterrogationBaseline.SmokeBounds.Timeout <= VadimInterrogationBaseline.LiveBounds.Timeout);
    }

    /// <summary>
    /// A non-question opening — the prior candidate trial's actual live opening, which never asked for the name —
    /// fails the gate closed: the scenario stops without publishing the player line, no request or turn page ever
    /// carries the name, and the trial slot is consumed by the opening cycle alone.
    /// </summary>
    [Fact]
    public async Task PreconditionMissing_NonQuestionOpening_StopsWithoutDeliveringTheName()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string scenarioRoot = CreateScenarioRoot("vadim-interrogation-precondition");
        string transcriptRoot = Path.Combine(scenarioRoot, "transcripts");
        LiveLLMScriptedChatClient target = new("target");
        ScriptPreconditionMissingTurns(target);
        LiveLLMScenarioExecutor executor = new(target);
        VadimInterrogationScenario scenario = new(
            transcriptRoot,
            VadimInterrogationBaseline.SmokeBounds.Timeout);
        VadimInterrogationResult? result = null;

        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            VadimInterrogationBaseline.ScenarioName,
            VadimInterrogationBaseline.SmokeBounds,
            async context => result = await scenario.RunAsync(context));

        Assert.True(execution.Succeeded, DescribeFailure(execution));
        Assert.NotNull(result);
        Assert.False(result.PreconditionSatisfied);
        Assert.Equal(VadimOpeningKind.NonQuestion, result.OpeningKind);
        Assert.NotNull(result.OpeningAcceptedSpeech);
        Assert.False(result.WindowCompleted);
        Assert.Equal(-1, result.DeliveryRequestIndex);
        Assert.InRange(result.CompletedRequests.Count, 1, 2);

        // The name was never delivered: no request's messages and no turn page contain it.
        foreach (VadimInterrogationRequest request in result.CompletedRequests)
        {
            string joined = string.Join(
                '\n',
                request.Messages.Select(static message => message.Text
                    + string.Concat(message.Contents.OfType<FunctionResultContent>()
                        .Select(static content => content.Result?.ToString() ?? string.Empty))));
            Assert.DoesNotContain(VadimInterrogationBaseline.PlayerLine, joined, StringComparison.Ordinal);
        }

        foreach (string turnFile in result.TurnFiles)
        {
            Assert.DoesNotContain(
                VadimInterrogationBaseline.PlayerLine,
                await File.ReadAllTextAsync(turnFile),
                StringComparison.Ordinal);
        }

        // The slot is consumed by the opening cycle: the opening exchange is traced, and at most the cancelled
        // follow-up follows it.
        LiveLLMTraceEntry[] exchanges = [.. execution.Trace.Entries
            .Where(static entry => entry.Kind == LiveLLMTraceEntryKind.Exchange)];
        Assert.InRange(exchanges.Length, 1, 2);
        Assert.All(exchanges, static entry => Assert.Equal(LiveLLMPurpose.Target, entry.Purpose));
        Assert.Contains(exchanges, static entry => entry.Response is not null);
    }

    /// <summary>
    /// Scripts the representative turns: an opening speak asking the subject's full name, a hold-forever
    /// stand-in for the request the ~43s delivery invalidates (its replacement is the delivery request), an
    /// invalid delivery-turn response, a tolerated text-plus-speak recovery, a closing wait, and a final
    /// hold-forever for the request that node exit cancels after the window.
    /// </summary>
    private static void ScriptRepresentativeTurns(LiveLLMScriptedChatClient target)
    {
        _ = target.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [SpeakCall("turn-1-speak", OpeningQuestion)])));
        // The scenario publishes the player line while this held request is in flight; the fresh addressed
        // observation invalidates it, so this responder only ever observes its own cancellation.
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

    /// <summary>
    /// Scripts the precondition-missing turns: the prior candidate trial's actual non-question opening, and a
    /// hold-forever stand-in for the follow-up request that teardown cancels.
    /// </summary>
    private static void ScriptPreconditionMissingTurns(LiveLLMScriptedChatClient target)
    {
        _ = target.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [SpeakCall(
                "turn-1-speak",
                "Good morning, Ally. Take a seat. The session is recorded, so please answer only when "
                    + "the question has been asked.")])));
        _ = target.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
        });
    }

    [GeneratedRegex(@"\(at 4[3-9]\.\d+s game time\)")]
    private static partial Regex DeliveredAtBaselineGameTime();

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
