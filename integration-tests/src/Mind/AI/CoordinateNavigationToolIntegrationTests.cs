using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Scene;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Navigation;
using AlleyCat.Scene;
using AlleyCat.Scene.MiniMap;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage of the AI-015 composition and delivery boundaries that need the real AgenticMind session
/// preparation and SCN-002 renderer: the coordinate navigation tools join the session inventory only for a
/// navigation-capable subject (TR-1), a started move's outcome reaches the subject's event timeline through Mind's
/// queue exactly once (TR-13–TR-14), and the AI minimap composition resolves a 1 m grid at the agent-navigation
/// framing (TR-17). The navigation is a facade double backed by NAV-001's production request lifecycle; the
/// real-walk coverage of TR-20 lives with the agent-navigation scene.
/// </summary>
public sealed class CoordinateNavigationToolIntegrationTests
{
    private static readonly string[] _navigationToolNames =
        [WalkToTool.ProductionToolName, TurnToTool.ProductionToolName, StopTool.ProductionToolName];

    /// <summary>
    /// A subject exposing navigation gets <c>walk_to</c>, <c>turn_to</c> and <c>stop</c> with their normative
    /// descriptions beside the unchanged production inventory; a subject without navigation gets none of them, so a
    /// model call to any is an unknown tool (AI-015 TR-1–TR-3, AI-002 TR-13).
    /// </summary>
    [Fact]
    public async Task Composition_AddsNavigationToolsOnlyForNavigationCapableSubjects()
    {
        await using var navigating = new SessionHarness(new LifecycleNavigationDouble());
        await using var stationary = new SessionHarness(navigation: null);

        AgenticMind.AgentSession navigatingSession = await navigating.Mind.RunSessionStartForTestAsync();
        AgenticMind.AgentSession stationarySession = await stationary.Mind.RunSessionStartForTestAsync();

        var navigatingTools = navigatingSession.Tools.ToDictionary(static tool => tool.Name);
        var stationaryTools = stationarySession.Tools.ToDictionary(static tool => tool.Name);
        Assert.Equal(WalkToTool.ProductionToolDescription, navigatingTools[WalkToTool.ProductionToolName].Description);
        Assert.Equal(TurnToTool.ProductionToolDescription, navigatingTools[TurnToTool.ProductionToolName].Description);
        Assert.Equal(StopTool.ProductionToolDescription, navigatingTools[StopTool.ProductionToolName].Description);
        Assert.NotNull(navigating.Mind.NavigationController);
        Assert.All(_navigationToolNames, name => Assert.DoesNotContain(name, stationaryTools.Keys));
        Assert.Null(stationary.Mind.NavigationController);
        foreach (string production in (string[])["speak", "wait", "read_lore"])
        {
            Assert.Contains(production, navigatingTools.Keys);
            Assert.Contains(production, stationaryTools.Keys);
        }
    }

    /// <summary>
    /// A model call to <c>walk_to</c> through the composed, runtime-bound function returns its start text at once
    /// and carries no observation; a replacing <c>turn_to</c> gives the walk one <c>replaced</c> outcome, and the
    /// turn's completion, polled by the mind's frame processing, gives one <c>arrived</c> outcome. Both reach the
    /// subject's persistent event timeline through Mind's queue, each exactly once, ending with the own-position
    /// sentence (AI-015 TR-10–TR-15).
    /// </summary>
    [Fact]
    public async Task Outcomes_ReachTheEventTimelineThroughMindsQueueExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        LifecycleNavigationDouble navigation = new()
        {
            RealisedTransform = new NavigationOutcomeTransform(1.04f, 0f, -2.26f, Mathf.Pi),
        };
        await using var harness = new SessionHarness(navigation);
        AgenticMind.AgentSession session = await harness.Mind.RunSessionStartForTestAsync();
        var tools = session.Tools.ToDictionary(static tool => tool.Name);

        object? walkResult = await ((AIFunction)tools[WalkToTool.ProductionToolName]).InvokeAsync(
            new AIFunctionArguments { ["x"] = 1, ["z"] = -2.25, ["pace"] = "hurry" });
        object? turnResult = await ((AIFunction)tools[TurnToTool.ProductionToolName]).InvokeAsync(
            new AIFunctionArguments { ["heading"] = 180 });
        Assert.StartsWith("Started walking to (1, -2.25) at hurry pace.", walkResult?.ToString(), StringComparison.Ordinal);
        Assert.Contains("This replaced your earlier move.", turnResult?.ToString(), StringComparison.Ordinal);

        Assert.True(navigation.CompleteActive());
        for (int frame = 0; frame < 3; frame++)
        {
            harness.Mind._Process(0d);
        }

        IReadOnlyList<ObservedNavigationOutcome> outcomes = [];
        for (int frame = 0; frame < 600 && outcomes.Count < 2; frame++)
        {
            await WaitForNextFrameAsync(sceneTree);
            outcomes = [.. harness.Mind.GetTimelineForTest().OfType<ObservedNavigationOutcome>()];
        }

