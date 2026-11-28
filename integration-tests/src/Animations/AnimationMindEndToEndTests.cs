using AlleyCat.Animations;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.IntegrationTests.Motion;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Mind.Perception;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// ANIM-003 Mind Integration end to end on the representative transition fixture: the installed reference female NPC
/// with real MotionBricks navigation and the fixture (<c>slump</c>) and extension (<c>stroll</c>) packs.
/// <list type="bullet">
/// <item>MIND-AC-001: the composed tools bind the NPC's own variant control, set preferences without awaiting a
/// transition, refuse unknown or incompatible ids in character, and carry no parking policy.</item>
/// <item>MIND-AC-003 (slice): a second, focused observer receives the description through the real transition-started
/// event.</item>
/// <item>MIND-AC-002: the static fragment and the own stance-and-gait status line render from the NPC's catalogue.</item>
/// </list>
/// </summary>
public sealed class AnimationMindEndToEndTests : IDisposable
{
    // Nothing here depends on generated motion: keep every MotionBricks node in this fixture native-free.
    private readonly NativeFreeMotion _nativeFreeMotion = NativeFreeMotion.Install(TestUtils.GetSceneTree());

    /// <inheritdoc />
    public void Dispose() => _nativeFreeMotion.Dispose();

    private const string CatalogueSectionPath = "res://assets/characters/prompts/animation_catalogue_section.tres";
    private const string StatusSectionPath = "res://assets/characters/prompts/own_stance_and_gait_status_section.tres";

    /// <summary>
    /// <c>change_stance("slump")</c> reaches the NPC's idle preference and returns before any transition starts; the
    /// bridge's transition-started event then reaches a focused observer as a description-only, event-eligible
    /// observation. Unknown and incompatible ids are refused with the preference unchanged.
    /// </summary>
    [Fact]
    public async Task ChangeStance_FocusedObserverReceivesTheDescription()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        var catalogue = AnimationCatalogue.RequireFor(harness.Actor);
        await using Observer observer = await Observer.StartAsync(harness);
        observer.Eyes.SetLookTarget(observer.SubjectCue);
        await observer.Mind.DrainPerceptionsForTestingAsync();
        Assert.True(observer.Perception.IsSubscribed);
        // First look at the standing NPC describes its displayed stance only (Observation Of Changes item 4).
        string actorId = ((ICharacter)harness.Actor).FullId;
        AnimationEntry sad = catalogue.Idle.Find("sad")!;
        Assert.Equal(AnimationCategory.Idle, harness.Navigation.DisplayedCategory);
        Assert.Equal(
            [(actorId, AnimationCategory.Idle, sad.Description)],
            observer.Mind.Timeline.OfType<ObservedAnimationChange>().Select(static c => (c.SubjectId, c.Category, c.Description)));

        IReadOnlyList<AnimationPreferenceTool> tools = AgenticMind.CreateAnimationPreferenceTools(harness.Actor);
        Assert.Equal(["change_stance", "change_gait"], tools.Select(static tool => tool.ToolName));
        Dictionary<string, AIFunction> functions = Compose(harness.Actor, tools);
        Assert.All(functions.Values, static function => Assert.IsNotType<ParkingBoundFunction>(function));
        Assert.All(tools, static tool => Assert.False(tool.ParksSessionByDefault));

        string? accepted = await InvokeAsync(functions["change_stance"], "slump");
        // Returned once the preference was accepted: the bridge has not started yet.
        Assert.Empty(harness.Transitions);
        Assert.Equal("slump", catalogue.Idle.Preference);
        Assert.Contains("`slump`", accepted, StringComparison.Ordinal);

        foreach (string refused in new[] { "dance", "feminine" })
        {
            string? message = await InvokeAsync(functions["change_stance"], refused);
            Assert.StartsWith("You cannot take up the stance", message, StringComparison.Ordinal);
            Assert.Equal("slump", catalogue.Idle.Preference);
        }

        Assert.StartsWith("You cannot move with the gait",
            await InvokeAsync(functions["change_gait"], "slump"), StringComparison.Ordinal);
        Assert.Contains("`stroll`", await InvokeAsync(functions["change_gait"], "stroll"), StringComparison.Ordinal);
        Assert.Equal("stroll", catalogue.Locomotion.Preference);

        await harness.WaitUntilAsync(() => harness.Transitions.Count > 0, 240, "slump bridge start");
        await observer.Mind.DrainPerceptionsForTestingAsync();

