using System.Text.Json;
using System.Text.RegularExpressions;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Perception;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

public sealed partial class MindSessionTranscriptIntegrationTests
{
    private const string KoreanPayload = "안녕하세요 \"강주영\"\\기록\n다음 줄";

    /// <summary>
    /// Approved readable-debug-JSON contract: a complete real runner/tool/recorder cycle, not formatter-only output.
    /// AgentTool projects feedback to strings, so a runner-level AIFunction supplies the supported structured result.
    /// </summary>
    [Fact]
    public async Task KoreanJson_CompletedScriptedCycle_WritesReadableRoundTrippableTurnAndIndex()
    {
        string root = CreateUnicodeEvidenceRoot("structured-cycle");
        ScriptedSessionClientProvider provider = new();
        using CancellationTokenSource lifetime = new();
        Task? run = null;
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            Dictionary<string, object?> arguments = new()
            {
                ["text"] = KoreanPayload
            };
            FunctionCallContent responseCall = new("korean-call", "echo_korean", arguments);
            provider.EnqueueCallBatch(responseCall);
            TaskCompletionSource nextRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.EnqueueHold(nextRequest);
            string? deliveredArgument = null;
            AIFunction function = AIFunctionFactory.Create((string text) =>
            {
                deliveredArgument = text;
                return new Dictionary<string, string> { ["reply"] = text };
            }, name: "echo_korean");
            using IChatClient client = provider.CreateChatClient();
            var recorder = new MindSessionTranscriptRecorder("owner", () => root, NullLogger.Instance);
            var runner = new AgentSessionRunner(
                client, "Scripted Korean JSON regression.", [new ChatMessage(ChatRole.User, "Begin.")],
                [function], NullLogger.Instance, transcriptSink: recorder);
            run = runner.RunAsync(lifetime.Token);
            // The next request starts only after cycle one has settled and both files have been written.
            await nextRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string directory = GetCharacterDirectory(root);
            string turn = File.ReadAllText(Path.Combine(directory, "turn-0001.md"));
            string index = File.ReadAllText(Path.Combine(directory, "index.md"));
            // Cleanup interrupts held cycle two and updates the live index. Retain the exact observed window.
            File.WriteAllText(Path.Combine(root, "observed-turn-0001.md"), turn);
            File.WriteAllText(Path.Combine(root, "observed-index.md"), index);
            Assert.Contains("- **Outcome:** Accepted", turn);
            Assert.Contains("[Turn 0001](turn-0001.md)", index);
            Assert.Equal(KoreanPayload, deliveredArgument);
            Assert.Equal(KoreanPayload, responseCall.Arguments!["text"]);
            FunctionResultContent replayedResult = Assert.Single(provider.Requests[1]
                .SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            JsonElement result = Assert.IsType<JsonElement>(replayedResult.Result);
            Assert.Equal(KoreanPayload, result.GetProperty("reply").GetString());

            // First prove valid fences and semantic preservation, then require literal Korean in EVERY JSON block.
            List<string> turnBlocks = ReadKoreanJsonBlocks(turn, expectedCount: 2);
            List<string> indexBlocks = ReadKoreanJsonBlocks(index, expectedCount: 1);
            // Structured results render as bare pretty JSON, unlike argument fences. Do not demand new fencing.
            turnBlocks.Add(ReadStructuredKoreanResult(turn));
            indexBlocks.Add(ReadStructuredKoreanResult(index));
            foreach (string block in turnBlocks.Concat(indexBlocks))
            {
                Assert.Contains("안녕하세요", block);
                Assert.Contains("강주영", block);
                Assert.DoesNotContain("\\uC548", block, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try
            {
                await lifetime.CancelAsync();
                if (run is not null)
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                provider.Free();
                MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
                MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            }
        }
    }

    internal static async Task AssertInstalledCharacterCycleAsync(ICharacter owner, bool requireReadableJson = false)
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        string root = CreateUnicodeEvidenceRoot($"installed-{owner.Id}");
        FixtureBag fixtures = new();
        TestAgenticMind? mind = null;
        using RecordingLoggerProvider logs = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(root);
            FixturePlayerCharacter player = fixtures.Add(new FixturePlayerCharacter());
            KoreanEchoTool tool = fixtures.Add(new KoreanEchoTool());
            ScriptedSessionClientProvider provider = fixtures.Add(new ScriptedSessionClientProvider());
            provider.EnqueueCallBatch(new FunctionCallContent("korean-call", "echo_korean",
                new Dictionary<string, object?> { ["text"] = KoreanPayload }));
            TaskCompletionSource nextRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            provider.EnqueueHold(nextRequest);
            mind = CreateTranscriptMind(owner, player, provider, tool);
            // The installed character exposes Eyes; keep its real faculty pairing rather than stripping senses.
            mind.AddChild(new VisualSurveyPerception());
            mind.AddChild(new VisualDescriptionPerception());
            mind.AddChild(new RelativePositionPerception());
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
            await WaitUntilAsync(sceneTree, () => nextRequest.Task.IsCompleted);
            Assert.True(nextRequest.Task.IsCompleted,
                $"Session ended before completing the scripted cycle. Requests: {provider.Requests.Count}; "
                + $"inside tree: {mind.IsInsideTree()}; ready: {mind.IsNodeReady()}; "
                + string.Join('\n', logs.Entries.Select(entry => $"{entry.Message}: {entry.Exception}")));
            string runDirectory = Assert.Single(Directory.GetDirectories(root));
            string directory = Assert.Single(Directory.GetDirectories(runDirectory));
            Assert.Equal(owner.Id, Path.GetFileName(directory));
            string turn = File.ReadAllText(Path.Combine(directory, "turn-0001.md"));
            string index = File.ReadAllText(Path.Combine(directory, "index.md"));
            Assert.Contains("- **Outcome:** Accepted", turn);
            Assert.Contains($"- **Character:** {owner.Id}", turn);
            Assert.Contains("[Turn 0001](turn-0001.md)", index);
            Assert.Equal(KoreanPayload, tool.DeliveredArgument);
            FunctionResultContent replayed = Assert.Single(provider.Requests[1]
                .SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            Assert.Equal(KoreanPayload, Assert.IsType<string>(replayed.Result));
            if (requireReadableJson)
            {
                File.WriteAllText(Path.Combine(root, "observed-turn-0001.md"), turn);
                File.WriteAllText(Path.Combine(root, "observed-index.md"), index);
                List<string> arguments = ReadKoreanJsonBlocks(turn, expectedCount: 2);
                arguments.AddRange(ReadKoreanJsonBlocks(index, expectedCount: 1));
                foreach (string block in arguments)
                {
                    Assert.Contains("안녕하세요", block);
                    Assert.Contains("강주영", block);
                    Assert.DoesNotContain("\\uC548", block, StringComparison.OrdinalIgnoreCase);
                }
                FunctionCallContent replayedCall = Assert.Single(provider.Requests[1]
                    .SelectMany(message => message.Contents).OfType<FunctionCallContent>());
                Assert.Equal(KoreanPayload, replayedCall.Arguments!["text"]);
            }
        }
        finally
        {
            try
            {
                _ = Directory.CreateDirectory(root);
                File.WriteAllLines(Path.Combine(root, "runtime.log"), logs.Entries.Select(entry =>
                    $"{entry.Level}: {entry.Message}\n{entry.Exception}"));
            }
            finally
            {
                await CleanupSessionScopeAsync(sceneTree, mind, fixtures, root, deleteTree: false);
            }
        }
    }

    private static string ReadStructuredKoreanResult(string markdown)
    {
        string result = markdown[(markdown.IndexOf("**Result**", StringComparison.Ordinal) + "**Result**".Length)..].Trim();
        using var json = JsonDocument.Parse(result);
        Assert.Equal(KoreanPayload, json.RootElement.GetProperty("reply").GetString());
        return result;
    }

    private static List<string> ReadKoreanJsonBlocks(string markdown, int expectedCount)
    {
        List<string> payloadBlocks = [];
        foreach (Match match in Regex.Matches(markdown, @"```json\n(?<json>[\s\S]*?)\n```"))
        {
            string block = match.Groups["json"].Value;
            using var json = JsonDocument.Parse(block);
            if (json.RootElement.TryGetProperty("text", out JsonElement text)
                || json.RootElement.TryGetProperty("reply", out text))
            {
                Assert.Equal(KoreanPayload, text.GetString());
                payloadBlocks.Add(block);
            }
        }
        Assert.Equal(expectedCount, payloadBlocks.Count);
        return payloadBlocks;
    }

    private static string CreateUnicodeEvidenceRoot(string scenario)
        => Path.Combine(ProjectSettings.GlobalizePath("res://temp/unicode-candidate"), scenario,
            Guid.NewGuid().ToString("N"));

    private sealed partial class KoreanEchoTool : AgentTool
    {
        public KoreanEchoTool()
        {
            ToolName = "echo_korean";
            ToolDescription = "Return the supplied text unchanged.";
        }

        public string? DeliveredArgument
        {
            get; private set;
        }

        protected override Delegate CreateDelegate() => Echo;

        private ValueTask<AgentToolResult> Echo(string text)
        {
            DeliveredArgument = text;
            return ValueTask.FromResult(new AgentToolResult(text));
        }
    }
}
