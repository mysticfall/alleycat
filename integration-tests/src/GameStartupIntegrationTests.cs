using System.Reflection;
using AlleyCat.UI;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests;

/// <summary>
/// Runtime integration coverage for startup orchestration branches in <see cref="Game"/>.
/// </summary>
public sealed partial class GameStartupIntegrationTests
{
    private const string StartScenePath = "res://assets/scenes/empty.tscn";

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
            Assert.Equal(StartScenePath, fixture.LoadingScreen.LastRequestedScenePath);
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
            Assert.Equal(StartScenePath, fixture.LoadingScreen.LastRequestedScenePath);
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
            Assert.Equal(StartScenePath, fixture.LoadingScreen.LastRequestedScenePath);
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

    private static async Task<StartupFixture> CreateStartupFixtureAsync(SceneTree sceneTree)
    {
        TestGame game = new()
        {
            Name = "Game",
            StartScenePath = StartScenePath,
        };

        Node xr = new()
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

        sceneTree.Root.AddChild(game);
        await WaitForFramesAsync(sceneTree, 2);

        return new StartupFixture(game, splashScreen, loadingScreen);
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
        TestSplashScreen SplashScreen,
        TestLoadingScreen LoadingScreen);

    private sealed partial class TestGame : Game
    {
        public List<int> QuitRequests { get; } = [];

        protected override void QuitGame(int exitCode)
            => QuitRequests.Add(exitCode);
    }

    private sealed partial class TestSplashScreen : SplashScreen
    {
        public override void _Ready()
        {
        }

        public void EmitSplashFinished()
            => _ = EmitSignal(SplashScreen.SignalName.SplashFinished);
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
            => _ = EmitSignal(LoadingScreen.SignalName.LoadCompleted);
    }
}
