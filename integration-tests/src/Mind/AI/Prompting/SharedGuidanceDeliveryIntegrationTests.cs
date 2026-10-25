using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot-runtime coverage proving the authored shared guidance and the corrected tool metadata actually reach the
/// model together with the context they describe: one captured provider request carries the rendered shared
/// instruction, a new player reply marked newly presented in the event timeline, fresh current-scene status, and
/// the corrected session-tool inventory (AI-002 TR-15/16; AI-003 TR-12–16; AI-010 TR-14/15) — and that the
/// delivered instruction itself teaches speech-addressee interpretation: reply opportunity versus overheard
/// context, and context inference for wording that makes no addressee claim (AI-003 UR-10, TR-13/TR-15).
/// </summary>
[Headless]
public sealed partial class SharedGuidanceDeliveryIntegrationTests
{
    private const string CurrentScenePromptPath = "res://assets/characters/prompts/current_scene.tres";

    /// <summary>
    /// A fresh player reply during held generation produces a replacement request whose options carry the rendered
    /// shared instruction and tool metadata, and whose messages lead with the canonical timeline message and
    /// current-scene status.
    /// </summary>
    [Fact]
    public async Task ProviderRequest_CarriesGuidanceNewReplySceneStatusAndCorrectedToolMetadata()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        TaskCompletionSource firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHold(firstRequestStarted);
        clientProvider.EnqueueHoldForever();
        WatchRegistry watchRegistry = new()
        {
            Conditions = [new ProximityWatchTool()],
        };
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack
            {
                Sections = [new FilePromptSection { FilePath = "res://prompts/mind.md", Name = "Instructions" }],
            },
            CurrentSceneStatus = Assert.IsType<PromptStack>(
                ResourceLoader.Load(CurrentScenePromptPath),
                exactMatch: false),
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
            // The attended player must survive the whole request cycle without decay-sensitive defaults.
            AttentionDecayPerSecond = 0f,
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
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        mind.ReinforceAttentionForTest(player.FullId);
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            mind.ObserveForTest(new ObservedSpeech(player.FullId, "I am heading to the market now.", ["char:owner"]));
            await WaitUntilAsync(sceneTree, () => clientProvider.Captures.Count == 2);
            CapturedRequest freshRequest = clientProvider.Captures[1];

            // The replacement request leads with the canonical timeline and scene messages (AI-002 TR-2/3).
            Assert.Equal(
                [ChatRole.User, ChatRole.User, ChatRole.User],
                freshRequest.Messages.Select(static message => message.Role));
            string timeline = freshRequest.Messages[0].Text;
            Assert.StartsWith("<Event History>", timeline, StringComparison.Ordinal);
            // The fresh player reply renders exactly once in the single section, marked newly presented on its
            // complete rendered line (AI-002 TR-3).
            Assert.Contains(
                "[new] Heard `char:fixture_player` say to me: I am heading to the market now.",
                timeline,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "\nHeard `char:fixture_player` say to me:",
                timeline,
                StringComparison.Ordinal);

            // Fresh current-scene status accompanies the timeline on the same request, stating its snapshot's
            // current game time unconditionally — including with an attended character present.
            Assert.Contains("Current attended characters:", freshRequest.Messages[1].Text, StringComparison.Ordinal);
            Assert.Contains("char:fixture_player", freshRequest.Messages[1].Text, StringComparison.Ordinal);
            Assert.Matches(
                new System.Text.RegularExpressions.Regex(@"Current game time: \d+\.\ds"),
                freshRequest.Messages[1].Text);

