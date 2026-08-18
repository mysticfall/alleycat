using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>Production-turn coverage for the single captured scene-context boundary.</summary>
public sealed partial class AgenticMindTurnContextIntegrationTests
{
    /// <summary>
    /// A foreground turn captures one fixed-membership snapshot and shares it through prompt construction,
    /// rendering, and trusted tool binding while retaining live references to its characters.
    /// </summary>
    [Fact]
    public async Task ForegroundTurn_CapturesOneSceneSnapshotAndSharesExactReferenceAcrossConsumers()
    {
        var owner = new TestCharacter("owner", "before");
        var newcomer = new TestCharacter("newcomer", "new");
        FixturePlayerCharacter player = new();
        List<ICharacter> liveMembership = [owner, player];
        var sceneProvider = new CountingSceneProvider(liveMembership);
        var section = new MutatingPromptSection(owner, newcomer, liveMembership) { Name = "Turn Context" };
        var tool = new CapturingTool();
        var clientProvider = new CapturingClientProvider();
        var mind = new TestAgenticMind(owner)
        {
            SystemInstruction = new PromptStack { Sections = [section] },
            ClientProvider = clientProvider,
            Tools = [tool],
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);

        try
        {
            await mind.RunForegroundTurnForTestAsync();

            SceneContext capturedScene = Assert.IsType<SceneContext>(section.CapturedScene);
            Assert.Equal(1, sceneProvider.CaptureCount);
            Assert.Same(capturedScene, owner.ReceivedScene);
            Assert.Same(capturedScene, tool.CapturedContext!.SceneContext);
            Assert.Same(owner, tool.CapturedContext.Character);
            Assert.Equal("after", owner.State);
            // Two-phase rendering builds the core context before prompt construction: the section's mid-compile state
            // mutation happens after the owner's context dictionary was captured, so the prompt renders the turn-start
            // state while the live character object still reflects the mutation.
            Assert.Contains("before", clientProvider.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("after", clientProvider.Instructions, StringComparison.Ordinal);

            Assert.Collection(
                capturedScene.Characters,
                character => Assert.Same(owner, character),
                character => Assert.Same(player, character));
            Assert.Same(owner, capturedScene.Find("char:owner"));
            Assert.Null(capturedScene.Find("char:newcomer"));
            Assert.Equal([owner, player, newcomer], liveMembership);
        }
        finally
        {
            mind.Free();
            section.Free();
            tool.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    private sealed class CountingSceneProvider(List<ICharacter> liveMembership)
    {
        public int CaptureCount
        {
            get; private set;
        }

        public ISceneContext GetCurrent()
        {
            CaptureCount++;
            return new SceneContext(liveMembership);
        }
    }

    private sealed partial class MutatingPromptSection(
        TestCharacter owner,
        ICharacter newcomer,
        List<ICharacter> liveMembership) : PromptSection
    {
        public ISceneContext? CapturedScene
        {
            get; private set;
        }

        public override Task<string> GetContentAsync(
            PromptSectionBuildContext buildContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedScene = buildContext.Scene;
            owner.State = "after";
            liveMembership.Add(newcomer);
            return Task.FromResult("Owner state: {{character.state}}");
        }
    }

    private sealed partial class CapturingTool : AgentTool
    {
        public CapturingTool()
        {
            ToolName = "capture_context";
            ToolDescription = "Capture the trusted turn context.";
        }

        public ScenarioContext? CapturedContext
        {
            get; private set;
        }

        protected override Delegate CreateDelegate() => Capture;

        private ValueTask<AgentToolResult> Capture(ScenarioContext context)
        {
            CapturedContext = context;
            return ValueTask.FromResult(new AgentToolResult());
        }
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public Task RunForegroundTurnForTestAsync()
            => RunAgentTurnAsync([], CancellationToken.None);

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed partial class TestCharacter(string id, string state) : ICharacter
    {
        public string Id { get; set; } = id;

        public string State { get; set; } = state;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public ISceneContext? ReceivedScene
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
        {
            ReceivedScene = scene;
            return new Dictionary<string, object?> { ["state"] = State };
        }
    }

    private sealed partial class CapturingClientProvider : ClientProvider
    {
        public string Instructions { get; private set; } = string.Empty;

        public override IChatClient CreateChatClient() => new CapturingClient(this);

        private sealed class CapturingClient(CapturingClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = messages;
                cancellationToken.ThrowIfCancellationRequested();
                owner.Instructions = options!.Instructions!;
                AIFunction productionTool = Assert.Single(options.Tools!.OfType<AIFunction>(),
                    function => !string.Equals(function.Name, "end_turn", StringComparison.Ordinal));
                ChatMessage response = new(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("capture-call", productionTool.Name, new Dictionary<string, object?>()),
                        new FunctionCallContent("end-call", "end_turn", new Dictionary<string, object?>()),
                    ]);
                return Task.FromResult(new ChatResponse(response));
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