        AnimationEntry slump = catalogue.Idle.Find("slump")!;
        ObservedAnimationChange[] changes = [.. observer.Mind.Timeline.OfType<ObservedAnimationChange>()];
        Assert.Equal(2, changes.Length);
        ObservedAnimationChange change = changes[1];
        Assert.Equal((actorId, AnimationCategory.Idle, slump.Description),
            (change.SubjectId, change.Category, change.Description));
        string rendered = change.Render(observer.Character);
        Assert.Contains(slump.Description, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("`slump`", rendered, StringComparison.Ordinal);
        Assert.All(slump.Tags, tag => Assert.DoesNotContain(tag, rendered.Replace(slump.Description, "", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    /// <summary>
    /// The static fragment lists the NPC's compatible stances and gaits by id and tags under in-character headings,
    /// locomotion by variant, after the Lore Catalogue; the status line shows the NPC's own effective stance and gait as
    /// description and backticked id, and follows an accepted preference.
    /// </summary>
    [Fact]
    public async Task CatalogueFragmentAndStatusLine_RenderFromTheNpcCatalogue()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        CharacterSceneContext scene = new(harness.Actor);
        PromptSectionBuildContext buildContext = new(Game.Instance, scene, harness.Actor);
        var catalogue = AnimationCatalogue.RequireFor(harness.Actor);

        AnimationCataloguePromptSection section = ResourceLoader.Load<AnimationCataloguePromptSection>(CatalogueSectionPath);
        string source = await PseudoXmlPromptWriter.FormatAsync(
            [new TextPromptSection { Name = "Lore Catalogue", Text = "lore" }, section],
            buildContext);
        int lore = source.IndexOf("<Lore Catalogue>", StringComparison.Ordinal);
        int fragment = source.IndexOf("<Stances And Gaits>", StringComparison.Ordinal);
        Assert.True(lore >= 0 && fragment > lore, source);
        Assert.Contains(section.StanceHeading, source, StringComparison.Ordinal);
        Assert.Contains(section.GaitHeading, source, StringComparison.Ordinal);
        Assert.True(source.IndexOf(section.StanceHeading, StringComparison.Ordinal)
            < source.IndexOf(section.GaitHeading, StringComparison.Ordinal));
        foreach (AnimationEntry entry in catalogue.Idle.Entries.Concat(catalogue.Locomotion.Entries))
        {
            Assert.Contains($"- `{entry.ID}`: {string.Join(", ", entry.Tags)}", source, StringComparison.Ordinal);
            Assert.DoesNotContain(entry.Description, source, StringComparison.Ordinal);
            Assert.DoesNotContain(entry.AuthoredPath, source, StringComparison.Ordinal);
        }

        Assert.Contains("- `slump`:", source, StringComparison.Ordinal);
        Assert.Contains("- `stroll`:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("walk_forward", source, StringComparison.Ordinal);

        AgenticMind mind = Assert.Single(harness.Actor.GetChildren().OfType<AgenticMind>());
        var projectors = SceneStatusProjectorRegistry.Discover(mind);
        PromptStack status = new()
        {
            Sections = [ResourceLoader.Load<ProjectionPromptSection>(StatusSectionPath)]
        };
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ITemplateCompiler>(new FluidTemplateCompiler())
            .BuildServiceProvider();
        CompiledSceneStatusPrompt compiled = await status.CompileSceneStatusAsync(
            new PromptSectionBuildContext(services, scene, harness.Actor),
            projectors);

        string before = await compiled.RenderAsync(StatusContext(harness.Actor, scene));
        AssertStatus(before, catalogue.Idle.Find("sad")!, catalogue.Locomotion.Effective);

        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        string after = await compiled.RenderAsync(StatusContext(harness.Actor, scene));
        AssertStatus(after, catalogue.Idle.Find("slump")!, catalogue.Locomotion.Effective);
    }

    /// <summary>
    /// The production session composition (<c>CreateSessionTools</c>) offers <c>change_stance</c> and
    /// <c>change_gait</c>, without a parking policy, to a real NPC with an animation catalogue, beside the production
    /// inventory.
    /// </summary>
    [Fact]
    public async Task SessionComposition_OffersStanceAndGaitToolsForAnNpcWithACatalogue()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        // Never entered into the tree, so no session starts; only the composition boundary runs.
        CallSiteMind mind = new(harness.Actor);

        try
        {
            List<AITool> composed = mind.CreateSessionTools(
                new ScenarioContext(harness.Actor, new CharacterSceneContext(harness.Actor)),
                Game.Instance.GetRequiredService<IMainThreadDispatcher>(),
                new FixedClock(),
                new ToolAdmissionBroker(),
                VisualContextMode.Off,
                miniMapRenderer: null);
            var tools = composed.OfType<AIFunction>().ToDictionary(static function => function.Name);

            Assert.Equal(
                // AI-002 TR-13: the catalogue tools plus, for this navigation-capable NPC, the AI-015 navigation tools.
                ["change_gait", "change_stance", "read_lore", "speak", "stop", "turn_to", "wait", "walk_to"],
                [.. tools.Keys.OrderBy(static name => name, StringComparer.Ordinal)]);
            Assert.Equal(new ChangeStanceTool().ToolDescription, tools["change_stance"].Description);
            Assert.Equal(new ChangeGaitTool().ToolDescription, tools["change_gait"].Description);
            Assert.IsNotType<ParkingBoundFunction>(tools["change_stance"]);
            Assert.IsNotType<ParkingBoundFunction>(tools["change_gait"]);

            Assert.Contains("`slump`", (await tools["change_stance"].InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "slump" })))?.ToString(),
                StringComparison.Ordinal);
            Assert.Equal("slump", AnimationCatalogue.RequireFor(harness.Actor).Idle.Preference);
        }
        finally
        {
            mind.Free();
        }
    }

