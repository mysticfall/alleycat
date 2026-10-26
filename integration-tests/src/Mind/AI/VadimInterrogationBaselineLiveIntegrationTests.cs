using AlleyCat.IntegrationTests.Support.AI;
using AlleyCat.Mind.AI;
using AlleyCat.TestFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Live-LLM execution stage of the Vadim interrogation baseline experiment (plan step 4's target): the
/// deterministic scenario driven against the configured live model, with every provider roundtrip traced and the
/// production session-transcript turn files captured as judging evidence.
/// </summary>
/// <remarks>
/// <para>
/// Excluded from discovery and execution unless <c>--live-llm</c> is supplied. The target client comes from
/// <see cref="LiveLLMClientFactory.CreateLiveClients()" /> — the only authorised <c>AITest</c> credential
/// boundary. The pair's separate judge client is built from the same <c>AITest</c> settings — no divergent
/// target/judge configuration exists today — and stays unused here, reserved for the judged phase.
/// </para>
/// <para>
/// Operational prerequisites for the run: <c>AITest:Host</c>, <c>AITest:Model</c> (for the experiment,
/// <c>qwen/qwen3.8-27b</c>), and <c>AITest:ApiKey</c> configured in the user override
/// (<c>~/.local/share/godot/app_userdata/AlleyCat/AlleyCat.yaml</c>), and the whole-fact budget raised
/// (<c>ALLEYCAT_GODOT_RUN_FACT_TIMEOUT_MS=600000</c>) because the scenario's declared timeout alone is eight
/// minutes for up to twelve provider roundtrips.
/// </para>
/// <para>
/// Deterministic assertions cover only the captured evidence's plumbing — the delivery request's shape and the
/// completed window — never the model's phrasing; acceptance of the behaviour itself belongs to the judged
/// stage over the retained artefacts (llm-experiment-driven-development: execution, evaluation, and assertion
/// are separate stages).
/// </para>
/// </remarks>
[LiveLlm]
[Headless]
public sealed class VadimInterrogationBaselineLiveIntegrationTests
{
    /// <summary>
    /// The baseline scenario runs against the live target model: the scripted player line is delivered through a
    /// runtime-authored synthetic scene-update exchange, the observation window completes two requests after
    /// delivery, and the trace plus turn files are retained under the live-traces artefact root for judging.
    /// </summary>
    [Fact]
    public async Task BaselineScenario_CapturesWindowEvidenceAgainstTheLiveTarget()
    {
        string? configuredModel = Game.Instance.GetRequiredService<IConfiguration>()["AITest:Model"]?.Trim();
        Assert.True(
            configuredModel is "qwen3.8-27b" or "qwen/qwen3.8-27b",
            "This experiment requires AITest:Model to select qwen3.8-27b; do not substitute another model.");
        DirectoryInfo liveTraces = LiveLLMTraceWriter.ResolveDefaultRoot();
        string runRoot = Path.Combine(
            liveTraces.FullName,
            $"{VadimInterrogationBaseline.ScenarioName}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        VadimInterrogationScenario scenario = new(Path.Combine(runRoot, "transcripts"));
        VadimInterrogationResult? result = null;

        using LiveLLMClientPair clients = LiveLLMClientFactory.CreateLiveClients();
        LiveLLMScenarioExecutor executor = new(clients.Target);
        LiveLLMScenarioExecution execution = await executor.ExecuteAsync(
            VadimInterrogationBaseline.ScenarioName,
            VadimInterrogationBaseline.LiveBounds,
            async context => result = await scenario.RunAsync(context));

        // The trace is retained whatever the outcome, so a failed run still carries its partial evidence.
        FileInfo traceFile = new LiveLLMTraceWriter(liveTraces).Write(execution.Trace);
        Assert.True(execution.Succeeded, execution.Failure is null
            ? "The execution failed without a failure record."
            : $"Classification {execution.Failure.Classification}: {execution.Failure.SafeMessage}");
        Assert.NotNull(result);
        Assert.True(traceFile.Exists);

        // Deterministic plumbing assertions over the captured evidence only (AI-002 TR-2/3/18): the observation
        // window completed, the player line became model-visible through the synthetic exchange, and no retired
        // event-history shape exists anywhere in the delivery request.
        Assert.True(result.WindowCompleted, "The observation window never completed.");
        VadimInterrogationRequest delivery = result.CompletedRequests[result.DeliveryRequestIndex];
        Assert.True(
            result.CompletedRequests
                .Skip(result.DeliveryRequestIndex + 1)
                .All(request => request.Messages
                    .SelectMany(static message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .Count(static call => call.Name == SyntheticSceneUpdateExchange.DeliveryToolName) == 1),
            "A window request redelivered the player line through a second synthetic exchange.");
        FunctionCallContent sceneUpdate = Assert.Single(
            delivery.Messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>(),
            static call => call.Name == SyntheticSceneUpdateExchange.DeliveryToolName);
        Assert.StartsWith(
            SyntheticSceneUpdateExchange.SyntheticCallIDPrefix,
            sceneUpdate.CallId,
            StringComparison.Ordinal);
        Assert.Contains(
            VadimInterrogationBaseline.PlayerLineDeliveryPrefix,
            string.Join(
                string.Empty,
                delivery.Messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Where(content => content.CallId == sceneUpdate.CallId)
                    .Select(static content => content.Result?.ToString() ?? string.Empty)),
            StringComparison.Ordinal);
        string joined = string.Join('\n', delivery.Messages.Select(static message => message.Text));
        Assert.DoesNotContain("<Event History>", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("[new]", joined, StringComparison.Ordinal);
        Assert.NotEmpty(result.TurnFiles);
    }
}
