using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>Session coverage for fixed tool bindings and fresh request-scene context boundaries.</summary>
public sealed partial class AgenticMindTurnContextIntegrationTests
{
    /// <summary>
    /// A session captures one fixed-membership snapshot for static prompt construction and trusted tool binding,
    /// while status validation and every logical request receive distinct fresh scene snapshots.
    /// </summary>
    [Fact]
    public async Task Session_KeepsToolBindingFixedWhileRequestSceneSnapshotsRemainFresh()
    {
        var owner = new TestCharacter("owner", "before");
        var newcomer = new TestCharacter("newcomer", "new");
        FixturePlayerCharacter player = new();
        List<ICharacter> liveMembership = [owner, player];
        var sceneProvider = new CountingSceneProvider(liveMembership);
        var section = new MutatingPromptSection(owner, newcomer, liveMembership) { Name = "Session Context" };
        var tool = new CapturingTool();
        var clientProvider = new CancellingClientProvider();
        var mind = new TestAgenticMind(owner)
        {
            SystemInstruction = new PromptStack { Sections = [section] },
            ClientProvider = clientProvider,
            Tools = [tool],
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);

        try
        {
            await mind.RunSessionForTestAsync(clientProvider.SessionCancellation.Token);

            SceneContext capturedScene = Assert.IsType<SceneContext>(section.CapturedScene);
            // The first capture remains fixed for static prompt construction and trusted tools. One validation
            // snapshot and one snapshot per logical request must remain fresh and independent (AI-002 TR-4; AI-003
            // TR-6).
            Assert.Equal(4, sceneProvider.CaptureCount);
            Assert.Same(capturedScene, sceneProvider.Captured[0]);
            Assert.All(sceneProvider.Captured.Skip(1), snapshot => Assert.NotSame(capturedScene, snapshot));
            Assert.All(
                sceneProvider.Captured.Skip(1),
                snapshot => Assert.Contains(newcomer, snapshot.Characters));
            Assert.Same(capturedScene, tool.CapturedContext!.SceneContext);
            Assert.Same(owner, tool.CapturedContext.Character);
            Assert.Equal("after", owner.State);
            // Two-phase rendering builds the core render context before prompt construction: the section's
            // mid-compile mutation happens after the owner's curated view entered the context, so the prompt renders
            // the canonical identity while the live character object already reflects the state mutation.
            Assert.Contains("Owner: char:owner", clientProvider.Instructions, StringComparison.Ordinal);

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
        public List<SceneContext> Captured { get; } = [];

        public int CaptureCount
        {
            get; private set;
        }

        public ISceneContext GetCurrent()
        {
            CaptureCount++;
            var captured = new SceneContext(liveMembership);
            Captured.Add(captured);
            return captured;
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
            return Task.FromResult("Owner: {{character.FullId}}");
        }
    }

    private sealed partial class CapturingTool : AgentTool
    {
        public CapturingTool()
        {
            ToolName = "capture_context";
            ToolDescription = "Capture the trusted session context.";
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
        /// <summary>Runs the complete session through the production prepare and execute paths.</summary>
        public async Task RunSessionForTestAsync(CancellationToken cancellationToken)
        {
            AgentSession session = await PrepareSessionAsync(CancellationToken.None);
            await ExecuteSessionAsync(session, cancellationToken);
        }

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed class TestCharacter(string id, string state) : ICharacter
    {
        public string Id { get; set; } = id;

        public string State { get; set; } = state;

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    private sealed partial class CancellingClientProvider : ClientProvider
    {
        public CancellationTokenSource SessionCancellation { get; } = new();

        public string Instructions { get; private set; } = string.Empty;

        public int CallCount
        {
            get; private set;
        }

        public override IChatClient CreateChatClient() => new CapturingClient(this);

        private sealed class CapturingClient(CancellingClientProvider owner) : IChatClient
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
                    function => string.Equals(function.Name, "capture_context", StringComparison.Ordinal));
                owner.CallCount++;
                if (owner.CallCount > 1)
                {
                    // The session is long-running: once the capturing tool ran and its result was replayed, ending
                    // the scripted session lets the test observe the one captured binding.
                    owner.SessionCancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(cancellationToken);
                }

                ChatMessage response = new(
                    ChatRole.Assistant,
                    [new FunctionCallContent("capture-call", productionTool.Name, new Dictionary<string, object?>())]);
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
