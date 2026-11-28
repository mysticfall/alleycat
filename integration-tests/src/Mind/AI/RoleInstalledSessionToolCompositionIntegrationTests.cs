using AlleyCat.Core;
using AlleyCat.IntegrationTests.Motion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Navigation;
using AlleyCat.Scene;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Regression coverage for AI-015 TR-1 on the production role-installation path: an <see cref="AgenticMind" />
/// installed from the reference NPC role template, whose character authors <see cref="MotionBricksNavigation" />,
/// composes <c>walk_to</c>, <c>turn_to</c> and <c>stop</c> into its session.
/// </summary>
/// <remarks>
/// The defect under test is the production installation order itself: the role installer adds the template's mind
/// (whose <c>_Ready</c> starts the session) before the runtime subsystem installer builds the character's component
/// projection. A minimal fixture that builds the projection first, or composes tools later, hides the symptom, so the
/// fixture loads the shipped reference-female NPC scene, which runs the real body through the production
/// <c>npc_installer.tscn</c> and the <c>reference_female_npc.tscn</c> role template. A runtime-assembled root cannot
/// stand in for it: the installer keeps only scene-authored target overrides such as the character identity. Only the mind's non-symptom dependencies are swapped as it enters the
/// tree — before its <c>_Ready</c> — so no network, prompt content, or visual capture is involved: a capturing client
/// provider, a static prompt, visual context off, and a fixture scene context.
/// </remarks>
public sealed class RoleInstalledSessionToolCompositionIntegrationTests : IDisposable
{
    // Nothing here depends on generated motion: keep every MotionBricks node in this fixture native-free.
    private readonly NativeFreeMotion _nativeFreeMotion = NativeFreeMotion.Install(GetSceneTree());

    /// <inheritdoc />
    public void Dispose() => _nativeFreeMotion.Dispose();

    /// <summary>
    /// The shipped reference-female NPC scene: the reference-female body, the production <c>npc_installer.tscn</c>, and
    /// a role template instancing <c>reference_female_npc.tscn</c> unchanged except for a visual-cue description.
    /// </summary>
    private const string ReferenceFemaleNpcScenePath = "res://assets/characters/reference/ally_npc.tscn";

    /// <summary>
    /// Hang guard for installation, session start and session end: ten seconds of simulated time at the default 60 Hz
    /// physics rate. It bounds no requirement; reaching it means the awaited step never happened.
    /// </summary>
    private const int SetupGuardPhysicsFrames = 600;

    /// <summary>
    /// The first request a role-installed reference NPC's session sends carries the coordinate navigation tools, as
    /// its character exposes <see cref="INavigation" /> once installation completes (AI-015 TR-1).
    /// </summary>
    [Fact]
    public async Task RoleInstalledReferenceNpc_FirstSessionRequestCarriesNavigationTools()
    {
        SceneTree tree = GetSceneTree();
        CapturingClientProvider provider = new();
        FixturePlayerCharacter player = new();
        CharacterHub npc = LoadPackedScene(ReferenceFemaleNpcScenePath).Instantiate<CharacterHub>();
        AgenticMind? mind = null;
        npc.ChildEnteredTree += child =>
        {
            if (child is AgenticMind entered && mind is null)
            {
                mind = entered;
                ConfigureHermeticSession(entered, provider, npc, player);
            }
        };

        try
        {
            tree.Root.AddChild(player);
            tree.Root.AddChild(npc);

            // Session start completes off the scene thread, so its guard counts physics frames, which advance with
            // simulated time; uncapped process frames would shrink the guard as rendering speeds up.
            for (int frame = 0; frame < SetupGuardPhysicsFrames && !(npc.HasComponentProjection && provider.FirstRequestTools.IsCompleted); frame++)
            {
                await WaitForNextPhysicsFrameAsync(tree);
            }

            // Setup guards: installation completed and the character does expose navigation, so a missing tool below
            // is the session-composition defect, not a broken fixture.
            Assert.NotNull(mind);
            Assert.True(npc.HasComponentProjection, "The role installer never built the character's component projection.");
            Assert.True(npc.TryGetComponent(out INavigation? navigation) && navigation is MotionBricksNavigation);
            Assert.True(provider.FirstRequestTools.IsCompleted, "The installed mind's session never sent its first request.");

            IReadOnlyList<string> tools = await provider.FirstRequestTools;
            Assert.All(
                (string[])[WalkToTool.ProductionToolName, TurnToTool.ProductionToolName, StopTool.ProductionToolName],
                name => Assert.Contains(name, tools));
        }
        finally
        {
            Task? worker = npc.GetChildren().OfType<MotionBricksNavigation>().SingleOrDefault()?.WorkerCompletion;
            npc.GetParent()?.RemoveChild(npc);
            for (int frame = 0; frame < SetupGuardPhysicsFrames && mind?.ActiveRunner is not null; frame++)
            {
                await WaitForNextPhysicsFrameAsync(tree);
            }

            npc.QueueFree();
            tree.Root.RemoveChild(player);
            player.QueueFree();
            await WaitForPhysicsFramesAsync(tree, 2);
            if (worker is not null)
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(30));
            }

            provider.Free();
        }
    }

    private static void ConfigureHermeticSession(
        AgenticMind mind,
        CapturingClientProvider provider,
        CharacterHub npc,
        FixturePlayerCharacter player)
    {
        mind.ClientProvider = provider;
        mind.SystemInstruction = new PromptStack
        {
            Sections = [new TextPromptSection { Text = "Static session guidance.", Name = "Static" }],
        };
        mind.CurrentSceneStatus = new PromptStack
        {
            Sections = [new TextPromptSection { Text = "Static scene status.", Name = "Status" }],
        };
        mind.ScenarioManager = null;
        mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
            EnableRequestResponseLogging: false,
            EnableReasoningLogging: false,
            EnableSessionTranscriptLogging: false));
        mind.SetVisualContextModeLoaderForTesting(() => VisualContextMode.Off);
        mind.SetSceneContextLoaderForTesting(() => new SceneContext([npc, player]));
    }

    /// <summary>
    /// Records the tool names of the session's first request, then holds every request until cancelled, so the session
    /// never progresses past its first composed inventory.
    /// </summary>
    private sealed partial class CapturingClientProvider : ClientProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _firstRequestTools =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<string>> FirstRequestTools => _firstRequestTools.Task;

        public override IChatClient CreateChatClient(string subjectFullId) => new CapturingClient(_firstRequestTools);

        private sealed class CapturingClient(TaskCompletionSource<IReadOnlyList<string>> firstRequestTools) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = firstRequestTools.TrySetResult([.. (options?.Tools ?? []).Select(static tool => tool.Name)]);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = firstRequestTools.TrySetResult([.. (options?.Tools ?? []).Select(static tool => tool.Name)]);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