        await WaitForFramesAsync(sceneTree, 10);
        outcomes = [.. harness.Mind.GetTimelineForTest().OfType<ObservedNavigationOutcome>()];
        Assert.Equal(
            [NavigationOutcomeKind.Replaced, NavigationOutcomeKind.Arrived],
            outcomes.Select(static outcome => outcome.Kind));
        Assert.Equal(
            "Your `turn_to` move arrived: you finished turning. You are at (1.0, -2.3), facing 180°.",
            outcomes[1].Render(harness.Owner));
        Assert.Empty(navigation.TerminalOutcomes);
    }

    /// <summary>
    /// The AI minimap composition, rendered by the real SCN-002 renderer over the agent-navigation layout from the
    /// subject's position, resolves a 1 m grid step; the renderer default spacing would give 2 m at the same
    /// framing, so the step is the override's effect (AI-015 TR-17, AI-013 TR-3).
    /// </summary>
    [Fact]
    public async Task AIMinimapComposition_ResolvesAOneMetreGridAtTheAgentNavigationFraming()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = CreateAgentNavigationLayout();
        await MiniMapTestHarness.AddToTreeAsync(sceneTree, root);
        try
        {
            MiniMapRenderer renderer = MiniMapTestHarness.CreateScopedRenderer(root);
            var centre = new Vector2(1f, 1.5f);
            MiniMapRenderSettings composed = VisualContextCapture.ComposeMinimapSettings("char:reference_female");

            MiniMapRenderResult result = renderer.Render(centre, composed);
            MiniMapRenderResult withDefaultSpacing = renderer.Render(
                centre,
                composed with
                {
                    GridMinimumSpacingPixels = null
                });

            Assert.Equal(MiniMapRenderStatus.Ready, result.Status);
            Assert.Equal(1f, result.GridStep);
            Assert.Equal(MiniMapRenderStatus.Ready, withDefaultSpacing.Status);
            Assert.Equal(2f, withDefaultSpacing.GridStep);
        }
        finally
        {
            await MiniMapTestHarness.RemoveFromTreeAsync(sceneTree, root);
        }
    }

    /// <summary>
    /// Marker layout of <c>res://tests/agent_navigation/agent_navigation.tscn</c>: landmarks, a wall, and both
    /// reference characters, matching the scene's footprint and therefore its minimap framing.
    /// </summary>
    private static Node CreateAgentNavigationLayout()
    {
        Node root = new()
        {
            Name = "AgentNavigationLayout"
        };
        root.AddChild(Box("Rug", new Vector3(-2.5f, 0, -1f), 1.6f, 1.2f, "#e01020"));
        root.AddChild(Box("Crate", new Vector3(4.5f, 0, -2f), 1f, 1f, "#ff8800"));
        root.AddChild(Box("Bench", new Vector3(-2.5f, 0, 4.5f), 1.6f, 0.5f, "#a05000"));
        root.AddChild(Box("Lamp", new Vector3(4.5f, 0, 4f), 0.5f, 0.5f, "#ffee00"));
        root.AddChild(Box("Wall", new Vector3(1f, 0, -1.2f), 3f, 0.3f, "#0000ff"));
        root.AddChild(Box("Companion", new Vector3(-1f, 0, -3.5f), 0.4f, 0.4f, "#1030e0"));
        root.AddChild(Box("Subject", new Vector3(1f, 0, 1.5f), 0.4f, 0.4f, "#ffd000"));
        return root;
    }

    private static MiniMapMarker Box(string name, Vector3 position, float width, float depth, string colour)
    {
        MiniMapMarker marker = MiniMapTestHarness.CreateMarker(
            name,
            1,
            new MiniMapRectangle
            {
                Name = "Shape",
                Width = width,
                Depth = depth,
                FillColour = new Color(colour),
            });
        marker.Position = position;
        return marker;
    }

    /// <summary>
    /// One AgenticMind prepared outside the tree for a plain subject character carrying the optional navigation
    /// component, with the fixture player in its scene context.
    /// </summary>
    private sealed class SessionHarness : IAsyncDisposable
    {
        private readonly FixturePlayerCharacter _player = new();
        private readonly HoldingClientProvider _provider = new();

        public SessionHarness(INavigation? navigation)
        {
            Owner = new ComponentCharacter(navigation is null ? [] : [navigation]);
            Mind = new TestAgenticMind(Owner)
            {
                SystemInstruction = new PromptStack
                {
                    Sections = [new TextPromptSection { Text = "static", Name = "Static" }]
                },
                ClientProvider = _provider,
            };
            Mind.SetSceneContextLoaderForTesting(() => new SceneContext([Owner, _player]));
        }

        public ComponentCharacter Owner
        {
            get;
        }

        public TestAgenticMind Mind
        {
            get;
        }

        public ValueTask DisposeAsync()
        {
            Mind.Free();
            _provider.Free();
            _player.Free();
            return ValueTask.CompletedTask;
        }
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public Task<AgentSession> RunSessionStartForTestAsync() => PrepareSessionAsync(CancellationToken.None);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed partial class HoldingClientProvider : ClientProvider
    {
        public override IChatClient CreateChatClient(string subjectFullId) => new HoldingClient();

        private sealed class HoldingClient : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }

    private sealed class ComponentCharacter(IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = "navigator";

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

}
