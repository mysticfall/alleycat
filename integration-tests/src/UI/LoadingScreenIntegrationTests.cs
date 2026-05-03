using AlleyCat.UI;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using UIControl = Godot.Control;

namespace AlleyCat.IntegrationTests.UI;

/// <summary>
/// Integration coverage for loading-screen layout and scene-transition behaviour.
/// </summary>
public sealed class LoadingScreenIntegrationTests
{
    private const string LoadingScreenScenePath = "res://assets/ui/loading_screen.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string EmptyScenePath = "res://assets/scenes/empty.tscn";
    private const string RecenterInstructionMessage = "Stand up straight and recentre your headset to continue.";
    private const float CentreTolerancePixels = 4.0f;
    private const int CompletionTimeoutFrames = 240;

    /// <summary>
    /// Verifies required loading controls are present and centred within the root control.
    /// </summary>
    [Fact]
    public async Task LoadingScreen_Layout_ContainsCentredMessageAndProgressBar()
    {
        SceneTree sceneTree = GetSceneTree();
        Node loadingScreen = await InstantiateLoadingScreenAsync(sceneTree);

        try
        {
            UIControl loadingScreenControl = Assert.IsType<UIControl>(loadingScreen, exactMatch: false);
            Label loadingMessage = loadingScreen.GetNode<Label>("CenterContent/LoadingMessage");
            ProgressBar loadingProgressBar = loadingScreen.GetNode<ProgressBar>("CenterContent/LoadingProgressBar");
            UIControl centreContent = loadingScreen.GetNode<UIControl>("CenterContent");

            Assert.NotNull(loadingMessage);
            Assert.NotNull(loadingProgressBar);

            Assert.Equal(0.5f, centreContent.AnchorLeft);
            Assert.Equal(0.5f, centreContent.AnchorTop);
            Assert.Equal(0.5f, centreContent.AnchorRight);
            Assert.Equal(0.5f, centreContent.AnchorBottom);

            Vector2 rootCentre = loadingScreenControl.GetGlobalRect().GetCenter();
            Vector2 contentCentre = centreContent.GetGlobalRect().GetCenter();

            Assert.InRange(contentCentre.X, rootCentre.X - CentreTolerancePixels, rootCentre.X + CentreTolerancePixels);
            Assert.InRange(contentCentre.Y, rootCentre.Y - CentreTolerancePixels, rootCentre.Y + CentreTolerancePixels);
        }
        finally
        {
            loadingScreen.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies completed scene loads enter the recenter wait state instead of completing immediately.
    /// </summary>
    [Fact]
    public async Task LoadingScreen_LoadSceneAsync_AfterSceneLoads_HidesProgressBarAndShowsRecenterInstructionWithoutCompleting()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeLoadingScreenFixture fixture = await CreateRuntimeLoadingScreenFixtureAsync(sceneTree);

        int completionSignalCount = 0;
        Error connectError = fixture.LoadingScreen.Connect(
            LoadingScreen.SignalName.LoadCompleted,
            Callable.From(() => completionSignalCount++));
        Assert.Equal(Error.Ok, connectError);

        try
        {
            Error loadRequestError = fixture.LoadingScreen.LoadSceneAsync(EmptyScenePath);
            Assert.Equal(Error.Ok, loadRequestError);

            Error secondLoadRequestError = fixture.LoadingScreen.LoadSceneAsync(EmptyScenePath);
            Assert.Equal(Error.Busy, secondLoadRequestError);

            await WaitForLoadToReachRecenterStateAsync(sceneTree, fixture);

            Assert.False(fixture.LoadingProgressBar.Visible);
            Assert.Equal(RecenterInstructionMessage, fixture.LoadingMessage.Text);
            Assert.Equal(0, completionSignalCount);

            await WaitForFramesAsync(sceneTree, 8);
            Assert.Equal(0, completionSignalCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    /// <summary>
    /// Verifies scene transition and completion occur only after the XR recenter signal arrives.
    /// </summary>
    [Fact]
    public async Task LoadingScreen_LoadSceneAsync_CompletesAfterPoseRecenter()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeLoadingScreenFixture fixture = await CreateRuntimeLoadingScreenFixtureAsync(sceneTree);

        int completionSignalCount = 0;
        Error connectError = fixture.LoadingScreen.Connect(
            LoadingScreen.SignalName.LoadCompleted,
            Callable.From(() => completionSignalCount++));
        Assert.Equal(Error.Ok, connectError);

        try
        {
            Error loadRequestError = fixture.LoadingScreen.LoadSceneAsync(EmptyScenePath);
            Assert.Equal(Error.Ok, loadRequestError);

            await WaitForLoadToReachRecenterStateAsync(sceneTree, fixture);
            Assert.Equal(0, completionSignalCount);

            _ = fixture.XRManager.EmitSignal(XRManager.SignalName.PoseRecentered);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(1, completionSignalCount);

            Node currentScene = sceneTree.CurrentScene
                ?? throw new Xunit.Sdk.XunitException("Expected current scene to be set after pose recenter completes loading.");

            Assert.Equal(EmptyScenePath, currentScene.SceneFilePath);
            Assert.InRange(fixture.LoadingProgressBar.Value, fixture.LoadingProgressBar.MaxValue - 0.001, fixture.LoadingProgressBar.MaxValue);

            await WaitForFramesAsync(sceneTree, 12);
            Assert.Equal(1, completionSignalCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture);
        }
    }

    private static async Task<Node> InstantiateLoadingScreenAsync(SceneTree sceneTree)
    {
        PackedScene loadingScreenScene = LoadPackedScene(LoadingScreenScenePath);
        Node loadingScreen = loadingScreenScene.Instantiate();

        sceneTree.Root.AddChild(loadingScreen);
        await WaitForFramesAsync(sceneTree, 2);

        return loadingScreen;
    }

    private static async Task<RuntimeLoadingScreenFixture> CreateRuntimeLoadingScreenFixtureAsync(SceneTree sceneTree)
    {
        PackedScene mockRuntimeScene = LoadPackedScene(MockRuntimeScenePath);

        XRManager xrManager = new()
        {
            Name = "XR",
            OpenXrRuntimeScene = mockRuntimeScene,
            MockRuntimeScene = mockRuntimeScene,
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
        };

        LoadingScreen loadingScreen = new()
        {
            Name = "LoadingScreen",
            XRManagerPath = new NodePath("../.."),
        };

        VBoxContainer centerContent = new()
        {
            Name = "CenterContent",
        };

        Label loadingMessage = new()
        {
            Name = "LoadingMessage",
        };

        ProgressBar loadingProgressBar = new()
        {
            Name = "LoadingProgressBar",
        };

        centerContent.AddChild(loadingMessage);
        centerContent.AddChild(loadingProgressBar);
        loadingScreen.AddChild(centerContent);

        subViewport.AddChild(loadingScreen);
        xrManager.AddChild(subViewport);
        sceneTree.Root.AddChild(xrManager);
        await WaitForFramesAsync(sceneTree, 2);

        return new RuntimeLoadingScreenFixture(xrManager, loadingScreen, loadingProgressBar, loadingMessage);
    }

    private static async Task WaitForLoadToReachRecenterStateAsync(SceneTree sceneTree, RuntimeLoadingScreenFixture fixture)
    {
        for (int frame = 0; frame < CompletionTimeoutFrames && fixture.LoadingProgressBar.Visible; frame++)
        {
            fixture.LoadingScreen._Process(0.0);
            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.False(fixture.LoadingProgressBar.Visible);
    }

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, RuntimeLoadingScreenFixture fixture)
    {
        if (!GodotObject.IsInstanceValid(fixture.XRManager) || !fixture.XRManager.IsInsideTree())
        {
            return;
        }

        fixture.XRManager.QueueFree();
        await WaitForNextFrameAsync(sceneTree);
    }

    private sealed record RuntimeLoadingScreenFixture(
        XRManager XRManager,
        LoadingScreen LoadingScreen,
        ProgressBar LoadingProgressBar,
        Label LoadingMessage);
}
