using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Deterministic provider-flow coverage for read-only lore retrieval: a scripted provider request asks
/// <c>read_lore</c> for exact entry IDs, the next provider request replays the retained exchange with the real
/// retrieved bodies, and the model's subsequent action consumes that context — with the session-start catalogue in
/// the static instruction, unchanged speak/wait disposal, whole mixed-batch retention, and no lore observations
/// (AI-002 UR-11, TR-23/24; AI-003 UR-9, TR-17/18; AI-004 acceptance 34).
/// </summary>
[Headless]
public sealed class ReadLoreFlowIntegrationTests
{
    private const string SharedStackPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string CurrentScenePromptPath = "res://assets/characters/prompts/current_scene.tres";

    /// <summary>
    /// The NPC's <c>read_lore</c> request is retained and reaches the next provider request with its real retrieved
    /// content before the model's subsequent speak action; the catalogue renders in the session-start system
    /// instruction and never in per-request scene status; retrieval creates no remembered events; and the settled
    /// speak exchange is disposed while the lore exchange stays model-visible.
    /// </summary>
    [Fact]
    public async Task ReadLore_ExchangeIsRetained_ReachesNextRequest_AndInformsTheSubsequentAction()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ImmediateVoice ownerVoice = new();
        FlowCharacter owner = new("test", ownerVoice);
        FlowCharacter ally = new("ally");
        FlowCharacter vadim = new("vadim");
        FixturePlayerCharacter player = new();
        var content = new ContentContext("lore-query-fixture", "res://tests/lore-query-fixture");
        ScriptedFlowClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(new FunctionCallContent(
            "lore-call-1",
            "read_lore",
            new Dictionary<string, object?>
            {
                ["entry_ids"] = new object[] { "test.interrogation_room", "test.missing" },
            }));
        clientProvider.EnqueueCallBatch(new FunctionCallContent(
            "speak-call-1",
            "speak",
            new Dictionary<string, object?> { ["speech"] = "You will find nothing down there but old questions." }));
        clientProvider.EnqueueHoldForever();
        TestFlowMind mind = new(owner)
        {
            SystemInstruction = Assert.IsType<PromptStack>(ResourceLoader.Load(SharedStackPath), exactMatch: false),
            CurrentSceneStatus = Assert.IsType<PromptStack>(
                ResourceLoader.Load(CurrentScenePromptPath),
                exactMatch: false),
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
            AttentionDecayPerSecond = 0f,
        };
        WatchRegistry watchRegistry = new()
        {
            Conditions = [new ProximityWatchTool()]
        };
        mind.AddChild(watchRegistry);
        mind.AddChild(new AttendedCharacterSceneStatusProjector
        {
            ProjectorID = AttendedCharacterSceneStatusProjector.ProjectorIDValue,
        });
        mind.AddChild(new WatchSceneStatusProjector
        {
            ProjectorID = WatchSceneStatusProjector.ProjectorIDValue,
            Registry = watchRegistry,
        });
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, ally, vadim, player], content));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 1);

            // The session-start system instruction carries the shared guidance, the unchanged automatic bodies, and
            // the grouped catalogue of remaining entries (AI-003 TR-17/18).
            CapturedFlowRequest opening = clientProvider.Requests[0];
            Assert.Contains("# Stable Entry", opening.Instructions, StringComparison.Ordinal);
            Assert.Contains("# char:ally", opening.Instructions, StringComparison.Ordinal);
            Assert.Contains("<Lore Catalogue>", opening.Instructions, StringComparison.Ordinal);
            Assert.Contains(
                "- `test.interrogation_room` — loc:interrogation_room — The room where they ask their questions.",
                opening.Instructions,
                StringComparison.Ordinal);
            Assert.Contains("- `test.nonessential`", opening.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("- `test.stable`", opening.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("- `test.ally`", opening.Instructions, StringComparison.Ordinal);
            Assert.Contains("read_lore", opening.Instructions, StringComparison.Ordinal);
            Assert.Contains("not a subject full ID", opening.Instructions, StringComparison.Ordinal);
            Assert.Contains("never required before every action", opening.Instructions, StringComparison.Ordinal);
            Assert.Contains("Lore Catalogue", opening.Tools["read_lore"], StringComparison.Ordinal);

            // The next provider request includes the real retrieved content before any subsequent action decision
            // (AI-002 TR-24).
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            CapturedFlowRequest afterRetrieval = clientProvider.Requests[1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                afterRetrieval.Messages.Select(static message => message.Role));
            Assert.Equal(AgenticMind.SessionBootstrapInput, afterRetrieval.Messages[2].Text);
            FunctionResultContent loreResult = Assert.IsType<FunctionResultContent>(
                Assert.Single(afterRetrieval.Messages[4].Contents.OfType<FunctionResultContent>()));
            Assert.Equal("lore-call-1", loreResult.CallId);
            string loreText = loreResult.Result?.ToString() ?? string.Empty;
            Assert.Contains("Entry 'test.interrogation_room':", loreText, StringComparison.Ordinal);
            Assert.Contains(
                "loc:interrogation_room sits below the charter office.",
                loreText,
                StringComparison.Ordinal);
            Assert.Contains("Entry 'test.missing': unavailable.", loreText, StringComparison.Ordinal);

            // The static instruction — including the catalogue — is compiled once and never rebuilt by the fresh
            // per-request scene status or the retrieval (AI-003 TR-17; AI-004 requirement 46).
            Assert.Equal(opening.Instructions, afterRetrieval.Instructions);
            Assert.Contains("Current game time", afterRetrieval.Messages[1].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Lore Catalogue", afterRetrieval.Messages[1].Text, StringComparison.Ordinal);

            // The subsequent action commits exactly one speech observation; lore retrieval created none.
            await WaitUntilAsync(sceneTree, () => mind.GetTimelineForTest().Count == 1);
            ObservedSpeech committed = Assert.IsType<ObservedSpeech>(mind.GetTimelineForTest()[0]);
            Assert.Equal(owner.FullId, committed.ActorId);
            Assert.Equal(["You will find nothing down there but old questions."], ownerVoice.Submissions);

            // The settled speak exchange is disposed while the retained lore exchange keeps reaching the model
            // (AI-002 TR-20/22).
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 3);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);
            CapturedFlowRequest afterAction = clientProvider.Requests[^1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                afterAction.Messages.Select(static message => message.Role));
            Assert.DoesNotContain(
                afterAction.Messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
                result => result.CallId == "speak-call-1");
            Assert.Contains(
                afterAction.Messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
                result => result.CallId == "lore-call-1");
            _ = Assert.Single(mind.GetTimelineForTest());
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A batch mixing the disposal-opted wait with read_lore is retained whole: both assistant calls and both tool
    /// results replay in the next request (AI-002 TR-20).
    /// </summary>
    [Fact]
    public async Task MixedBatch_WithWaitAndReadLore_IsRetainedWhole()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        FlowCharacter owner = new("test");
        FixturePlayerCharacter player = new();
        var content = new ContentContext("lore-query-fixture", "res://tests/lore-query-fixture");
        ScriptedFlowClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(
            new FunctionCallContent(
                "wait-call-1",
                "wait",
                new Dictionary<string, object?> { ["seconds"] = 0.05f }),
            new FunctionCallContent(
                "lore-call-2",
                "read_lore",
                new Dictionary<string, object?> { ["entry_ids"] = new object[] { "test.shared" } }));
        clientProvider.EnqueueHoldForever();
        TestFlowMind mind = new(owner)
        {
            SystemInstruction = new PromptStack
            {
                Sections = [new TextPromptSection { Text = "static", Name = "Static" }],
            },
            CurrentSceneStatus = Assert.IsType<PromptStack>(
                ResourceLoader.Load(CurrentScenePromptPath),
                exactMatch: false),
            ClientProvider = clientProvider,
            AllowMultipleToolCalls = true,
            ObservationImportanceThreshold = 1f,
        };
        WatchRegistry watchRegistry = new()
        {
            Conditions = [new ProximityWatchTool()]
        };
        mind.AddChild(watchRegistry);
        mind.AddChild(new AttendedCharacterSceneStatusProjector
        {
            ProjectorID = AttendedCharacterSceneStatusProjector.ProjectorIDValue,
        });
        mind.AddChild(new WatchSceneStatusProjector
        {
            ProjectorID = WatchSceneStatusProjector.ProjectorIDValue,
            Registry = watchRegistry,
        });
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player], content));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            CapturedFlowRequest replacement = clientProvider.Requests[^1];
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
                replacement.Messages.Select(static message => message.Role));
            IReadOnlyList<FunctionResultContent> results =
                [.. replacement.Messages[4].Contents.OfType<FunctionResultContent>()];
            Assert.Equal(["wait-call-1", "lore-call-2"], results.Select(static result => result.CallId));
            Assert.Contains("Wait ended:", results[0].Result?.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                "I walk them when I do not want to be found.",
                results[1].Result?.ToString(),
                StringComparison.Ordinal);
            Assert.Empty(mind.GetTimelineForTest());
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            clientProvider.Free();
            player.Free();
        }
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames = 300)
    {
        for (int frame = 0; frame < maxFrames && !predicate(); frame++)
        {
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private sealed class FlowCharacter(string id, params IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    /// <summary>Voice double whose cancellable submission completes immediately at playback hand-off.</summary>
    private sealed class ImmediateVoice : IVoice
    {
        public string Id { get; set; } = "flow-owner-voice";

        public string Type => "voice";

        public string FullId => $"voice:{Id}";

        public bool IsSpeaking
        {
            get;
            private set;
        }

        public List<string> Submissions { get; } = [];

        public event Action<IVoice>? SpeechStarted;

        public event Action<IVoice>? SpeechEnded;

        public Vector3 Origin => Vector3.Zero;

        public void Speak(string speech)
        {
        }

        public ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SpeakCancellableAsync(string speech, CancellationToken cancellationToken = default)
        {
            Submissions.Add(speech);
            IsSpeaking = true;
            SpeechStarted?.Invoke(this);
            IsSpeaking = false;
            SpeechEnded?.Invoke(this);
            return ValueTask.CompletedTask;
        }
    }

    private sealed partial class TestFlowMind(ICharacter owner) : AgenticMind
    {
        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>Immutable snapshot of one provider request: messages, instructions, and tool metadata.</summary>
    private sealed record CapturedFlowRequest(
        IReadOnlyList<ChatMessage> Messages,
        string Instructions,
        IReadOnlyDictionary<string, string> Tools);

    /// <summary>
    /// Scripted provider whose client records every request and serves enqueued steps; unscripted requests fail
    /// loudly so unexpected session activity surfaces in assertions.
    /// </summary>
    private sealed partial class ScriptedFlowClientProvider : ClientProvider
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();

        public List<CapturedFlowRequest> Requests { get; } = [];

        public void EnqueueCallBatch(params FunctionCallContent[] calls)
            => _steps.Enqueue(cancellationToken => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, calls))));

        public void EnqueueHoldForever()
            => _steps.Enqueue(async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                return new ChatResponse();
            });

        public override IChatClient CreateChatClient() => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedFlowClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                owner.Requests.Add(new CapturedFlowRequest(
                    [.. messages],
                    options?.Instructions ?? string.Empty,
                    options?.Tools?.OfType<AIFunction>().ToDictionary(
                        static function => function.Name,
                        static function => function.Description ?? string.Empty)
                    ?? []));
                cancellationToken.ThrowIfCancellationRequested();
                return owner._steps.Count == 0
                    ? throw new InvalidOperationException(
                        "The scripted session client received an unexpected request.")
                    : owner._steps.Dequeue()(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        }
    }
}