            // The authored shared guidance reaches the request fully rendered: non-empty, built from this mind's
            // character context, and free of unresolved template placeholders. Exact prose wording is game content
            // and stays tunable (AI-003 TR-12), so only delivery is asserted here.
            Assert.NotEmpty(freshRequest.Instructions);
            Assert.Contains(owner.FullId, freshRequest.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", freshRequest.Instructions, StringComparison.Ordinal);

            // The corrected tool metadata reaches the model on the same request.
            Assert.Equal(
                ["read_lore", "speak", "unwatch", "wait", "watch_proximity"],
                [.. freshRequest.Tools.Keys.OrderBy(static name => name, StringComparer.Ordinal)]);
            Assert.Contains("Lore Catalogue", freshRequest.Tools["read_lore"], StringComparison.Ordinal);
            Assert.Contains("not omniscient facts", freshRequest.Tools["read_lore"], StringComparison.Ordinal);
            Assert.Contains("arrives with every request", freshRequest.Tools["wait"], StringComparison.Ordinal);
            Assert.DoesNotContain("nothing new reaches you", freshRequest.Tools["wait"], StringComparison.Ordinal);
            Assert.Contains("never what was observed", freshRequest.Tools["wait"], StringComparison.Ordinal);
            Assert.Contains("opaque watch ID", freshRequest.Tools["watch_proximity"], StringComparison.Ordinal);
            Assert.Contains("never re-arm to keep it active", freshRequest.Tools["watch_proximity"], StringComparison.Ordinal);
            Assert.Contains(
                "ordinary events in your event history",
                freshRequest.Tools["watch_proximity"],
                StringComparison.Ordinal);
            Assert.Contains("opaque watch ID", freshRequest.Tools["unwatch"], StringComparison.Ordinal);
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
    /// The rendered shared instruction delivered on a real provider request teaches speech-addressee
    /// interpretation: the Event History guidance covers addressee wording — including entries that make no
    /// addressee claim and leave the addressee to be inferred from context — while the Choosing Actions guidance
    /// distinguishes speech addressed to the NPC as a reply opportunity from speech addressed to other characters
    /// as overheard context (AI-003 UR-10, TR-13/TR-15). Stable concept phrases are asserted rather than exact
    /// prose, which stays tunable (AI-003 TR-12).
    /// </summary>
    [Fact]
    public async Task ProviderRequest_InstructionTeachesSpeechAddresseeInterpretation()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CapturingSessionClientProvider clientProvider = new();
        clientProvider.EnqueueHoldForever();
        WatchRegistry watchRegistry = new()
        {
            Conditions = [new ProximityWatchTool()],
        };
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack
            {
                Sections = [new FilePromptSection { FilePath = "res://prompts/mind.md", Name = "Instructions" }],
            },
            CurrentSceneStatus = Assert.IsType<PromptStack>(
                ResourceLoader.Load(CurrentScenePromptPath),
                exactMatch: false),
            ClientProvider = clientProvider,
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
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player]));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Captures.Count == 1);

            // The startup request's instruction is the authored shared guidance exactly as the model receives it.
            string instructions = clientProvider.Captures[0].Instructions;

            // Event-history guidance covers addressee wording: entries may state whom the speaker addressed, and
            // wording that makes no addressee claim leaves the addressee to be inferred from context (TR-13).
            string eventHistory = AuthoredSection(instructions, "Event History");
            Assert.Contains("no addressee claim", eventHistory, StringComparison.Ordinal);
            Assert.Contains("infer from context", eventHistory, StringComparison.Ordinal);

            // Action-selection guidance distinguishes the two addressee outcomes: speech addressed to the NPC is
            // its reply opportunity, while speech addressed to other characters is overheard context between
            // others rather than itself a reason to respond (TR-15).
            string choosingActions = AuthoredSection(instructions, "Choosing Actions");
            Assert.Contains("addressed to you", choosingActions, StringComparison.Ordinal);
            Assert.Contains("reply opportunity", choosingActions, StringComparison.Ordinal);
            Assert.Contains("addressed to other characters", choosingActions, StringComparison.Ordinal);
            Assert.Contains("overheard context", choosingActions, StringComparison.Ordinal);
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

    /// <summary>Returns the authored markdown passage from the named top-level heading to the next one.</summary>
    private static string AuthoredSection(string source, string heading)
    {
        string marker = $"# {heading}";
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a '{marker}' heading in the rendered shared instruction.");
        int end = source.IndexOf("\n# ", start + marker.Length, StringComparison.Ordinal);

        return end < 0 ? source[start..] : source[start..end];
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "owner";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public void ReinforceAttentionForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>Immutable snapshot of one provider request: messages plus the model-facing options fields.</summary>
    private sealed record CapturedRequest(
        IReadOnlyList<ChatMessage> Messages,
        string Instructions,
        IReadOnlyDictionary<string, string> Tools);

    private sealed partial class CapturingSessionClientProvider : ClientProvider
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();

        public List<CapturedRequest> Captures { get; } = [];

        public void EnqueueHold(TaskCompletionSource started) => EnqueueHoldStep(started);

        public void EnqueueHoldForever() => EnqueueHoldStep(null);

        public override IChatClient CreateChatClient() => new CapturingClient(this);

        private void EnqueueHoldStep(TaskCompletionSource? started)
        {
            _steps.Enqueue(async cancellationToken =>
            {
                _ = (started?.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse();
            });
        }

        private sealed class CapturingClient(CapturingSessionClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                owner.Captures.Add(new CapturedRequest(
                    [.. messages],
                    options?.Instructions ?? string.Empty,
                    options?.Tools?.OfType<AIFunction>().ToDictionary(
                        static function => function.Name,
                        static function => function.Description ?? string.Empty)
                    ?? []));
                cancellationToken.ThrowIfCancellationRequested();
                return owner._steps.Count == 0
                    ? throw new InvalidOperationException(
                        "The capturing session client received an unexpected request.")
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
