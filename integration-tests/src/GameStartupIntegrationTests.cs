using System.Reflection;
using AlleyCat.Core;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.Testing;
using AlleyCat.UI;
using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests;

/// <summary>
/// Runtime integration coverage for startup orchestration branches in <see cref="Game"/>.
/// </summary>
public sealed partial class GameStartupIntegrationTests
{
    private const string EditorRunStartupBypassScenePath = "res://tests/startup/startup_bypass_runtime_test.tscn";
    private const string GlobalScenePath = "res://assets/scenes/global.tscn";
    private const string FallbackStartScene = "res://assets/scenes/empty.tscn";

    private static readonly FieldInfo _splashScreenField = typeof(Game)
        .GetField("_splashScreen", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected Game._splashScreen field for startup-orchestration tests.");

    private static readonly FieldInfo _loadingScreenField = typeof(Game)
        .GetField("_loadingScreen", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected Game._loadingScreen field for startup-orchestration tests.");

    private static readonly MethodInfo _runStartupFlowAsyncMethod = typeof(Game)
        .GetMethod("RunStartupFlowAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected Game.RunStartupFlowAsync method for startup-orchestration tests.");

    private static readonly MethodInfo _onXRInitialisedMethod = typeof(Game)
        .GetMethod("OnXRInitialised", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected Game.OnXRInitialised method for startup-orchestration tests.");

    /// <summary>
    /// Verifies startup requests an error-path quit when XR initialisation reports failure.
    /// </summary>
    [Fact]
    public async Task StartupFlow_WhenXRInitialisationFails_RequestsErrorQuit()
    {
        SceneTree sceneTree = GetSceneTree();
        StartupFixture fixture = await CreateStartupFixtureAsync(sceneTree);

        try
        {
            AssignStartupFields(fixture.Game, fixture.SplashScreen, fixture.LoadingScreen);
            SignalXrInitialisationResult(fixture.Game, succeeded: false);

            Task startupTask = InvokeRunStartupFlowAsync(fixture.Game);
            await WaitForNextFrameAsync(sceneTree);
            fixture.SplashScreen.EmitSplashFinished();
            await startupTask;

            Assert.Equal([1], fixture.Game.QuitRequests);
            Assert.Equal(0, fixture.LoadingScreen.LoadSceneAsyncCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies startup requests an error-path quit when start-scene loading fails immediately.
    /// </summary>
    [Fact]
    public async Task StartupFlow_WhenStartSceneLoadRequestFails_RequestsErrorQuit()
    {
        SceneTree sceneTree = GetSceneTree();
        StartupFixture fixture = await CreateStartupFixtureAsync(sceneTree);

        try
        {
            fixture.LoadingScreen.NextLoadSceneResult = Error.FileNotFound;
            AssignStartupFields(fixture.Game, fixture.SplashScreen, fixture.LoadingScreen);
            SignalXrInitialisationResult(fixture.Game, succeeded: true);

            Task startupTask = InvokeRunStartupFlowAsync(fixture.Game);
            await WaitForNextFrameAsync(sceneTree);
            fixture.SplashScreen.EmitSplashFinished();
            await startupTask;

            Assert.Equal([1], fixture.Game.QuitRequests);
            Assert.Equal(1, fixture.LoadingScreen.LoadSceneAsyncCallCount);
            Assert.Equal(FallbackStartScene, fixture.LoadingScreen.LastRequestedScenePath);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies startup keeps the loading screen visible until load completion is signalled.
    /// </summary>
    [Fact]
    public async Task StartupFlow_OnSuccessfulStartup_KeepsLoadingVisibleUntilCompletion_RemovesSplash_AndRequestsStartSceneLoad()
    {
        SceneTree sceneTree = GetSceneTree();
        StartupFixture fixture = await CreateStartupFixtureAsync(sceneTree);

        try
        {
            fixture.LoadingScreen.NextLoadSceneResult = Error.Ok;
            AssignStartupFields(fixture.Game, fixture.SplashScreen, fixture.LoadingScreen);
            SignalXrInitialisationResult(fixture.Game, succeeded: true);

            Task startupTask = InvokeRunStartupFlowAsync(fixture.Game);
            await WaitForNextFrameAsync(sceneTree);
            fixture.SplashScreen.EmitSplashFinished();
            await startupTask;

            Assert.Empty(fixture.Game.QuitRequests);
            Assert.Equal(1, fixture.LoadingScreen.LoadSceneAsyncCallCount);
            Assert.Equal(FallbackStartScene, fixture.LoadingScreen.LastRequestedScenePath);
            Assert.True(fixture.LoadingScreen.Visible);
            Assert.Null(fixture.SplashScreen.GetParent());

            await WaitForFramesAsync(sceneTree, 3);
            Assert.False(GodotObject.IsInstanceValid(fixture.SplashScreen));
            Assert.True(fixture.LoadingScreen.Visible);

            fixture.LoadingScreen.EmitLoadCompleted();
            await WaitForNextFrameAsync(sceneTree);
            Assert.False(fixture.LoadingScreen.Visible);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies startup continues without waiting on splash when splash is absent (skip-splash path).
    /// </summary>
    [Fact]
    public async Task StartupFlow_WhenSplashIsAbsent_LoadsStartSceneAfterSuccessfulXRInitialisation()
    {
        SceneTree sceneTree = GetSceneTree();
        StartupFixture fixture = await CreateStartupFixtureAsync(sceneTree);

        try
        {
            fixture.LoadingScreen.NextLoadSceneResult = Error.Ok;
            AssignStartupFields(fixture.Game, splashScreen: null, loadingScreen: fixture.LoadingScreen);
            SignalXrInitialisationResult(fixture.Game, succeeded: true);

            Task startupTask = InvokeRunStartupFlowAsync(fixture.Game);
            await startupTask;

            Assert.Empty(fixture.Game.QuitRequests);
            Assert.Equal(1, fixture.LoadingScreen.LoadSceneAsyncCallCount);
            Assert.Equal(FallbackStartScene, fixture.LoadingScreen.LastRequestedScenePath);
            Assert.True(fixture.LoadingScreen.Visible);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies integration-runtime bypass keeps startup orchestration disabled in <see cref="Game._Ready"/>.
    /// </summary>
    [Fact]
    public async Task GameReady_InIntegrationRuntime_DoesNotRunStartupOrchestration()
    {
        SceneTree sceneTree = GetSceneTree();
        StartupFixture fixture = await CreateStartupFixtureAsync(sceneTree);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Empty(fixture.Game.QuitRequests);
            Assert.Equal(0, fixture.LoadingScreen.LoadSceneAsyncCallCount);
            Assert.NotNull(fixture.SplashScreen.GetParent());
            Assert.NotNull(fixture.LoadingScreen.GetParent());
            Assert.False(fixture.LoadingScreen.Visible);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies editor-run <c>res://tests/...</c> scenes bypass both Game and XR startup on the live scene tree.
    /// </summary>
    [Fact]
    public async Task EditorRunTestScene_WhenLoadedFromTestsPath_BypassesGameAndXRStartup()
    {
        SceneTree sceneTree = GetSceneTree();
        Node game = LoadPackedScene(EditorRunStartupBypassScenePath).Instantiate();

        try
        {
            sceneTree.Root.AddChild(game);
            await WaitForFramesAsync(sceneTree, 2);

            Node xrManager = game.GetNode<Node>("XR");
            SubViewport uiRoot = game.GetNode<SubViewport>("XR/SubViewport");
            CanvasItem loadingScreen = game.GetNode<CanvasItem>("XR/SubViewport/LoadingScreen");

            Assert.Equal(EditorRunStartupBypassScenePath, game.SceneFilePath);
            Assert.True(RuntimeContext.ShouldBypassGlobalStartup(sceneTree));
            Assert.False(ReadBooleanProperty(xrManager, "InitialisationAttempted"));
            Assert.False(ReadBooleanProperty(xrManager, "InitialisationSucceeded"));
            Assert.Null(ReadPropertyValue(xrManager, "Runtime"));
            Assert.Equal(1, xrManager.GetChildCount());
            Assert.Equal(1, uiRoot.GetChildCount());
            Assert.False(loadingScreen.Visible);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(game) && game.IsInsideTree())
            {
                game.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    /// <summary>
    /// Verifies the global game node exposes the scene-owned XR manager through service resolution.
    /// </summary>
    [Fact]
    public async Task GameEnterTree_RegistersSceneXRManagerAsGlobalService()
    {
        SceneTree sceneTree = GetSceneTree();
        StartupFixture fixture = await CreateStartupFixtureAsync(sceneTree);

        try
        {
            Assert.Same(fixture.Game, Game.Instance);
            _ = Assert.IsAssignableFrom<IServiceProvider>(fixture.Game);

            XRManager resolvedXRManager = Game.Instance.GetRequiredService<XRManager>();
            ISceneContextProvider sceneContextProvider = Game.Instance.GetRequiredService<ISceneContextProvider>();

            Assert.Same(fixture.XRManager, resolvedXRManager);
            Assert.Same(fixture.XRManager, Game.Instance.GetService<XRManager>());
            _ = Assert.IsType<SceneContextProvider>(sceneContextProvider);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies service registrar discovery is recursive, tree-ordered, and not coupled to an XR child node name.
    /// </summary>
    [Fact]
    public async Task GameEnterTree_DiscoversSceneServiceRegistrarsRecursively_AndResolvesRenamedXRManager()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "RegistrarDiscoveryFixture",
        };

        TestServiceRegistrar firstRegistrar = new("first")
        {
            Name = "FirstRegistrar",
        };
        Node serviceContainer = new()
        {
            Name = "SceneServices",
        };
        TestXRManager renamedXRManager = new()
        {
            Name = "PlayerRuntimeService",
        };
        TestServiceRegistrar nestedRegistrar = new("nested")
        {
            Name = "NestedRegistrar",
        };

        game.AddChild(firstRegistrar);
        game.AddChild(serviceContainer);
        serviceContainer.AddChild(renamedXRManager);
        serviceContainer.AddChild(nestedRegistrar);

        game._EnterTree();
        sceneTree.Root.AddChild(game);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Same(renamedXRManager, Game.Instance.GetRequiredService<XRManager>());
            Assert.Equal(["first", "nested"], Game.Instance.GetRequiredService<RegistrarDiscoveryLog>().Entries);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(game) && game.IsInsideTree())
            {
                game.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    /// <summary>
    /// Verifies resource-owned service registrars run through the generic <see cref="Game.ServiceRegistrars"/> path.
    /// </summary>
    [Fact]
    public async Task GameEnterTree_ExecutesConfiguredResourceServiceRegistrarsBeforeSceneOwnedRegistrars()
    {
        SceneTree sceneTree = GetSceneTree();
        TestGame game = new()
        {
            Name = "ConfiguredRegistrarFixture",
        };

        TestResourceRegistrar configuredRegistrar = new("configured-resource");
        TestXRManager xrManager = new()
        {
            Name = "XR",
        };
        TestServiceRegistrar sceneRegistrar = new("scene-owned");

        game.ServiceRegistrars.Add(configuredRegistrar);
        game.AddChild(xrManager);
        game.AddChild(sceneRegistrar);

        game._EnterTree();
        sceneTree.Root.AddChild(game);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Same(xrManager, Game.Instance.GetRequiredService<XRManager>());
            Assert.Equal(
                ["configured-resource", "scene-owned"],
                Game.Instance.GetRequiredService<RegistrarDiscoveryLog>().Entries);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(game) && game.IsInsideTree())
            {
                game.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    /// <summary>
    /// Verifies the authored global startup scene contains the configured resource registrars.
    /// </summary>
    [Fact]
    public async Task GlobalSceneEnterTree_ContainsConfiguredResourceRegistrars()
    {
        SceneTree sceneTree = GetSceneTree();
        Node game = LoadPackedScene(GlobalScenePath).Instantiate();

        sceneTree.Root.AddChild(game);

        try
        {
            await WaitForFramesAsync(sceneTree, 2);

            Godot.Collections.Array<Resource> serviceRegistrars = Assert.IsType<Godot.Collections.Array<Resource>>(
                game.Get("ServiceRegistrars").AsGodotArray<Resource>());
            Assert.Equal(2, serviceRegistrars.Count);

            Resource templateCompilerRegistrar = Assert.IsType<HandlebarsTemplateCompiler>(serviceRegistrars[0]);
            Resource promptWriterRegistrar = Assert.IsType<PseudoXmlPromptWriter>(serviceRegistrars[1]);

            Type templateCompilerInterface = templateCompilerRegistrar.GetType().GetInterface("AlleyCat.Templating.ITemplateCompiler")
                ?? throw new InvalidOperationException("Configured registrar must implement ITemplateCompiler.");
            Type templateServiceRegistrarInterface = templateCompilerRegistrar.GetType().GetInterface("AlleyCat.Core.IServiceRegistrar")
                ?? throw new InvalidOperationException("Template compiler registrar must implement IServiceRegistrar.");
            Type promptWriterInterface = promptWriterRegistrar.GetType().GetInterface("AlleyCat.Mind.AI.Prompting.IPromptWriter")
                ?? throw new InvalidOperationException("Configured registrar must implement IPromptWriter.");
            Type promptServiceRegistrarInterface = promptWriterRegistrar.GetType().GetInterface("AlleyCat.Core.IServiceRegistrar")
                ?? throw new InvalidOperationException("Prompt writer registrar must implement IServiceRegistrar.");

            Assert.Equal("AlleyCat.Templating.HandlebarsTemplateCompiler", templateCompilerRegistrar.GetType().FullName);
            Assert.Equal("AlleyCat.Templating.ITemplateCompiler", templateCompilerInterface.FullName);
            Assert.Equal("AlleyCat.Core.IServiceRegistrar", templateServiceRegistrarInterface.FullName);
            Assert.Equal("AlleyCat.Mind.AI.Prompting.PseudoXmlPromptWriter", promptWriterRegistrar.GetType().FullName);
            Assert.Equal("AlleyCat.Mind.AI.Prompting.IPromptWriter", promptWriterInterface.FullName);
            Assert.Equal("AlleyCat.Core.IServiceRegistrar", promptServiceRegistrarInterface.FullName);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(game) && game.IsInsideTree())
            {
                game.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    /// <summary>
    /// Verifies the Godot resource registrar directly registers itself as the template compiler service.
    /// </summary>
    [Fact]
    public void HandlebarsTemplateCompilerRegisterServices_RegistersSelfAsTemplateCompiler()
    {
        HandlebarsTemplateCompiler compiler = new();
        ServiceCollection services = new();

        compiler.RegisterServices(services);

        using ServiceProvider provider = services.BuildServiceProvider();
        ITemplateCompiler resolvedCompiler = provider.GetRequiredService<ITemplateCompiler>();

        Assert.Same(compiler, resolvedCompiler);
    }

    /// <summary>
    /// Verifies the Godot resource registrar directly registers itself as the prompt writer service.
    /// </summary>
    [Fact]
    public void PseudoXmlPromptWriterRegisterServices_RegistersSelfAsPromptWriter()
    {
        PseudoXmlPromptWriter writer = new();
        ServiceCollection services = new();

        writer.RegisterServices(services);

        using ServiceProvider provider = services.BuildServiceProvider();
        IPromptWriter resolvedWriter = provider.GetRequiredService<IPromptWriter>();

        Assert.Same(writer, resolvedWriter);
    }

    private static bool ReadBooleanProperty(object instance, string propertyName)
        => Assert.IsType<bool>(ReadPropertyValue(instance, propertyName));

    private static object? ReadPropertyValue(object instance, string propertyName)
    {
        PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Expected public property '{propertyName}' on '{instance.GetType().FullName}'.");

        return property.GetValue(instance);
    }

    private static async Task<StartupFixture> CreateStartupFixtureAsync(SceneTree sceneTree)
    {
        TestGame game = new()
        {
            Name = "Game",
            FallbackStartScene = FallbackStartScene,
        };

        TestXRManager xr = new()
        {
            Name = "XR",
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
        };

        TestSplashScreen splashScreen = new()
        {
            Name = "SplashScreen",
        };

        TestLoadingScreen loadingScreen = new()
        {
            Name = "LoadingScreen",
            Visible = false,
        };

        subViewport.AddChild(splashScreen);
        subViewport.AddChild(loadingScreen);
        xr.AddChild(subViewport);
        game.AddChild(xr);

        game._EnterTree();
        sceneTree.Root.AddChild(game);
        await WaitForFramesAsync(sceneTree, 2);

        return new StartupFixture(game, xr, splashScreen, loadingScreen);
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, StartupFixture fixture)
    {
        if (!GodotObject.IsInstanceValid(fixture.Game) || !fixture.Game.IsInsideTree())
        {
            return;
        }

        fixture.Game.QueueFree();
        await WaitForNextFrameAsync(sceneTree);
    }

    private static void AssignStartupFields(Game game, SplashScreen? splashScreen, LoadingScreen loadingScreen)
    {
        _splashScreenField.SetValue(game, splashScreen);
        _loadingScreenField.SetValue(game, loadingScreen);
    }

    private static Task InvokeRunStartupFlowAsync(Game game)
    {
        object? result = _runStartupFlowAsyncMethod.Invoke(game, null);
        return Assert.IsType<Task>(result, exactMatch: false);
    }

    private static void SignalXrInitialisationResult(Game game, bool succeeded)
        => _ = _onXRInitialisedMethod.Invoke(game, [succeeded]);

    private sealed record StartupFixture(
        TestGame Game,
        TestXRManager XRManager,
        TestSplashScreen SplashScreen,
        TestLoadingScreen LoadingScreen);

    private sealed partial class TestGame : Game
    {
        public List<int> QuitRequests { get; } = [];

        protected override void QuitGame(int exitCode)
            => QuitRequests.Add(exitCode);
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }
    }

    private sealed class RegistrarDiscoveryLog
    {
        public List<string> Entries { get; } = [];
    }

    private sealed partial class TestServiceRegistrar(string entry) : Node, IServiceRegistrar
    {
        public void RegisterServices(IServiceCollection services)
            => RegisterLogEntry(services, entry);
    }

    private sealed partial class TestResourceRegistrar(string entry) : Resource, IServiceRegistrar
    {
        public void RegisterServices(IServiceCollection services)
            => RegisterLogEntry(services, entry);
    }

    private static void RegisterLogEntry(IServiceCollection services, string entry)
    {
        ServiceDescriptor? descriptor = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(RegistrarDiscoveryLog));
        RegistrarDiscoveryLog log;
        if (descriptor?.ImplementationInstance is RegistrarDiscoveryLog existingLog)
        {
            log = existingLog;
        }
        else
        {
            log = new RegistrarDiscoveryLog();
            _ = services.AddSingleton(log);
        }

        log.Entries.Add(entry);
    }

    private sealed partial class TestSplashScreen : SplashScreen
    {
        public override void _Ready()
        {
        }

        public void EmitSplashFinished()
            => _ = EmitSignal(SignalName.SplashFinished);
    }

    private sealed partial class TestLoadingScreen : LoadingScreen
    {
        public int LoadSceneAsyncCallCount
        {
            get; private set;
        }

        public string? LastRequestedScenePath
        {
            get; private set;
        }

        public Error NextLoadSceneResult { get; set; } = Error.Ok;

        public override void _Ready()
        {
        }

        public override Error LoadSceneAsync(string scenePath)
        {
            LoadSceneAsyncCallCount++;
            LastRequestedScenePath = scenePath;
            return NextLoadSceneResult;
        }

        public void EmitLoadCompleted()
            => _ = EmitSignal(SignalName.LoadCompleted);
    }
}
