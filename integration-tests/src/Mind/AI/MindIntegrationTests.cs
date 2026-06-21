using System.Runtime.CompilerServices;
using AlleyCat.Body.Voice;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Isolated runtime coverage for the migrated AgenticMind speech path without reference character assets or backend calls.
/// </summary>
[Headless]
public sealed partial class MindIntegrationTests : IDisposable
{
    private readonly AIPipelineDebugLogFixture _debugLogFixture = new();

    /// <summary>
    /// Clears the isolated AI pipeline logger override after each test.
    /// </summary>
    public void Dispose() => _debugLogFixture.Dispose();

    /// <summary>
    /// Player speech should become a runtime turn whose first speak-tool call cancels the backend run when diagnostics are disabled.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenRequestResponseDiagnosticsDisabled_CancelsRunAfterFirstAcceptedSpeak()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "player",
        };
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "First reply.",
            SecondSpeech = "Second reply should not be attempted.",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 1f,
            PostReplyListenCooldownSeconds = 0f,
            Tools = [new SpeechTool()],
        };
        mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(EnableRequestResponseLogging: false));

        clientProvider.AfterFirstSpeakAsync = () =>
        {
            mind.ReceiveVoice("interrupting player speech", playerVoice);
            return Task.CompletedTask;
        };

        IServiceProvider toolServices = mind;
        Assert.Same(mind, toolServices.GetService(typeof(AgenticMind)));
        IVoice toolVoice = Assert.IsAssignableFrom<IVoice>(toolServices.GetService(typeof(IVoice)));
        Assert.Equal("alley", toolVoice.Id);

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("  hello Alley  ", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { Completed: true }, maxFrames: 120);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.NotNull(clientProvider.Client);
            FakeChatClient client = clientProvider.Client;
            Assert.Equal(1, client.RunCount);
            Assert.Contains("- Speech from player: hello Alley", Assert.Single(client.Prompts));
            Assert.Equal("Spoken through the configured voice.", client.FirstSpeakResult);
            Assert.True(client.CancellationObservedAfterFirstSpeak);
            Assert.False(client.ReturnedResponse);
            Assert.Equal("Ignored because this turn already accepted a speak request.", client.SecondSpeakResult);
            Assert.Equal(["First reply."], npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// Request/response diagnostics should keep the backend run alive so AgentResponse diagnostics can inspect the result.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenRequestResponseDiagnosticsEnabled_AllowsRunCompletionAndDuplicateSpeakProtection()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "player",
        };
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "First diagnostic reply.",
            SecondSpeech = "Second diagnostic reply should be ignored.",
            ResponseText = "general agent response diagnostics text",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 1f,
            PostReplyListenCooldownSeconds = 0f,
            Tools = [new SpeechTool()],
        };
        mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(EnableRequestResponseLogging: true));

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("hello with diagnostics", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { Completed: true }, maxFrames: 120);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.NotNull(clientProvider.Client);
            FakeChatClient client = clientProvider.Client;
            Assert.Equal(1, client.RunCount);
            Assert.False(client.CancellationObservedAfterFirstSpeak);
            Assert.True(client.ReturnedResponse);
            Assert.Equal("Spoken through the configured voice.", client.FirstSpeakResult);
            Assert.Equal("Ignored because this turn already accepted a speak request.", client.SecondSpeakResult);
            Assert.Equal(["First diagnostic reply."], npcVoice.SpokenLines);

            string diagnostics = AgenticMind.CreateSensitiveTrialAgentResponseDiagnostics(
                new AgentResponse(new ChatMessage(ChatRole.Assistant, client.ResponseText)));
            Assert.Contains("Text=general agent response diagnostics text", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Messages=1", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// AgenticMind should select exported tools for every turn through per-invocation ChatOptions.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenToolsChangeBetweenTurns_SendsCurrentToolsInRunOptions()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "player",
        };
        FakeClientProvider clientProvider = new();
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 1f,
            PostReplyListenCooldownSeconds = 0f,
            Tools = [new MarkerTool("first_tool")],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("first turn", playerVoice);
            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { RunCount: 1, Completed: true }, maxFrames: 120);

            mind.Tools = [new MarkerTool("second_tool")];
            clientProvider.Client!.Completed = false;
            mind.ReceiveVoice("second turn", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { RunCount: 2, Completed: true }, maxFrames: 120);

            Assert.NotNull(clientProvider.Client);
            Assert.Equal(2, clientProvider.Client.RunCount);
            Assert.Equal(["first_tool", "second_tool"], clientProvider.Client.ToolNamesByRun);
            Assert.Empty(npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// Backend creation failures should be contained by AgenticMind so the scene keeps running and no NPC speech emits.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenBackendCreationFails_DoesNotCrashOrSpeak()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "player",
        };
        ThrowingClientProvider clientProvider = new();
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 1f,
            PostReplyListenCooldownSeconds = 0f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("backend unavailable", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.CreateChatClientCallCount == 1, maxFrames: 120);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.Empty(npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// AgenticMind should accept IVoice identifiers directly, without requiring the concrete Voice node type for input.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WithNonNodeIVoice_UsesInterfaceIdForPlayerRouting()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        PlainVoice playerVoice = new("player");
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "Interface reply.",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 1f,
            PostReplyListenCooldownSeconds = 0f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("hello through interface", playerVoice);

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { Completed: true }, maxFrames: 120);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.NotNull(clientProvider.Client);
            Assert.Contains("- Speech from player: hello through interface", Assert.Single(clientProvider.Client.Prompts));
            Assert.Equal(["Interface reply."], npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, npcVoice);
        }
    }

    /// <summary>
    /// Below-threshold speech should wait for the configured maximum observation wait instead of polling frequently.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenBelowWeightThreshold_RunsAfterMaxObservationWait()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        RecordingVoice playerVoice = new()
        {
            Id = "player",
        };
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "Delayed reply.",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 2f,
            PostReplyListenCooldownSeconds = 0f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, playerVoice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("below threshold", playerVoice);
            await TestUtils.WaitForNextFrameAsync(sceneTree);

            Assert.Null(clientProvider.Client);

            await WaitUntilAsync(sceneTree, () => clientProvider.Client is { Completed: true }, maxFrames: 120);
            Assert.Equal(["Delayed reply."], npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, playerVoice, npcVoice);
        }
    }

    /// <summary>
    /// The base Mind processing loop should run immediately once cumulative observation weight reaches the threshold.
    /// </summary>
    [Fact]
    public async Task Observe_WhenWeightThresholdReached_ProcessesThroughBaseMindLoop()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            ObservationWeightThreshold = 1f,
        };

        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "important"));

            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, maxFrames: 120);

            Assert.Equal("important", Assert.Single(Assert.Single(mind.ProcessedBatches)).ToPromptString());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Disabling Mind should stop deferred timer starts and preserve pending observations until re-enabled.
    /// </summary>
    [Fact]
    public async Task EnabledFalse_StopsDeferredTimerAndPreservesPendingObservationsUntilReenabled()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestMind mind = new()
        {
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 2f,
        };

        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "deferred"));
            mind.Enabled = false;

            await TestUtils.WaitForFramesAsync(sceneTree, 20);
            Assert.Empty(mind.ProcessedBatches);
            Assert.True(mind.HasPendingObservationsForTest);

            mind.Enabled = true;

            await WaitUntilAsync(sceneTree, () => mind.ProcessedBatches.Count == 1, maxFrames: 120);
            Assert.Equal("deferred", Assert.Single(Assert.Single(mind.ProcessedBatches)).ToPromptString());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind);
        }
    }

    /// <summary>
    /// Disabled AgenticMind should not queue voice observations or create backend clients.
    /// </summary>
    [Fact]
    public async Task ReceiveVoice_WhenDisabled_DoesNotRunAgenticMindTurn()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        RecordingVoice npcVoice = new()
        {
            Id = "alley",
        };
        PlainVoice playerVoice = new("player");
        FakeClientProvider clientProvider = new()
        {
            FirstSpeech = "Should not speak.",
        };
        AgenticMind mind = new()
        {
            ClientProvider = clientProvider,
            Enabled = false,
            SystemInstruction = CreateTestSystemInstruction(),
            Voice = npcVoice,
            MaxObservationWaitSeconds = 0.05f,
            ObservationWeightThreshold = 1f,
            PostReplyListenCooldownSeconds = 0f,
            Tools = [new SpeechTool()],
        };

        AddTestNode(sceneTree, npcVoice);
        AddTestNode(sceneTree, mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ReceiveVoice("ignored while disabled", playerVoice);

            await TestUtils.WaitForFramesAsync(sceneTree, 20);

            Assert.Null(clientProvider.Client);
            Assert.Empty(npcVoice.SpokenLines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, mind, npcVoice);
        }
    }

    private static void AddTestNode(SceneTree sceneTree, Node node)
    {
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(node);
    }

    private static PromptStack CreateTestSystemInstruction() => new()
    {
        Sections =
        [
            new TextPromptSection
            {
                Name = "Test Instructions",
                Text = "Run the integration test turn.",
            },
        ],
    };

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, params Node[] nodes)
    {
        foreach (Node node in nodes)
        {
            node.QueueFree();
        }

        await TestUtils.WaitForFramesAsync(sceneTree, 2);
    }

    private sealed partial class RecordingVoice : Voice
    {
        public List<string> SpokenLines { get; } = [];

        public override void Speak(string speech)
        {
            SpokenLines.Add(speech);
            _ = TryNotifySpeechGeneratedWhenEnabled(speech);
        }
    }

    private sealed partial class TestMind : MindBase
    {
        public List<IReadOnlyList<AgentObservation>> ProcessedBatches { get; } = [];

        public bool HasPendingObservationsForTest => HasPendingObservations;

        public void ObserveForTest(AgentObservation observation) => _ = Observe(observation);

        public override void ReceiveVoice(string speech, IVoice source)
        {
            if (ShouldHandleVoice(speech, source))
            {
                _ = Observe(new TestObservation(1f, speech.Trim()));
            }
        }

        protected override Task ProcessObservationsAsync(
            IReadOnlyList<AgentObservation> observations,
            CancellationToken cancellationToken)
        {
            ProcessedBatches.Add([.. observations]);
            return Task.CompletedTask;
        }
    }

    private sealed record TestObservation(float Importance, string Prompt) : AgentObservation(Importance)
    {
        public override string ToPromptString() => Prompt;
    }

    private sealed partial class FakeClientProvider : ClientProvider
    {
        public string FirstSpeech { get; init; } = string.Empty;

        public string SecondSpeech { get; init; } = string.Empty;

        public string ResponseText { get; init; } = string.Empty;

        public Func<Task>? AfterFirstSpeakAsync
        {
            get;
            set;
        }

        public FakeChatClient? Client
        {
            get;
            private set;
        }

        public override IChatClient CreateChatClient()
        {
            Client = new FakeChatClient(FirstSpeech, SecondSpeech, ResponseText, AfterFirstSpeakAsync);
            return Client;
        }
    }

    private sealed partial class ThrowingClientProvider : ClientProvider
    {
        public int CreateChatClientCallCount
        {
            get;
            private set;
        }

        public override IChatClient CreateChatClient()
        {
            CreateChatClientCallCount++;
            throw new InvalidOperationException("Backend configuration is invalid for test.");
        }
    }

    private sealed class FakeChatClient(
        string firstSpeech,
        string secondSpeech,
        string responseText,
        Func<Task>? afterFirstSpeakAsync) : IChatClient
    {
        public int RunCount
        {
            get;
            private set;
        }

        public bool Completed
        {
            get;
            set;
        }

        public List<string> Prompts { get; } = [];

        public List<string> ToolNamesByRun { get; } = [];

        public string ResponseText => responseText;

        public bool CancellationObservedAfterFirstSpeak
        {
            get;
            private set;
        }

        public bool ReturnedResponse
        {
            get;
            private set;
        }

        public string? FirstSpeakResult
        {
            get;
            private set;
        }

        public string? SecondSpeakResult
        {
            get;
            private set;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            Prompts.Add(messages.Last().Text);

            try
            {
                Assert.NotNull(options);
                Assert.NotNull(options.Tools);
                AIFunction toolFunction = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options.Tools));
                ToolNamesByRun.Add(toolFunction.Name);

                if (toolFunction.Name != "speak")
                {
                    _ = await toolFunction.InvokeAsync([], cancellationToken);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
                }

                FirstSpeakResult = await InvokeSpeakAsync(toolFunction, firstSpeech, cancellationToken);

                if (afterFirstSpeakAsync is not null)
                {
                    await afterFirstSpeakAsync();
                }

                SecondSpeakResult = await InvokeSpeakAsync(toolFunction, secondSpeech, CancellationToken.None);

                if (cancellationToken.IsCancellationRequested)
                {
                    CancellationObservedAfterFirstSpeak = true;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ReturnedResponse = true;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
            }
            finally
            {
                Completed = true;
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static async Task<string?> InvokeSpeakAsync(
            AIFunction speakFunction,
            string speech,
            CancellationToken cancellationToken)
        {
            AIFunctionArguments arguments = new()
            {
                ["speech"] = speech,
            };

            object? result = await speakFunction.InvokeAsync(arguments, cancellationToken);
            return result?.ToString();
        }
    }

    private sealed class PlainVoice(string id) : IVoice
    {
        public string Id => id;

        public Vector3 Origin => Vector3.Zero;

        public void Speak(string speech)
        {
        }
    }

    private sealed partial class MarkerTool : AgentTool
    {
        public MarkerTool(string toolName)
        {
            ToolName = toolName;
            ToolDescription = "Records that this test tool was selected.";
        }

        protected override Delegate CreateDelegate() => Mark;

        private static string Mark() => "Marked.";
    }
}
