using Godot;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Shared helper methods for Godot runtime integration tests.
/// </summary>
public static class TestUtils
{
    /// <summary>
    /// Returns the active <see cref="SceneTree"/> from the Godot main loop.
    /// </summary>
    public static SceneTree GetSceneTree()
        => Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Expected Godot SceneTree main loop during integration test execution.");

    /// <summary>
    /// Waits for the next process frame.
    /// </summary>
    public static async Task WaitForNextFrameAsync(SceneTree sceneTree)
        => _ = await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);

    /// <summary>
    /// Waits for the next physics frame.
    /// </summary>
    public static async Task WaitForNextPhysicsFrameAsync(SceneTree sceneTree)
        => _ = await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.PhysicsFrame);

    /// <summary>
    /// Waits for the given number of process frames.
    /// </summary>
    public static async Task WaitForFramesAsync(SceneTree sceneTree, int frameCount)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Waits for the given number of physics frames.
    /// </summary>
    public static async Task WaitForPhysicsFramesAsync(SceneTree sceneTree, int frameCount)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            await WaitForNextPhysicsFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Waits using a <see cref="SceneTreeTimer"/>, then synchronises one process frame.
    /// </summary>
    public static async Task WaitForSecondsAsync(SceneTree sceneTree, double seconds)
    {
        SceneTreeTimer timer = sceneTree.CreateTimer(seconds);
        _ = await sceneTree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        await WaitForNextFrameAsync(sceneTree);
    }

    /// <summary>
    /// Waits for the given amount of simulated physics time, then synchronises one physics frame.
    /// </summary>
    public static async Task WaitForPhysicsSecondsAsync(SceneTree sceneTree, double seconds)
    {
        SceneTreeTimer timer = sceneTree.CreateTimer(seconds, processAlways: true, processInPhysics: true);
        _ = await sceneTree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        await WaitForNextPhysicsFrameAsync(sceneTree);
    }

    /// <summary>
    /// Loads a packed scene and throws when unavailable.
    /// </summary>
    public static PackedScene LoadPackedScene(string path)
        => ResourceLoader.Load<PackedScene>(path)
            ?? throw new InvalidOperationException($"Failed to load packed scene at path '{path}'.");
}
