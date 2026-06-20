using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Shared helper methods for Godot runtime integration tests.
/// </summary>
public static class TestUtils
{
    private const string SceneInstallationContextTypeName = "AlleyCat.Core.Installer.SceneInstallationContext";
    private const string DefaultInstallerNamespace = "alleycat.scene_installer";

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

    /// <summary>
    /// Resolves the NPC actor from the mirror-room scene, preferring the current authored Ally path while accepting
    /// older Female-authored fixtures for compatibility.
    /// </summary>
    public static Node GetMirrorRoomAllyActor(Node mirrorRoom)
        => mirrorRoom.GetNodeOrNull("Actors/Ally")
            ?? mirrorRoom.GetNodeOrNull("Actors/Female")
            ?? throw new Xunit.Sdk.XunitException("Expected mirror-room scene to contain an NPC actor at 'Actors/Ally'.");

    /// <summary>
    /// Resolves the NPC actor from the mirror-room scene as a <see cref="Node3D"/>.
    /// </summary>
    public static Node3D GetMirrorRoomAllyActor3D(Node mirrorRoom)
        => Assert.IsAssignableFrom<Node3D>(GetMirrorRoomAllyActor(mirrorRoom));

    /// <summary>
    /// Ensures an installer-driven runtime character scene has materialised its template-owned nodes.
    /// </summary>
    public static void EnsureCharacterRuntimeInstalled(Node characterRoot)
    {
        if (characterRoot.GetNodeOrNull("AnimationTree") is not null
            && characterRoot.GetNodeOrNull("Female/GeneralSkeleton") is not null)
        {
            return;
        }

        Node? installer = characterRoot.GetNodeOrNull("PlayerCharacterInstaller")
            ?? characterRoot.GetNodeOrNull("NPCCharacterInstaller")
            ?? characterRoot.GetNodeOrNull("BaseCharacterInstaller");
        Assert.NotNull(installer);

        Type installerType = installer.GetType();
        Type contextType = installerType.Assembly.GetType(SceneInstallationContextTypeName)
            ?? throw new InvalidOperationException("Failed to resolve loaded SceneInstallationContext type.");
        object context = Activator.CreateInstance(contextType, characterRoot, DefaultInstallerNamespace)
            ?? throw new InvalidOperationException("Failed to create loaded scene installation context.");
        object result = installerType.GetMethod("Install")?.Invoke(installer, [context])
            ?? throw new InvalidOperationException("Failed to invoke character scene installer.");
        bool succeeded = (bool)(result.GetType().GetProperty("Succeeded")?.GetValue(result) ?? false);
        object? errors = result.GetType().GetProperty("Errors")?.GetValue(result);
        string errorText = errors is IEnumerable<string> typedErrors
            ? string.Join('\n', typedErrors)
            : errors?.ToString() ?? string.Empty;

        Assert.True(succeeded, errorText);
    }
}
