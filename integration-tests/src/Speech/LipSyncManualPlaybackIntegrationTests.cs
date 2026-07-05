using AlleyCat.Speech.LipSync;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for manual lip-sync playback controls.
/// </summary>
public sealed partial class LipSyncManualPlaybackIntegrationTests
{
    private const string SampleSpeechPath = "res://assets/audio/samples/sample-voice.wav";

    /// <summary>
    /// Verifies a second manual play request during active playback restarts the player immediately.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_Play_WhenCalledDuringActivePlayback_RestartsStateImmediately()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncManualPlaybackTestRoot",
        };

        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioStreamPlayer3D",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton3D",
        };

        FakeLipSyncPlayer player = new()
        {
            Name = "LipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
            LoopPlayback = true,
        };

        AudioStreamWav speech = GD.Load<AudioStreamWav>(SampleSpeechPath)
            ?? throw new InvalidOperationException($"Failed to load sample speech clip at '{SampleSpeechPath}'.");

        root.AddChild(audioPlayer);
        root.AddChild(skeleton);
        root.AddChild(player);
        sceneTree.Root.AddChild(root);
        player._Ready();
        await WaitForFramesAsync(sceneTree, 5);

        try
        {
            Assert.True(player.IsInitialised, player.InitialisationError);

            player.Play(speech);
            Assert.True(string.IsNullOrWhiteSpace(player.PlaybackError), player.PlaybackError);
            Assert.Equal(1, player.InferenceCallCount);
            Assert.Equal(1, player.AppliedFrameCount);
            Assert.Same(speech, audioPlayer.Stream);

            player.Play(speech);

            Assert.Equal(2, player.InferenceCallCount);
            Assert.Equal(1, player.AppliedFrameCount);
            Assert.True(string.IsNullOrWhiteSpace(player.PlaybackError), player.PlaybackError);
            Assert.Same(speech, audioPlayer.Stream);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    /// <summary>
    /// Verifies lip-sync binding discovers only skeleton descendants with requested blendshapes.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_Play_WithSkeletonRoot_MapsOnlyDescendantMeshesWithRequestedBlendshapes()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncMeshDiscoveryTestRoot",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton3D",
        };
        Node3D nested = new()
        {
            Name = "NestedMeshes",
        };
        MeshInstance3D directMatch = CreateMeshInstance("GeneratedBody", "jaw_open");
        MeshInstance3D nestedMatch = CreateMeshInstance("GeneratedFace", "JawOpen");
        MeshInstance3D ignoredDescendant = CreateMeshInstance("GeneratedHair", "browInnerUp");
        MeshInstance3D ignoredSibling = CreateMeshInstance("SiblingFace", "JawOpen");
        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioStreamPlayer3D",
        };
        FakeLipSyncPlayer player = new()
        {
            Name = "LipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
        };

        AudioStreamWav speech = GD.Load<AudioStreamWav>(SampleSpeechPath)
            ?? throw new InvalidOperationException($"Failed to load sample speech clip at '{SampleSpeechPath}'.");

        skeleton.AddChild(directMatch);
        skeleton.AddChild(nested);
        nested.AddChild(nestedMatch);
        skeleton.AddChild(ignoredDescendant);
        root.AddChild(skeleton);
        root.AddChild(ignoredSibling);
        root.AddChild(audioPlayer);
        root.AddChild(player);
        sceneTree.Root.AddChild(root);
        player._Ready();
        await WaitForFramesAsync(sceneTree, 5);

        try
        {
            Assert.True(player.IsInitialised, player.InitialisationError);

            player.Play(speech);

            Assert.True(string.IsNullOrWhiteSpace(player.PlaybackError), player.PlaybackError);
            Assert.Equal(2, player.MappedMeshCount);
            Assert.Equal(2, player.MappedChannelCount);
            Assert.Equal(0f, directMatch.GetBlendShapeValue(0));
            Assert.Equal(0f, nestedMatch.GetBlendShapeValue(0));
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    private static MeshInstance3D CreateMeshInstance(string name, string blendshapeName)
    {
        ArrayMesh mesh = new();
        mesh.AddBlendShape(blendshapeName);

        return new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
        };
    }
}

internal sealed partial class FakeLipSyncPlayer : LipSyncPlayer
{
    public int InferenceCallCount
    {
        get;
        private set;
    }

    protected override void InitialiseBackend()
    {
    }

    public override void _Ready() => base._Ready();

    protected override LipSyncInferenceResult RunBackendInference(AudioStreamWav speech)
    {
        InferenceCallCount++;

        return new LipSyncInferenceResult(
            [
                [0f],
                [1f],
                [0.25f],
                [0.75f],
            ],
            ["JawOpen"],
            30f);
    }

    protected override void DisposeBackend()
    {
    }
}
