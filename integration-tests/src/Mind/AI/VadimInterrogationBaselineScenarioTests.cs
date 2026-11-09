using AlleyCat.IntegrationTests.Support;
using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Deterministic fake-client smoke for the fail-closed opening gate of the Vadim interrogation path: the authored
/// Vadim NPC scene drives the real production Mind, tool execution, speech-perception/Hearing delivery, synthetic
/// scene updates, and AI-011 transcript recorder, and a non-question opening — the prior candidate trial's actual
/// live opening — stops the scenario without ever publishing the player's full name. Zero network access
/// (AI-002 TR-2/3/12/17/18).
/// </summary>
[Headless]
public sealed class VadimInterrogationBaselineScenarioTests
{
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
