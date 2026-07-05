using AlleyCat.Speech.LipSync;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for the Audio2Face lip-sync backend initialisation boundary.
/// </summary>
public sealed partial class A2FLipSyncPlayerIntegrationTests
{
    /// <summary>
    /// Verifies the exported health probe defaults off so startup does not require a local backend.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FLipSyncPlayer_Ready_WithDefaultSettings_DoesNotProbeHealth()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "A2FLipSyncDefaultProbeTestRoot",
        };
        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioStreamPlayer3D",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton3D",
        };
        A2FLipSyncPlayer player = new()
        {
            Name = "A2FLipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
            EndpointUrl = "http://127.0.0.1:1/blendshapes",
            ProbeHealthRetries = 1,
            ProbeHealthRetryDelayMs = 10,
        };

        try
        {
            _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
            _ = root.CallDeferred(Node.MethodName.AddChild, audioPlayer);
            _ = root.CallDeferred(Node.MethodName.AddChild, skeleton);
            _ = root.CallDeferred(Node.MethodName.AddChild, player);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.False(player.ProbeHealthOnInitialise);
            Assert.True(player.IsInitialised, player.InitialisationError);
            Assert.True(string.IsNullOrWhiteSpace(player.InitialisationError), player.InitialisationError);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Verifies explicitly enabling the health probe preserves failing startup semantics when no backend is available.
    /// </summary>
    [Fact]
    [Headless]
    public async Task A2FLipSyncPlayer_Ready_WithHealthProbeEnabled_FailsWhenBackendUnavailable()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "A2FLipSyncEnabledProbeTestRoot",
        };
        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioStreamPlayer3D",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton3D",
        };
        A2FLipSyncPlayer player = new()
        {
            Name = "A2FLipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
            EndpointUrl = "http://127.0.0.1:1/blendshapes",
            ProbeHealthOnInitialise = true,
            ProbeHealthRetries = 1,
            ProbeHealthRetryDelayMs = 10,
            RequestTimeoutSeconds = 1,
        };

        try
        {
            _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);
            _ = root.CallDeferred(Node.MethodName.AddChild, audioPlayer);
            _ = root.CallDeferred(Node.MethodName.AddChild, skeleton);
            _ = root.CallDeferred(Node.MethodName.AddChild, player);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.False(player.IsInitialised);
            Assert.Contains("health probe failed after 1 attempt(s)", player.InitialisationError, StringComparison.Ordinal);
            Assert.Contains("http://127.0.0.1:1/health", player.InitialisationError, StringComparison.Ordinal);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }
}
