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

        FakeLipSyncPlayer player = new()
        {
            Name = "LipSyncPlayer",
            AudioPlayer = audioPlayer,
            LoopPlayback = true,
        };

        AudioStreamWav speech = GD.Load<AudioStreamWav>(SampleSpeechPath)
            ?? throw new InvalidOperationException($"Failed to load sample speech clip at '{SampleSpeechPath}'.");

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
