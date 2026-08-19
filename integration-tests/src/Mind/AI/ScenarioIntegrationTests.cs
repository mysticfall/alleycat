using System.Reflection;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Attention;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>Godot-runtime coverage for the scenario feature across session start, tools, and prompt assets.</summary>
[Headless]
public sealed partial class ScenarioIntegrationTests
{
    /// <summary>The fixed manager is an exportable GlobalClass resource returning its authored file's exact text.</summary>
    [Fact]
    public void FixedScenarioManager_ReturnsAuthoredFileContentExactlyOnEveryCall()
    {
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(FixedScenarioManager)));
        Assert.NotNull(typeof(FixedScenarioManager).GetCustomAttribute<GlobalClassAttribute>());
        Assert.True(typeof(ScenarioManager).IsAssignableFrom(typeof(FixedScenarioManager)));
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        SceneContext scene = new([owner, player]);
        FixedScenarioManager manager = new()
        {
            DescriptionPath = "res://assets/testing/prompts/test_scenario_fixed.md",
        };

        try
        {
            IReadOnlyDictionary<string, object?> coreContext = CreateCoreContextForManager(owner, scene);
            Scenario first = manager.GetCurrentScenario(coreContext)!;
            Scenario second = manager.GetCurrentScenario(coreContext)!;

            Assert.Equal("File-backed fixed scenario description for AI-008.\n", first.Description);
            Assert.Equal(first.Description, second.Description);
            Assert.NotSame(first, second);
        }
        finally
        {
            manager.Free();
            player.Free();
        }
    }

    /// <summary>The fixed manager strips a leading well-formed front-matter block and returns the exact body.</summary>
    [Fact]
    public void FixedScenarioManager_WithLeadingFrontmatter_ReturnsExactBodyOnEveryCall()
    {
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        SceneContext scene = new([owner, player]);
        FixedScenarioManager manager = new()
        {
            DescriptionPath = "res://assets/testing/prompts/test_scenario_with_frontmatter.md",
        };

        try
        {
            IReadOnlyDictionary<string, object?> coreContext = CreateCoreContextForManager(owner, scene);
            Scenario first = manager.GetCurrentScenario(coreContext)!;
            Scenario second = manager.GetCurrentScenario(coreContext)!;

            Assert.Equal("File-backed fixed scenario body after front matter for AI-008.\n", first.Description);
            Assert.Equal(first.Description, second.Description);
            Assert.DoesNotContain("---", first.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("title:", first.Description, StringComparison.Ordinal);
            Assert.NotSame(first, second);
        }
        finally
        {
            manager.Free();
            player.Free();
        }
    }

    /// <summary>The fixed manager renders player and character tokens in the authored body to their canonical FullIds.</summary>
    [Fact]
    public void FixedScenarioManager_WithTokenBody_RendersPlayerAndCharacterFullIds()
    {
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        SceneContext scene = new([owner, player]);
        FixedScenarioManager manager = new()
        {
            DescriptionPath = "res://assets/testing/prompts/test_scenario_token.md",
        };

        try
        {
            IReadOnlyDictionary<string, object?> coreContext = CreateCoreContextForManager(owner, scene);
            Scenario scenario = manager.GetCurrentScenario(coreContext)!;

            Assert.Equal(
                "The interrogator char:owner must extract the pass phrase from the detainee char:fixture_player before the shift changes.\n",
                scenario.Description);
            Assert.DoesNotContain("{{", scenario.Description, StringComparison.Ordinal);
        }
        finally
        {
            manager.Free();
            player.Free();
        }
    }

    /// <summary>The fixed manager rejects a missing path, missing file, and blank content.</summary>
    [Fact]
    public void FixedScenarioManager_WithBlankMissingOrEmptyAuthoring_FailsClearly()
    {
        TestCharacter owner = new();
        SceneContext scene = new([owner]);
        FixedScenarioManager manager = new();

        try
        {
            IReadOnlyDictionary<string, object?> coreContext = CreateMinimalCoreContext();

            InvalidOperationException missingPath = Assert.Throws<InvalidOperationException>(
                () => manager.GetCurrentScenario(coreContext));
            Assert.Contains("non-empty Godot resource path", missingPath.Message, StringComparison.Ordinal);

            manager.DescriptionPath = "res://assets/testing/prompts/missing_scenario_fixed.md";
            InvalidOperationException missingFile = Assert.Throws<InvalidOperationException>(
                () => manager.GetCurrentScenario(coreContext));
            Assert.Contains("res://assets/testing/prompts/missing_scenario_fixed.md", missingFile.Message, StringComparison.Ordinal);
            Assert.Contains("could not read scenario description file", missingFile.Message, StringComparison.Ordinal);

            manager.DescriptionPath = "res://assets/testing/prompts/test_scenario_blank.md";
            InvalidOperationException blankContent = Assert.Throws<InvalidOperationException>(
                () => manager.GetCurrentScenario(coreContext));
            Assert.Contains("res://assets/testing/prompts/test_scenario_blank.md", blankContent.Message, StringComparison.Ordinal);
            Assert.Contains("non-empty scenario description", blankContent.Message, StringComparison.Ordinal);

            manager.DescriptionPath = "res://assets/testing/prompts/test_scenario_frontmatter_only.md";
            InvalidOperationException blankAfterStrip = Assert.Throws<InvalidOperationException>(
                () => manager.GetCurrentScenario(coreContext));
            Assert.Contains(
                "res://assets/testing/prompts/test_scenario_frontmatter_only.md",
                blankAfterStrip.Message,
                StringComparison.Ordinal);
            Assert.Contains("non-empty scenario description", blankAfterStrip.Message, StringComparison.Ordinal);
        }
        finally
        {
            manager.Free();
        }
    }

    /// <summary>The fixed manager wraps template compilation or render failure clearly, naming the document path.</summary>
    [Fact]
    public void FixedScenarioManager_WithBrokenTemplateBody_FailsClearlyNamingThePath()
    {
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        SceneContext scene = new([owner, player]);
        FixedScenarioManager manager = new()
        {
            DescriptionPath = "res://assets/testing/prompts/test_scenario_broken_template.md",
        };

        try
        {
            IReadOnlyDictionary<string, object?> coreContext = CreateCoreContextForManager(owner, scene);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => manager.GetCurrentScenario(coreContext));

            Assert.Contains(
                "failed to compile or render the scenario description template",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "res://assets/testing/prompts/test_scenario_broken_template.md",
                exception.Message,
                StringComparison.Ordinal);
            Assert.NotNull(exception.InnerException);
        }
        finally
        {
            manager.Free();
            player.Free();
        }
    }

    /// <summary>Session start captures a fresh scene snapshot before querying the manager exactly once.</summary>
    [Fact]
    public async Task SessionStart_CapturesFreshSnapshotThenQueriesManagerOnceWithCoreContext()
    {
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CountingSceneProvider sceneProvider = new([owner, player]);
        ScriptedScenarioManager manager = new()
        {
            SceneCaptureCountProbe = () => sceneProvider.CaptureCount,
        };
        manager.Enqueue(new Scenario("Scenario one."));
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ScenarioManager = manager,
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);

        try
        {
            _ = await mind.RunSessionStartForTestAsync();

            Assert.Equal(1, sceneProvider.CaptureCount);
            Assert.Equal([1], manager.SceneCaptureCountsAtQuery);

            // The core context handed to the manager excludes the scenario key; keys were captured at call time
            // because the same mutable dictionary is sealed with the scenario key afterwards.
            Assert.All(
                manager.ReceivedCoreContextKeys,
                keys => Assert.DoesNotContain("scenario", keys));
            _ = Assert.Single(manager.ReceivedCoreContextKeys);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>The manager's returned record or null becomes the scenario of the one binding shared by render and tools.</summary>
    [Fact]
    public async Task Session_ManagerReturnValue_BecomesScenarioOfOneBindingForRenderAndTools()
    {
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CountingSceneProvider sceneProvider = new([owner, player]);
        ScriptedScenarioManager manager = new();
        manager.Enqueue(new Scenario("Scenario with a record."));
        ScriptedClientProvider clientProvider = new();
        clientProvider.ScriptCall(1, "capture_context");
        CapturingTool tool = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ScenarioManager = manager,
            Tools = [tool],
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);

        try
        {
            await mind.RunSessionForTestAsync(clientProvider.SessionCancellation.Token);

            IReadOnlyDictionary<string, object?> published = mind.GetLatestRenderContext();
            Scenario publishedScenario = Assert.IsType<Scenario>(published["scenario"]);
            Assert.Equal("Scenario with a record.", publishedScenario.Description);
            ScenarioContext captured = tool.CapturedContexts.Single();
            Assert.Same(publishedScenario, captured.Scenario);
            Assert.Same(sceneProvider.Captured[0], captured.SceneContext);
            Assert.Same(owner, captured.Character);
            _ = Assert.Single(manager.ReceivedCoreContextKeys);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
            tool.Free();
            player.Free();
        }
    }

    /// <summary>An unconfigured manager behaves exactly like a manager returning null.</summary>
    [Fact]
    public async Task UnconfiguredManager_RendersLikeANullReturningManager()
    {
        TestCharacter configuredOwner = new();
        TestCharacter unconfiguredOwner = new();
        FixturePlayerCharacter configuredPlayer = new();
        FixturePlayerCharacter unconfiguredPlayer = new();
        static PromptStack CreateStack()
        {
            return new()
            {
                Sections = [new FilePromptSection { FilePath = "res://prompts/scenario.md", Name = "Scenario" }],
            };
        }

        ScriptedScenarioManager nullReturningManager = new();
        ScriptedClientProvider configuredProvider = new();
        ScriptedClientProvider unconfiguredProvider = new();
        TestAgenticMind configuredMind = new(configuredOwner)
        {
            SystemInstruction = CreateStack(),
            ClientProvider = configuredProvider,
            ScenarioManager = nullReturningManager,
        };
        configuredMind.SetSceneContextLoaderForTesting(new CountingSceneProvider([configuredOwner, configuredPlayer]).GetCurrent);
        TestAgenticMind unconfiguredMind = new(unconfiguredOwner)
        {
            SystemInstruction = CreateStack(),
            ClientProvider = unconfiguredProvider,
        };
        unconfiguredMind.SetSceneContextLoaderForTesting(new CountingSceneProvider([unconfiguredOwner, unconfiguredPlayer]).GetCurrent);

        try
        {
            AgenticMind.AgentSession configuredSession = await configuredMind.RunSessionStartForTestAsync();
            AgenticMind.AgentSession unconfiguredSession = await unconfiguredMind.RunSessionStartForTestAsync();

            Assert.Null(configuredMind.GetLatestRenderContext()["scenario"]);
            Assert.Null(unconfiguredMind.GetLatestRenderContext()["scenario"]);
            Assert.Equal(unconfiguredSession.Instructions, configuredSession.Instructions);
            Assert.Contains("<Scenario>", configuredSession.Instructions, StringComparison.Ordinal);
            Assert.Contains("</Scenario>", configuredSession.Instructions, StringComparison.Ordinal);
        }
        finally
        {
            configuredMind.Free();
            unconfiguredMind.Free();
            nullReturningManager.Free();
            configuredProvider.Free();
            unconfiguredProvider.Free();
            configuredPlayer.Free();
            unconfiguredPlayer.Free();
        }
    }

    /// <summary>Manager failure is contained: the session never starts, the failure is logged, and the timeline is unaffected.</summary>
    [Fact]
    public async Task ManagerFailure_IsContainedWithoutSessionStartOrTimelineCorruption()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CountingSceneProvider sceneProvider = new([owner, player]);
        ScriptedScenarioManager manager = new();
        manager.EnqueueFailure(new InvalidOperationException("scenario resolution exploded"));
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ScenarioManager = manager,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            await WaitUntilAsync(
                sceneTree,
                () => loggerProvider.Entries.Any(entry =>
                    entry.Level == LogLevel.Error
                        && entry.Exception is InvalidOperationException
                        && entry.Exception.Message.Contains("scenario resolution exploded", StringComparison.Ordinal)));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.Empty(mind.GetLatestRenderContext());
            Assert.Empty(clientProvider.Instructions);
            _ = Assert.Single(manager.ReceivedCoreContextKeys);
            Assert.Equal(
                ["first"],
                mind.GetTimelineForTest().Cast<TestObservation>().Select(observation => observation.Value));
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            manager.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>The reserved player key aliases the player's identical rendered context from the characters dictionary.</summary>
    [Fact]
    public async Task SessionRender_WithAttendedPlayer_PlayerKeyAliasesPlayersCharactersEntry()
    {
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CountingSceneProvider sceneProvider = new([owner, player]);
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);
        mind.ReinforceAttentionForTest(player.FullId);

        try
        {
            _ = await mind.RunSessionStartForTestAsync();

            IReadOnlyDictionary<string, object?> published = mind.GetLatestRenderContext();
            IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                published["characters"]);

            Assert.Equal(["char:fixture_player", "char:owner"], characters.Keys);
            Assert.Same(characters[player.FullId], published["player"]);
            Assert.Same(player.Context, published["player"]);
            Assert.Equal(1, player.ContextRequestCount);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>
    /// A player outside attention eligibility still renders <c>{{player.FullId}}</c> from the unconditional core
    /// player context dictionary.
    /// </summary>
    /// <remarks>
    /// Regression coverage for the empty-token defect: raw <c>ICharacter</c> objects never expose the default
    /// interface <c>FullId</c> member to Handlebars, so the player must arrive as its rendered context dictionary.
    /// </remarks>
    [Fact]
    public async Task SessionRender_WhenPlayerIsNotAttentionEligible_PlayerFullIdTokenStillRenders()
    {
        PromptStack stack = LoadSharedStackAndAssertScenarioSection();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CountingSceneProvider sceneProvider = new([owner, player]);
        FixedScenarioManager manager = new()
        {
            DescriptionPath = "res://assets/testing/prompts/test_scenario_token.md",
        };
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = stack,
            ClientProvider = clientProvider,
            ScenarioManager = manager,
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);

        try
        {
            string instructions = (await mind.RunSessionStartForTestAsync()).Instructions;
            Assert.Contains("char:owner", instructions, StringComparison.Ordinal);
            Assert.Contains("char:fixture_player", instructions, StringComparison.Ordinal);
            int openingIndex = instructions.IndexOf("<Scenario>", StringComparison.Ordinal);
            int closingIndex = instructions.IndexOf("</Scenario>", StringComparison.Ordinal);
            Assert.True(openingIndex >= 0, "Expected the Scenario tag pair in the rendered prompt.");
            Assert.True(closingIndex > openingIndex, "Expected a closed Scenario tag pair in the rendered prompt.");
            string sectionContent = instructions[(openingIndex + "<Scenario>".Length)..closingIndex];
            Assert.DoesNotContain("{{", sectionContent, StringComparison.Ordinal);

            IReadOnlyDictionary<string, object?> published = mind.GetLatestRenderContext();
            Assert.Same(player.Context, published["player"]);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>Attention gating omits the player from characters while the player key stays populated.</summary>
    [Fact]
    public async Task SessionRender_WhenPlayerIsNotAttentionEligible_CharactersOmitPlayerWhilePlayerKeyIsPopulated()
    {
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        CountingSceneProvider sceneProvider = new([owner, player]);
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);

        try
        {
            _ = await mind.RunSessionStartForTestAsync();

            IReadOnlyDictionary<string, object?> published = mind.GetLatestRenderContext();
            IReadOnlyDictionary<string, object?> characters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                published["characters"]);

            Assert.Equal(["char:owner"], characters.Keys);
            Assert.Same(player.Context, published["player"]);
            Assert.Equal(1, player.ContextRequestCount);
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>The shared generic NPC prompt stack renders the scenario through a plain file-backed guarded section.</summary>
    [Fact]
    public async Task SharedPromptStack_WithConfiguredManager_RendersScenarioDescription()
    {
        PromptStack stack = LoadSharedStackAndAssertScenarioSection();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        FixedScenarioManager manager = new()
        {
            DescriptionPath = "res://assets/testing/prompts/test_scenario_fixed.md",
        };
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = stack,
            ClientProvider = clientProvider,
            ScenarioManager = manager,
        };
        mind.SetSceneContextLoaderForTesting(new CountingSceneProvider([owner, player]).GetCurrent);

        try
        {
            string instructions = (await mind.RunSessionStartForTestAsync()).Instructions;
            Assert.Contains("<Scenario>", instructions, StringComparison.Ordinal);
            Assert.Contains("File-backed fixed scenario description for AI-008.", instructions, StringComparison.Ordinal);
            Assert.Contains("</Scenario>", instructions, StringComparison.Ordinal);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>The shared-stack render path with a token fixture yields instructions containing both FullIds.</summary>
    [Fact]
    public async Task SharedPromptStack_WithTokenScenario_RendersSubstitutedFullIdsInInstructions()
    {
        PromptStack stack = LoadSharedStackAndAssertScenarioSection();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        FixedScenarioManager manager = new()
        {
            DescriptionPath = "res://assets/testing/prompts/test_scenario_token.md",
        };
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = stack,
            ClientProvider = clientProvider,
            ScenarioManager = manager,
        };
        mind.SetSceneContextLoaderForTesting(new CountingSceneProvider([owner, player]).GetCurrent);

        try
        {
            string instructions = (await mind.RunSessionStartForTestAsync()).Instructions;
            Assert.Contains("char:owner", instructions, StringComparison.Ordinal);
            Assert.Contains("char:fixture_player", instructions, StringComparison.Ordinal);
            int openingIndex = instructions.IndexOf("<Scenario>", StringComparison.Ordinal);
            int closingIndex = instructions.IndexOf("</Scenario>", StringComparison.Ordinal);
            Assert.True(openingIndex >= 0, "Expected the Scenario tag pair in the rendered prompt.");
            Assert.True(closingIndex > openingIndex, "Expected a closed Scenario tag pair in the rendered prompt.");
            string sectionContent = instructions[(openingIndex + "<Scenario>".Length)..closingIndex];
            Assert.DoesNotContain("{{", sectionContent, StringComparison.Ordinal);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    /// <summary>A null scenario renders the empty guarded section inside its tag pair without new section machinery.</summary>
    [Fact]
    public async Task SharedPromptStack_WithoutScenario_RendersEmptyGuardedSectionInsideTagPair()
    {
        PromptStack stack = LoadSharedStackAndAssertScenarioSection();
        TestCharacter owner = new();
        FixturePlayerCharacter player = new();
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = stack,
            ClientProvider = clientProvider,
        };
        mind.SetSceneContextLoaderForTesting(new CountingSceneProvider([owner, player]).GetCurrent);

        try
        {
            string instructions = (await mind.RunSessionStartForTestAsync()).Instructions;
            int openingIndex = instructions.IndexOf("<Scenario>", StringComparison.Ordinal);
            int closingIndex = instructions.IndexOf("</Scenario>", StringComparison.Ordinal);
            Assert.True(openingIndex >= 0, "Expected the empty Scenario tag pair quirk in the rendered prompt.");
            Assert.True(closingIndex > openingIndex, "Expected a closed Scenario tag pair in the rendered prompt.");
            string sectionContent = instructions[(openingIndex + "<Scenario>".Length)..closingIndex];
            Assert.Equal(
                string.Empty,
                sectionContent.Trim());
        }
        finally
        {
            mind.Free();
            clientProvider.Free();
            player.Free();
        }
    }

    private static PromptStack LoadSharedStackAndAssertScenarioSection()
    {
        PromptStack stack = ResourceLoader.Load<PromptStack>(
            "res://assets/characters/prompts/generic_npc_prompt_stack.tres");
        Assert.NotNull(stack);

        // The shared generic NPC prompt stack carries exactly the mind.md file section, essential lore, character
        // lore, and the scenario section — no event-history section (AI-003 TR-23/24).
        Assert.Equal(
            ["Instructions", "Lore", "Characters", "Scenario"],
            stack.Sections.Select(section => section.Name));
        Assert.DoesNotContain(
            stack.Sections,
            section => section.GetType().Name.Contains("EventHistory", StringComparison.Ordinal));
        PromptSection scenarioSection = Assert.IsType<FilePromptSection>(stack.Sections[3]);
        var fileSection = (FilePromptSection)scenarioSection;
        Assert.Equal("res://prompts/scenario.md", fileSection.FilePath);
        Assert.Equal("Scenario", fileSection.Name);

        using var scenarioFile = Godot.FileAccess.Open(
            "res://prompts/scenario.md",
            Godot.FileAccess.ModeFlags.Read);
        Assert.NotNull(scenarioFile);
        string template = scenarioFile.GetAsText();
        Assert.Contains("{{#if scenario}}", template, StringComparison.Ordinal);
        Assert.Contains("{{scenario.Description}}", template, StringComparison.Ordinal);

        Assert.Null(typeof(PromptSection).GetProperty("IsEnabled"));
        Assert.Null(typeof(FilePromptSection).GetProperty("IsEnabled"));
        return stack;
    }

    /// <summary>Builds a production-shaped core render context through the real AgenticMind core path.</summary>
    private static IReadOnlyDictionary<string, object?> CreateCoreContextForManager(ICharacter owner, SceneContext scene)
        => AgenticMind.CreateCoreRenderContext(owner, scene, attentionEligibleFullIDs: null);

    /// <summary>Minimal realistic core dictionary for failure-path calls that never read the context.</summary>
    private static IReadOnlyDictionary<string, object?> CreateMinimalCoreContext()
        => new Dictionary<string, object?>
        {
            ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:owner" },
            ["player"] = new Dictionary<string, object?> { ["FullId"] = "char:fixture_player" },
        };

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames = 120)
    {
        for (int frame = 0; frame < maxFrames && !predicate(); frame++)
        {
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    private sealed class CountingSceneProvider(IReadOnlyCollection<ICharacter> liveMembership)
    {
        private readonly List<SceneContext> _captured = [];

        public IReadOnlyList<SceneContext> Captured => _captured;

        public int CaptureCount => _captured.Count;

        public SceneContext GetCurrent()
        {
            SceneContext scene = new(liveMembership);
            _captured.Add(scene);
            return scene;
        }
    }

    private sealed partial class ScriptedScenarioManager : ScenarioManager
    {
        private readonly Queue<object?> _script = new();

        public List<IReadOnlyDictionary<string, object?>> ReceivedCoreContexts { get; } = [];

        /// <summary>
        /// Core-context keys captured at call time. The core dictionary is later sealed with the scenario key in
        /// place, so post-session reads of the aliased instances would otherwise see the added key.
        /// </summary>
        public List<string[]> ReceivedCoreContextKeys { get; } = [];

        public List<int> SceneCaptureCountsAtQuery { get; } = [];

        public Func<int> SceneCaptureCountProbe { get; set; } = static () => 0;

        public void Enqueue(Scenario? scenario) => _script.Enqueue(scenario);

        public void EnqueueFailure(Exception exception) => _script.Enqueue(exception);

        public override Scenario? GetCurrentScenario(IReadOnlyDictionary<string, object?> coreContext)
        {
            ArgumentNullException.ThrowIfNull(coreContext);
            ReceivedCoreContexts.Add(coreContext);
            ReceivedCoreContextKeys.Add([.. coreContext.Keys]);
            SceneCaptureCountsAtQuery.Add(SceneCaptureCountProbe());
            if (_script.Count == 0)
            {
                return null;
            }

            object? next = _script.Dequeue();
            return next is Exception exception ? throw exception : (Scenario?)next;
        }
    }

    private sealed partial class CapturingTool : AgentTool
    {
        public CapturingTool()
        {
            ToolName = "capture_context";
            ToolDescription = "Capture the trusted session context.";
        }

        public List<ScenarioContext> CapturedContexts { get; } = [];

        protected override Delegate CreateDelegate() => Capture;

        private ValueTask<AgentToolResult> Capture(ScenarioContext context)
        {
            CapturedContexts.Add(context);
            return ValueTask.FromResult(new AgentToolResult());
        }
    }

    private sealed partial class ScriptedClientProvider : ClientProvider
    {
        private readonly Dictionary<int, string[]> _scriptedCalls = [];

        public List<string> Instructions { get; } = [];

        public CancellationTokenSource SessionCancellation { get; } = new();

        public int CallCount => Instructions.Count;

        public void ScriptCall(int call, params string[] toolNames) => _scriptedCalls[call] = toolNames;

        public override IChatClient CreateChatClient() => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = messages;
                int call = owner.Instructions.Count + 1;
                owner.Instructions.Add(options!.Instructions!);
                if (!owner._scriptedCalls.ContainsKey(call))
                {
                    // The session is long-running: once the script is exhausted, node-lifetime-style cancellation
                    // ends it quietly.
                    owner.SessionCancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(cancellationToken);
                }

                FunctionCallContent[] calls =
                [
                    .. owner._scriptedCalls[call].Select((name, index) => new FunctionCallContent(
                        $"call-{call}-{index}",
                        name,
                        new Dictionary<string, object?>())),
                ];
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));
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

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public void ObserveForTest(AgentObservation observation) => Observe(observation);

        public void ReinforceAttentionForTest(string fullId)
            => ReinforceAttention(fullId, 1f, AttentionSettings.Create(1f, 0f, 0.05f, 0.25f));

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

        /// <summary>Runs only the session-start sequence: render context, manager query, prompt render, publication.</summary>
        public Task<AgentSession> RunSessionStartForTestAsync()
            => PrepareSessionAsync(CancellationToken.None);

        /// <summary>Runs the complete session through the production prepare and execute paths.</summary>
        public async Task RunSessionForTestAsync(CancellationToken cancellationToken)
        {
            AgentSession session = await PrepareSessionAsync(CancellationToken.None);
            await ExecuteSessionAsync(session, cancellationToken);
        }

        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    private sealed record TestObservation(float Importance, string Value) : AgentObservation
    {
        public override string TypeKey => "test.scenario";

        public override float CalculateImportance(ObservationContext context) => Importance;
    }

    private sealed class TestCharacter : ICharacter
    {
        public string Id { get; set; } = "owner";

        public string FullId => $"char:{Id}";

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        // Production-shaped context: the canonical identity is a string entry mirroring CharacterCardContextSource,
        // so dictionary-based Handlebars access ({{character.FullId}}) resolves in tests exactly as in production.
        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>
            {
                ["name"] = $"Character {Id}",
                ["FullId"] = FullId,
            };
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<LogEntry> _entries = [];
        private bool _disposed;

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose() => _disposed = true;

        private void Record(LogLevel level, string message, Exception? exception)
        {
            if (_disposed)
            {
                return;
            }

            lock (_lock)
            {
                _entries.Add(new LogEntry(level, message, exception));
            }
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _ = eventId;
                provider.Record(logLevel, formatter(state, exception), exception);
            }
        }

        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
    }
}
