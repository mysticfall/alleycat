using AlleyCat.UI;
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
    private const string EmptyScenePath = "res://assets/scenes/empty.tscn";
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
    /// Verifies threaded loading progress, scene transition, and one-shot completion signalling.
    /// </summary>
    [Fact]
    public async Task LoadingScreen_LoadSceneAsync_TransitionsToLoadedSceneAndEmitsCompletionOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        LoadingScreen loadingScreen = await CreateRuntimeLoadingScreenAsync(sceneTree);
        ProgressBar loadingProgressBar = loadingScreen.GetNode<ProgressBar>("CenterContent/LoadingProgressBar");

        int completionSignalCount = 0;
        Error connectError = loadingScreen.Connect(
            LoadingScreen.SignalName.LoadCompleted,
            Callable.From(() => completionSignalCount++));
        Assert.Equal(Error.Ok, connectError);

        Error loadRequestError = loadingScreen.LoadSceneAsync(EmptyScenePath);
        Assert.Equal(Error.Ok, loadRequestError);

        Error secondLoadRequestError = loadingScreen.LoadSceneAsync(EmptyScenePath);
        Assert.Equal(Error.Busy, secondLoadRequestError);

        for (int frame = 0; frame < CompletionTimeoutFrames && completionSignalCount == 0; frame++)
        {
            loadingScreen._Process(0.0);
            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.Equal(1, completionSignalCount);

        await WaitForFramesAsync(sceneTree, 2);

        Node currentScene = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected current scene to be set after loading completes.");

        Assert.Equal(EmptyScenePath, currentScene.SceneFilePath);
        Assert.InRange(loadingProgressBar.Value, loadingProgressBar.MaxValue - 0.001, loadingProgressBar.MaxValue);

        await WaitForFramesAsync(sceneTree, 12);
        Assert.Equal(1, completionSignalCount);

        if (loadingScreen.IsInsideTree())
        {
            loadingScreen.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
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

    private static async Task<LoadingScreen> CreateRuntimeLoadingScreenAsync(SceneTree sceneTree)
    {
        LoadingScreen loadingScreen = new();
        VBoxContainer centreContent = new()
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

        centreContent.AddChild(loadingMessage);
        centreContent.AddChild(loadingProgressBar);
        loadingScreen.AddChild(centreContent);

        sceneTree.Root.AddChild(loadingScreen);
        await WaitForFramesAsync(sceneTree, 2);

        return loadingScreen;
    }
}