    private static void AssertStatus(string rendered, AnimationEntry stance, AnimationEntry gait)
    {
        Assert.Contains($"Your stance: {stance.Description} (`{stance.ID}`)", rendered, StringComparison.Ordinal);
        Assert.Contains($"Your gait: {gait.Description} (`{gait.ID}`)", rendered, StringComparison.Ordinal);
    }

    private static SceneStatusBuildContext StatusContext(ICharacter character, ISceneContext scene)
        => new(character, scene, new AttentionSnapshot(0d, new Dictionary<string, float>()), [], [], 0d);

    private static Dictionary<string, AIFunction> Compose(
        AlleyCat.Character.Character actor,
        IReadOnlyList<AnimationPreferenceTool> tools)
    {
        MindBase mind = Assert.Single(actor.GetChildren().OfType<MindBase>());
        ScenarioContext context = new(actor, new CharacterSceneContext(actor));
        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        return tools.ToDictionary(
            static tool => tool.ToolName,
            tool => AgenticMind.BindSessionParking(tool, tool.CreateFunction(context, mind, dispatcher)));
    }

    private static async Task<string?> InvokeAsync(AIFunction function, string id)
        => (await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = id })))?.ToString();

    private sealed partial class CallSiteMind(ICharacter owner) : AgenticMind
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed class FixedClock : IGameClock
    {
        public double NowSeconds => 0d;
    }

    private sealed class CharacterSceneContext(ICharacter character) : ISceneContext
    {
        public ICharacter Player => character;

        public IReadOnlyCollection<ICharacter> Characters => [character];

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => string.Equals(fullId, character.FullId, StringComparison.Ordinal) ? character : null;

        public IIdentifiable Resolve(string fullId) => Find(fullId) ?? throw new InvalidOperationException();
    }

    /// <summary>A second, minimal observer: Eyes, a Mind and the production perception with its default resolver.</summary>
    private sealed class Observer : IAsyncDisposable
    {
        private readonly Node3D _root = new();

        private Observer(NativeIdleTransitionHarness harness)
        {
            Eyes = new EyesBehaviour { VisualSurveyIntervalSeconds = 30d };
            Character = new ObserverCharacter(Eyes);
            Mind = new ObserverMind(Character);
            Perception = new AnimationChangePerception();
            Mind.AddChild(new VisualSurveyPerception());
            Mind.AddChild(Perception);
            Character.AddChild(Eyes);
            Character.AddChild(Mind);
            _root.AddChild(Character);
            SubjectCue = harness.Actor.VisualCues.FirstOrDefault() ?? AddCue(harness.Actor);
            Mind.SetSceneContextLoaderForTesting(() => new CharacterSceneContext(Character));
            harness.Scene.AddChild(_root);
        }

        public EyesBehaviour Eyes
        {
            get;
        }

        public ObserverCharacter Character
        {
            get;
        }

        public ObserverMind Mind
        {
            get;
        }

        public AnimationChangePerception Perception
        {
            get;
        }

        public VisualCue SubjectCue
        {
            get;
        }

        public static async Task<Observer> StartAsync(NativeIdleTransitionHarness harness)
        {
            Observer observer = new(harness);
            await TestUtils.WaitForFramesAsync(TestUtils.GetSceneTree(), 2);
            return observer;
        }

        public ValueTask DisposeAsync()
        {
            if (GodotObject.IsInstanceValid(_root))
            {
                _root.QueueFree();
            }

            return ValueTask.CompletedTask;
        }

        private static VisualCue AddCue(Node3D owner)
        {
            StaticVisualCue cue = new();
            owner.AddChild(cue);
            return cue;
        }
    }

    private sealed partial class ObserverCharacter(EyesBehaviour eyes) : Node3D, ICharacter
    {
        public string Id { get; set; } = "observer";

        public IReadOnlyList<IComponent> Components { get; } = [eyes];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];
    }

    private sealed partial class ObserverMind(ICharacter owner) : MindBase
    {
        public IReadOnlyList<AgentObservation> Timeline => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => owner;
    }
}
