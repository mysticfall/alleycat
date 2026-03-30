using AlleyCat.UI;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.UI;

/// <summary>
/// Integration coverage for splash-screen timing and layout behaviour.
/// </summary>
public sealed partial class SplashScreenIntegrationTests
    : IAsyncLifetime
{
    private const double AcceleratedTimeScale = 10.0;

    private double _previousTimeScale;

    // Engine.TimeScale is global engine state; this test scopes changes to lifecycle and restores it in DisposeAsync.
    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _previousTimeScale = Engine.TimeScale;

        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);
        await WaitForNextFrameAsync(sceneTree);

        Engine.TimeScale = AcceleratedTimeScale;
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        Engine.TimeScale = _previousTimeScale;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies the splash logo stays transparent for ~2s, then fades to full alpha over ~3s while occupying ~50% viewport width.
    /// </summary>
    [Fact]
    public async Task SplashScreen_LogoFade_MatchesSpecTimingAndSize()
    {
        SceneTree sceneTree = GetSceneTree();

        Node splashScreen = Assert.IsAssignableFrom<Node>(sceneTree.CurrentScene);
        Assert.Equal(typeof(SplashScreen).FullName, splashScreen.GetType().FullName);

        Control splashControl = Assert.IsAssignableFrom<Control>(splashScreen);
        TextureRect logo = splashScreen.GetNode<TextureRect>("Logo");

        float viewportWidth = splashControl.GetViewport().GetVisibleRect().Size.X;
        Assert.True(viewportWidth > 0.0f, "Viewport width must be positive before logo ratio checks.");

        float logoWidthRatio = logo.GetGlobalRect().Size.X / viewportWidth;
        Assert.InRange(logoWidthRatio, 0.45f, 0.55f);

        Assert.InRange(logo.Modulate.A, 0.0f, 0.01f);

        await WaitForSecondsAsync(sceneTree, 1.9);
        Assert.InRange(logo.Modulate.A, 0.0f, 0.05f);

        await WaitForSecondsAsync(sceneTree, 0.35);
        Assert.InRange(logo.Modulate.A, 0.02f, 0.30f);

        await WaitForSecondsAsync(sceneTree, 2.85);
        Assert.InRange(logo.Modulate.A, 0.95f, 1.0f);
    }

    private static SceneTree GetSceneTree()
        => Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Expected Godot SceneTree main loop during integration test execution.");

    private static async Task WaitForNextFrameAsync(SceneTree sceneTree)
        => _ = await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);

    private static async Task WaitForSecondsAsync(SceneTree sceneTree, double seconds)
    {
        SceneTreeTimer timer = sceneTree.CreateTimer(seconds);
        _ = await sceneTree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        await WaitForNextFrameAsync(sceneTree);
    }
}
