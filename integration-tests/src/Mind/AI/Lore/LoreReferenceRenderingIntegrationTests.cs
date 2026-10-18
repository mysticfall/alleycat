using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.AI.Watch;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Source-to-completed-payload regression for authored internal lore references: the exact user-supplied syntax
/// <c>[Title](../relative_path_to_another_page)(`target_entry_id`)</c> and the on-disk Obsidian wikilink form
/// <c>[[wiki/world/slaves/Slaves|Slaves]](`world:slaves`)</c> must both reach the model as
/// <c>Title(`target_entry_id`)</c> and <c>Slaves(`world:slaves`)</c> — exact label plus backtick ID annotation, no
/// relative or vault path, no wiki-style pipe corruption — both in the automatic session-start lore injection and
/// in the <c>read_lore</c> result replayed into the next provider request, which shares the same cleaned entries
/// and formatter (AI-004 requirements 37–40 and 48). Fenced reference examples pass through verbatim.
/// </summary>
[Headless]
public sealed class LoreReferenceRenderingIntegrationTests
{
    private const string SharedStackPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string CurrentScenePromptPath = "res://assets/characters/prompts/current_scene.tres";
    private const string FencedExampleLine =
        "Fenced example: [Fenced Title](../fenced_example_path)(`fenced_entry_id`) and "
        + "[[../fenced_example_path|Fenced Title]](`fenced_entry_id`).";

    /// <summary>
    /// Authored internal references keep their label and backtick entry-ID annotation through loading, parse-time
    /// cleaning, formatting, automatic prompt injection, and read_lore retrieval, with no path or wiki-style
    /// corruption in either LLM-facing surface and verbatim passthrough inside fenced code blocks.
    /// </summary>
    [Fact]
    public async Task InternalReferences_KeepLabelAndIDAnnotation_InAutomaticPromptAndRetrievedContent()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        ReferenceFlowCharacter owner = new("test");
        FixturePlayerCharacter player = new();
        var content = new ContentContext("lore-reference-fixture", "res://tests/lore-reference-fixture");
        ScriptedFlowClientProvider clientProvider = new();
        clientProvider.EnqueueCallBatch(new FunctionCallContent(
            "reference-lore-call",
            "read_lore",
            new Dictionary<string, object?>
            {
                ["entry_ids"] = new object[] { "test.reference_rendering" },
            }));
        clientProvider.EnqueueHoldForever();
        ReferenceFlowMind mind = new(owner)
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
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([owner, player], content));
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);

        try
        {
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 1);

            // Automatic injection: the session-start system instruction carries the cleaned, formatted entry with
            // the reference rendered as label plus ID annotation and no source path.
            CapturedFlowRequest opening = clientProvider.Requests[0];
            Assert.Contains("# Reference Rendering", opening.Instructions, StringComparison.Ordinal);
            AssertReferenceRendering(opening.Instructions);

            // Retrieved content: the read_lore result replays into the next provider request through the same
            // cleaned entries and formatter, and the automatic injection is not rebuilt or transformed later.
            await WaitUntilAsync(sceneTree, () => clientProvider.Requests.Count >= 2);
            CapturedFlowRequest afterRetrieval = clientProvider.Requests[1];
            Assert.Equal(opening.Instructions, afterRetrieval.Instructions);
            FunctionResultContent loreResult = Assert.IsType<FunctionResultContent>(
                Assert.Single(afterRetrieval.Messages[^1].Contents.OfType<FunctionResultContent>()));
            Assert.Equal("reference-lore-call", loreResult.CallId);
            Assert.Contains("Entry 'test.reference_rendering':", loreResult.Result?.ToString(), StringComparison.Ordinal);
            AssertReferenceRendering(loreResult.Result?.ToString() ?? string.Empty);
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
    /// Asserts the fixed user criterion on one LLM-facing lore payload: both reference forms render as their exact
    /// label with the backtick ID annotation, no source path leaks, no wiki-style pipe corruption remains, and the
    /// fenced example passes through verbatim.
    /// </summary>
    private static void AssertReferenceRendering(string payload)
    {
        Assert.Contains("Supplied form: Title(`target_entry_id`)", payload, StringComparison.Ordinal);
        Assert.Contains("Vault form: Slaves(`world:slaves`)", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("../relative_path_to_another_page", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("wiki/world/slaves/Slaves", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("|Title]", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("|Slaves]", payload, StringComparison.Ordinal);
        Assert.Contains(FencedExampleLine, payload, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames = 300)
    {
        for (int frame = 0; frame < maxFrames && !predicate(); frame++)
        {
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private sealed class ReferenceFlowCharacter(string id) : ICharacter
    {
        public string Id { get; set; } = id;

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed partial class ReferenceFlowMind(ICharacter owner) : AgenticMind
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>Immutable snapshot of one provider request: messages and instructions.</summary>
    private sealed record CapturedFlowRequest(IReadOnlyList<ChatMessage> Messages, string Instructions);

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
                    options?.Instructions ?? string.Empty));
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
