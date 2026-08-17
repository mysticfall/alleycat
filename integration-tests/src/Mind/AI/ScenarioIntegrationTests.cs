using System.Reflection;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
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

/// <summary>Godot-runtime coverage for the scenario feature across turns, tools, workers, and prompt assets.</summary>
[Headless]
public sealed partial class ScenarioIntegrationTests
{
    /// <summary>The fixed manager is an exportable GlobalClass resource returning exactly its authored text.</summary>
    [Fact]
    public void FixedScenarioManager_ReturnsExactlyAuthoredTextOnEveryCall()
    {
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(FixedScenarioManager)));
        Assert.NotNull(typeof(FixedScenarioManager).GetCustomAttribute<GlobalClassAttribute>());
        Assert.True(typeof(ScenarioManager).IsAssignableFrom(typeof(FixedScenarioManager)));

        TestCharacter owner = new();
        SceneContext scene = new([owner]);
        ScenarioContext previous = new(owner, scene);
        FixedScenarioManager manager = new()
        {
            Description = "Guard the market stall until dusk."
        };

        try
        {
            Scenario first = manager.GetCurrentScenario(previous)!;
            Scenario second = manager.GetCurrentScenario(previous)!;

            Assert.Equal("Guard the market stall until dusk.", first.Description);
            Assert.Equal("Guard the market stall until dusk.", second.Description);
            Assert.NotSame(first, second);
        }
        finally
        {
            manager.Free();
        }
    }

    /// <summary>The fixed manager rejects a blank authored description and a null previous binding clearly.</summary>
    [Fact]
    public void FixedScenarioManager_WithBlankDescriptionOrNullPrevious_FailsClearly()
    {
        TestCharacter owner = new();
        SceneContext scene = new([owner]);
        FixedScenarioManager manager = new();

        try
        {
            Assert.Equal(
                "previous",
                Assert.Throws<ArgumentNullException>(() => manager.GetCurrentScenario(null!)).ParamName);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => manager.GetCurrentScenario(new ScenarioContext(owner, scene)));
            Assert.Contains("non-empty authored description", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            manager.Free();
        }
    }

    /// <summary>Each fresh turn captures a new scene snapshot before the manager query and hands over the previous binding.</summary>
    [Fact]
    public async Task ForegroundTurn_CapturesFreshSnapshotThenQueriesManagerWithLazilyCreatedPrevious()
    {
        TestCharacter owner = new();
        List<ICharacter> liveMembership = [owner];
        CountingSceneProvider sceneProvider = new(liveMembership);
        ScriptedScenarioManager manager = new()
        {
            SceneCaptureCountProbe = () => sceneProvider.CaptureCount,
        };
        manager.Enqueue(new Scenario("Scenario one."));
        ScriptedClientProvider clientProvider = new();
        clientProvider.ScriptCall(1, "capture_context", "end_turn");
        clientProvider.ScriptCall(2, "capture_context", "end_turn");
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
            await mind.RunForegroundTurnForTestAsync();
            await mind.RunForegroundTurnForTestAsync();

            Assert.Equal(2, sceneProvider.CaptureCount);
            Assert.Equal([1, 2], manager.SceneCaptureCountsAtQuery);

            ScenarioContext firstPrevious = manager.ReceivedPrevious[0];
            Assert.Null(firstPrevious.Scenario);
            Assert.Same(owner, firstPrevious.Character);
            Assert.Same(sceneProvider.Captured[0], firstPrevious.SceneContext);

            Assert.Equal(2, manager.ReceivedPrevious.Count);
            Assert.Same(tool.CapturedContexts[0], manager.ReceivedPrevious[1]);
            Assert.Equal("Scenario one.", manager.ReceivedPrevious[1].Scenario!.Description);
            Assert.Same(sceneProvider.Captured[0], manager.ReceivedPrevious[1].SceneContext);
            Assert.NotSame(tool.CapturedContexts[0], tool.CapturedContexts[1]);
            Assert.Equal([owner], liveMembership);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
            tool.Free();
        }
    }

    /// <summary>The manager's returned record or null becomes the scenario of the one binding shared by render and tools.</summary>
    [Fact]
    public async Task ForegroundTurn_ManagerReturnValue_BecomesScenarioOfOneBindingForRenderAndTools()
    {
        TestCharacter owner = new();
        CountingSceneProvider sceneProvider = new([owner]);
        ScriptedScenarioManager manager = new();
        manager.Enqueue(new Scenario("Scenario with a record."));
        manager.Enqueue(null);
        ScriptedClientProvider clientProvider = new();
        clientProvider.ScriptCall(1, "capture_context", "end_turn");
        clientProvider.ScriptCall(2, "capture_context", "end_turn");
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
            await mind.RunForegroundTurnForTestAsync();

            IReadOnlyDictionary<string, object?> firstPublished = mind.GetLatestRenderContext();
            Scenario firstScenario = Assert.IsType<Scenario>(firstPublished["scenario"]);
            Assert.Equal("Scenario with a record.", firstScenario.Description);
            Assert.Same(firstScenario, tool.CapturedContexts.Single().Scenario);
            Assert.Same(sceneProvider.Captured[0], tool.CapturedContexts.Single().SceneContext);
            Assert.Same(owner, tool.CapturedContexts.Single().Character);

            await mind.RunForegroundTurnForTestAsync();

            IReadOnlyDictionary<string, object?> secondPublished = mind.GetLatestRenderContext();
            Assert.Null(secondPublished["scenario"]);
            Assert.Null(tool.CapturedContexts[1].Scenario);
            Assert.Equal(["character", "characters", "observations", "scenario"], secondPublished.Keys);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
            tool.Free();
        }
    }

    /// <summary>An unconfigured manager behaves exactly like a manager returning null.</summary>
    [Fact]
    public async Task UnconfiguredManager_RendersLikeANullReturningManager()
    {
        TestCharacter configuredOwner = new();
        TestCharacter unconfiguredOwner = new();
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
        configuredMind.SetSceneContextLoaderForTesting(new CountingSceneProvider([configuredOwner]).GetCurrent);
        TestAgenticMind unconfiguredMind = new(unconfiguredOwner)
        {
            SystemInstruction = CreateStack(),
            ClientProvider = unconfiguredProvider,
        };
        unconfiguredMind.SetSceneContextLoaderForTesting(new CountingSceneProvider([unconfiguredOwner]).GetCurrent);

        try
        {
            await configuredMind.RunForegroundTurnForTestAsync();
            await unconfiguredMind.RunForegroundTurnForTestAsync();

            Assert.Null(configuredMind.GetLatestRenderContext()["scenario"]);
            Assert.Null(unconfiguredMind.GetLatestRenderContext()["scenario"]);
            Assert.Equal(unconfiguredProvider.Instructions.Single(), configuredProvider.Instructions.Single());
            Assert.Contains("<Scenario>", configuredProvider.Instructions.Single(), StringComparison.Ordinal);
            Assert.Contains("</Scenario>", configuredProvider.Instructions.Single(), StringComparison.Ordinal);
        }
        finally
        {
            configuredMind.Free();
            unconfiguredMind.Free();
            nullReturningManager.Free();
            configuredProvider.Free();
            unconfiguredProvider.Free();
        }
    }

    /// <summary>A replacement turn after interruption reuses the just-built binding without re-querying the manager.</summary>
    [Fact]
    public async Task ReplacementTurnAfterInterruption_ReusesScenarioContextWithoutRequery()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        CountingSceneProvider sceneProvider = new([owner]);
        ScriptedScenarioManager manager = new();
        manager.Enqueue(new Scenario("Interrupted scenario."));
        ScriptedClientProvider clientProvider = new()
        {
            GateAtCall = 2
        };
        clientProvider.ScriptCall(1, "capture_context");
        clientProvider.ScriptCall(3, "capture_context", "end_turn");
        CapturingTool tool = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ScenarioManager = manager,
            Tools = [tool],
            HighImportanceInterruptionEnabled = true,
            HighImportanceInterruptionThreshold = 5f,
            ObservationImportanceThreshold = 1f,
        };
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "initial"));
            await WaitUntilAsync(
                sceneTree,
                () => clientProvider.CallCount == 2 && tool.CapturedContexts.Count == 1);

            mind.ObserveForTest(new TestObservation(5f, "high"));
            await clientProvider.GateCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(sceneTree, () => clientProvider.CallCount == 3);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            _ = Assert.Single(manager.ReceivedPrevious);
            Assert.Equal(2, tool.CapturedContexts.Count);
            Assert.Same(tool.CapturedContexts[0], tool.CapturedContexts[1]);
            Assert.Equal("Interrupted scenario.", tool.CapturedContexts[1].Scenario!.Description);
            Assert.Same(
                tool.CapturedContexts[1].SceneContext,
                tool.CapturedContexts[0].SceneContext);
            IReadOnlyDictionary<string, object?> published = mind.GetLatestRenderContext();
            Assert.Equal("Interrupted scenario.", Assert.IsType<Scenario>(published["scenario"]).Description);
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            manager.Free();
            clientProvider.Free();
            tool.Free();
        }
    }

    /// <summary>Manager failure is contained: the prior published snapshot is retained without retry or repair.</summary>
    [Fact]
    public async Task ManagerFailure_IsContainedRetainingPriorSnapshotWithoutRetry()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        CountingSceneProvider sceneProvider = new([owner]);
        ScriptedScenarioManager manager = new();
        manager.Enqueue(new Scenario("First scenario."));
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
        int successfulTurns = 0;
        mind.ForegroundTurnSucceeded += () => successfulTurns++;
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "first"));
            await WaitUntilAsync(sceneTree, () => mind.GetLatestRenderContext().Count > 0);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            IReadOnlyDictionary<string, object?> priorSnapshot = mind.GetLatestRenderContext();

            manager.EnqueueFailure(new InvalidOperationException("scenario resolution exploded"));
            mind.ObserveForTest(new TestObservation(1f, "second"));
            await WaitUntilAsync(
                sceneTree,
                () => loggerProvider.Entries.Any(entry =>
                    entry.Level == LogLevel.Error
                        && entry.Exception is InvalidOperationException
                        && entry.Exception.Message.Contains("scenario resolution exploded", StringComparison.Ordinal)));
            await TestUtils.WaitForFramesAsync(sceneTree, 4);

            Assert.Same(priorSnapshot, mind.GetLatestRenderContext());
            Assert.Equal("First scenario.", Assert.IsType<Scenario>(priorSnapshot["scenario"]).Description);
            Assert.Equal(2, manager.ReceivedPrevious.Count);
            Assert.Equal(1, clientProvider.CallCount);
            Assert.Equal(1, successfulTurns);
            Assert.False(mind.HasPendingForTest);
            Assert.Equal(
                ["first", "second"],
                mind.GetTimelineForTest().Cast<TestObservation>().Select(observation => observation.Value));
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            manager.Free();
            clientProvider.Free();
        }
    }

    /// <summary>An authored worker projection colliding with the reserved scenario key fails the foreground render.</summary>
    [Fact]
    public async Task WorkerProjection_CollidingWithScenarioKey_FailsForegroundRenderAndRetainsSnapshot()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        using RecordingLoggerProvider loggerProvider = new();
        Game.Instance.GetRequiredService<ILoggerFactory>().AddProvider(loggerProvider);
        TestCharacter owner = new();
        CountingSceneProvider sceneProvider = new([owner]);
        CollidingProjectionWorker worker = new();
        ManualRequestTrigger trigger = new();
        worker.AddChild(trigger);
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ObservationImportanceThreshold = 1f,
        };
        mind.AddChild(worker);
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "start"));
            await WaitUntilAsync(sceneTree, () => mind.GetLatestRenderContext().Count > 0);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            IReadOnlyDictionary<string, object?> priorSnapshot = mind.GetLatestRenderContext();

            trigger.RequestForTest();
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1);
            mind.ObserveForTest(new TestObservation(1f, "collision"));
            await WaitUntilAsync(
                sceneTree,
                () => loggerProvider.Entries.Any(entry =>
                    entry.Exception?.Message.Contains("duplicate context key 'scenario'", StringComparison.Ordinal) == true));
            await TestUtils.WaitForFramesAsync(sceneTree, 2);

            Assert.Same(priorSnapshot, mind.GetLatestRenderContext());
            Assert.Equal(1, clientProvider.CallCount);
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            clientProvider.Free();
        }
    }

    /// <summary>Context workers capture the scenario key through ordinary published-snapshot capture.</summary>
    [Fact]
    public async Task ContextWorkers_CaptureScenarioKeyThroughOrdinarySnapshotCapture()
    {
        SceneTree sceneTree = TestUtils.GetSceneTree();
        TestCharacter owner = new();
        CountingSceneProvider sceneProvider = new([owner]);
        ScriptedScenarioManager manager = new();
        Scenario scenario = new("Worker visible scenario.");
        manager.Enqueue(scenario);
        RecordingWorker worker = new();
        ManualRequestTrigger trigger = new();
        worker.AddChild(trigger);
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = new PromptStack { Sections = [new TextPromptSection { Text = "static", Name = "Static" }] },
            ClientProvider = clientProvider,
            ScenarioManager = manager,
            ObservationImportanceThreshold = 1f,
        };
        mind.AddChild(worker);
        mind.SetSceneContextLoaderForTesting(sceneProvider.GetCurrent);
        (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(mind);
        await TestUtils.WaitForFramesAsync(sceneTree, 2);

        try
        {
            mind.ObserveForTest(new TestObservation(1f, "start"));
            IReadOnlyDictionary<string, object?> published = mind.GetLatestRenderContext();
            await WaitUntilAsync(
                sceneTree,
                () =>
                {
                    published = mind.GetLatestRenderContext();
                    return published.Count > 0 && ReferenceEquals(published["scenario"], scenario);
                });

            trigger.RequestForTest();
            await WaitUntilAsync(sceneTree, () => worker.RunCount == 1);

            IReadOnlyDictionary<string, object?> captured = worker.Contexts.Single();
            Assert.Same(published, captured);
            Assert.Same(scenario, captured["scenario"]);
        }
        finally
        {
            mind.QueueFree();
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            manager.Free();
            clientProvider.Free();
        }
    }

    /// <summary>The shared generic NPC prompt stack renders the scenario through a plain file-backed guarded section.</summary>
    [Fact]
    public async Task SharedPromptStack_WithConfiguredManager_RendersScenarioDescription()
    {
        PromptStack stack = LoadSharedStackAndAssertScenarioSection();
        TestCharacter owner = new();
        FixedScenarioManager manager = new()
        {
            Description = "Win the alley cooking contest."
        };
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = stack,
            ClientProvider = clientProvider,
            ScenarioManager = manager,
        };
        mind.SetSceneContextLoaderForTesting(new CountingSceneProvider([owner]).GetCurrent);

        try
        {
            await mind.RunForegroundTurnForTestAsync();

            string instructions = clientProvider.Instructions.Single();
            Assert.Contains("<Scenario>", instructions, StringComparison.Ordinal);
            Assert.Contains("Win the alley cooking contest.", instructions, StringComparison.Ordinal);
            Assert.Contains("</Scenario>", instructions, StringComparison.Ordinal);
        }
        finally
        {
            mind.Free();
            manager.Free();
            clientProvider.Free();
        }
    }

    /// <summary>A null scenario renders the empty guarded section inside its tag pair without new section machinery.</summary>
    [Fact]
    public async Task SharedPromptStack_WithoutScenario_RendersEmptyGuardedSectionInsideTagPair()
    {
        PromptStack stack = LoadSharedStackAndAssertScenarioSection();
        TestCharacter owner = new();
        ScriptedClientProvider clientProvider = new();
        TestAgenticMind mind = new(owner)
        {
            SystemInstruction = stack,
            ClientProvider = clientProvider,
        };
        mind.SetSceneContextLoaderForTesting(new CountingSceneProvider([owner]).GetCurrent);

        try
        {
            await mind.RunForegroundTurnForTestAsync();

            string instructions = clientProvider.Instructions.Single();
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
        }
    }

    private static PromptStack LoadSharedStackAndAssertScenarioSection()
    {
        PromptStack stack = ResourceLoader.Load<PromptStack>(
            "res://assets/characters/prompts/generic_npc_prompt_stack.tres");
        Assert.NotNull(stack);

        PromptSection scenarioSection = Assert.IsType<FilePromptSection>(stack.Sections[3]);
        var fileSection = (FilePromptSection)scenarioSection;
        Assert.Equal("res://prompts/scenario.md", fileSection.FilePath);
        Assert.Equal("Scenario", fileSection.Name);
        Assert.Equal("Instructions", stack.Sections[0].Name);
        Assert.Equal("Event History", stack.Sections[4].Name);

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

        public List<ScenarioContext> ReceivedPrevious { get; } = [];

        public List<int> SceneCaptureCountsAtQuery { get; } = [];

        public Func<int> SceneCaptureCountProbe { get; set; } = static () => 0;

        public void Enqueue(Scenario? scenario) => _script.Enqueue(scenario);

        public void EnqueueFailure(Exception exception) => _script.Enqueue(exception);

        public override Scenario? GetCurrentScenario(ScenarioContext previous)
        {
            ArgumentNullException.ThrowIfNull(previous);
            ReceivedPrevious.Add(previous);
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
            ToolDescription = "Capture the trusted turn context.";
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
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, string[]> _scriptedCalls = [];

        public List<string> Instructions { get; } = [];

        public TaskCompletionSource GateCancellationObserved
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GateAtCall
        {
            get; init;
        }

        public int CallCount => Instructions.Count;

        public void ScriptCall(int call, params string[] toolNames) => _scriptedCalls[call] = toolNames;

        public override IChatClient CreateChatClient() => new ScriptedClient(this);

        private sealed class ScriptedClient(ScriptedClientProvider owner) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _ = messages;
                int call = owner.Instructions.Count + 1;
                owner.Instructions.Add(options!.Instructions!);
                if (call == owner.GateAtCall)
                {
                    try
                    {
                        await owner._gate.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _ = owner.GateCancellationObserved.TrySetResult();
                        throw;
                    }
                }

                string[] toolNames = owner._scriptedCalls.GetValueOrDefault(call, ["end_turn"]);
                FunctionCallContent[] calls =
                [
                    .. toolNames.Select((name, index) => new FunctionCallContent(
                        $"call-{call}-{index}",
                        name,
                        new Dictionary<string, object?>())),
                ];
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, calls));
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

    private sealed partial class CollidingProjectionWorker : ContextWorker
    {
        public int RunCount
        {
            get; private set;
        }

        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["scenario"] = "colliding projection",
            });
        }
    }

    private sealed partial class RecordingWorker : ContextWorker
    {
        public List<IReadOnlyDictionary<string, object?>> Contexts { get; } = [];

        public int RunCount
        {
            get; private set;
        }

        protected override Task<IReadOnlyDictionary<string, object?>> RunAsync(
            IReadOnlyDictionary<string, object?> context,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Contexts.Add(context);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed partial class ManualRequestTrigger : ContextWorkerTrigger
    {
        public void RequestForTest() => RequestRun();
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        public bool HasPendingForTest => HasPendingObservations;

        public Task RunForegroundTurnForTestAsync() => RunAgentTurnAsync([], CancellationToken.None);

        public void ObserveForTest(AgentObservation observation) => _ = Observe(observation);

        public IReadOnlyList<AgentObservation> GetTimelineForTest() => GetObservationTimelineSnapshot();

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

        public IReadOnlyList<IComponent> Components { get; } = [];

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?> { ["name"] = $"Character {Id}" };
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
