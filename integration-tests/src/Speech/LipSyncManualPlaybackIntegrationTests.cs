using System.Buffers.Binary;
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

    /// <summary>
    /// Verifies the playback-completed notification is raised exactly once when playback ends naturally.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_WhenPlaybackEndsNaturally_RaisesPlaybackCompletedExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncPlaybackCompletionTestRoot",
        };

        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioStreamPlayer3D",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton3D",
        };

        // Frames (12 at 30 fps = 0.4 s) intentionally outlast the audio (0.25 s) so completion is observed through
        // the audio-playing poll's falling edge rather than frame exhaustion.
        ConfigurableFrameLipSyncPlayer player = new(frameCount: 12)
        {
            Name = "LipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
        };

        root.AddChild(audioPlayer);
        root.AddChild(skeleton);
        root.AddChild(player);
        await AttachToRootAfterBootSettleAsync(sceneTree, root);
        player._Ready();
        await WaitForFramesAsync(sceneTree, 2);

        int completedCount = 0;
        player.PlaybackCompleted += () => completedCount++;

        try
        {
            Assert.True(player.IsInitialised, player.InitialisationError);

            player.Play(CreateSilenceStream(seconds: 0.25));
            Assert.True(string.IsNullOrWhiteSpace(player.PlaybackError), player.PlaybackError);

            await WaitUntilAsync(sceneTree, () => completedCount == 1, maxFrames: 600);
            await WaitForFramesAsync(sceneTree, 30);

            Assert.Equal(1, completedCount);
            // A completion raised through the failure path must not satisfy the natural-end contract.
            Assert.True(string.IsNullOrWhiteSpace(player.PlaybackError), player.PlaybackError);
            Assert.False(player.IsAudioPlaying);
            Assert.False(audioPlayer.IsPlaying());
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    /// <summary>
    /// Verifies the stop/cut capability halts audio and lip-sync frame application immediately and is safe when idle.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_Stop_WhenPlaybackIsActive_HaltsAudioAndFrameApplicationImmediately()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncStopCutTestRoot",
        };

        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioStreamPlayer3D",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton3D",
        };

        ConfigurableFrameLipSyncPlayer player = new(frameCount: 90)
        {
            Name = "LipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
        };

        root.AddChild(audioPlayer);
        root.AddChild(skeleton);
        root.AddChild(player);
        await AttachToRootAfterBootSettleAsync(sceneTree, root);
        player._Ready();
        await WaitForFramesAsync(sceneTree, 2);

        int completedCount = 0;
        player.PlaybackCompleted += () => completedCount++;

        try
        {
            Assert.True(player.IsInitialised, player.InitialisationError);

            player.Play(CreateSilenceStream(seconds: 2));
            Assert.True(string.IsNullOrWhiteSpace(player.PlaybackError), player.PlaybackError);
            Assert.True(player.IsAudioPlaying);
            await WaitUntilAsync(sceneTree, audioPlayer.IsPlaying, maxFrames: 120);
            Assert.True(player.AppliedFrameCount > 0);

            player.Stop();

            Assert.False(player.IsAudioPlaying);
            Assert.False(audioPlayer.IsPlaying());
            int appliedFrameCountAtCut = player.AppliedFrameCount;
            await WaitForFramesAsync(sceneTree, 5);
            Assert.Equal(appliedFrameCountAtCut, player.AppliedFrameCount);
            Assert.Equal(0, completedCount);

            // Stopping while playback is inactive must remain safe and must not raise completion.
            player.Stop();
            await WaitForFramesAsync(sceneTree, 2);
            Assert.Equal(0, completedCount);
            Assert.False(player.IsAudioPlaying);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    /// <summary>
    /// Verifies a session that fails inside the polling loop still raises the playback-completed notification once,
    /// so playback watchers such as a voice's speaking window cannot hang open on a failed session.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_WhenAudioStopsBeforeStarting_FailsAndRaisesPlaybackCompletedExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncSyncLostTestRoot",
        };

        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioStreamPlayer3D",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton3D",
        };

        ConfigurableFrameLipSyncPlayer player = new(frameCount: 90)
        {
            Name = "LipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
        };

        root.AddChild(audioPlayer);
        root.AddChild(skeleton);
        root.AddChild(player);
        await AttachToRootAfterBootSettleAsync(sceneTree, root);
        player._Ready();
        await WaitForFramesAsync(sceneTree, 2);

        int completedCount = 0;
        player.PlaybackCompleted += () => completedCount++;

        try
        {
            Assert.True(player.IsInitialised, player.InitialisationError);

            // Stopping the audio player before the poll ever observes it playing leaves the lip-sync session
            // without a rising audio edge, so its bounded start grace must fail the session.
            player.Play(CreateSilenceStream(seconds: 2));
            Assert.True(player.IsAudioPlaying);
            audioPlayer.Stop();

            await WaitUntilAsync(sceneTree, () => completedCount == 1, maxFrames: 600);
            await WaitForFramesAsync(sceneTree, 30);

            Assert.False(string.IsNullOrWhiteSpace(player.PlaybackError));
            Assert.False(player.IsAudioPlaying);
            Assert.Equal(1, completedCount);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    /// <summary>
    /// Verifies a non-backend-rate stream is resampled for inference while playback keeps the original stream.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_Play_WithNonBackendRateStream_ResamplesOnlyTheInferenceCopy()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncInferenceResampleTestRoot",
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
        };

        // Four samples at 8000 Hz: inference must receive eight resampled samples at 16000 Hz.
        AudioStreamWav speech = CreateMonoPcm16Stream([0, 16, 32, 48], mixRate: 8000);

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

            AudioStreamWav inferenceStream = player.LastInferenceStream!;
            Assert.NotSame(speech, inferenceStream);
            Assert.Equal(16000, inferenceStream.MixRate);
            Assert.Equal(AudioStreamWav.FormatEnum.Format16Bits, inferenceStream.Format);
            Assert.False(inferenceStream.Stereo);
            Assert.Equal(16, inferenceStream.Data.Length);

            // Playback must keep the original 8000 Hz stream untouched.
            Assert.Same(speech, audioPlayer.Stream);
            Assert.Equal(8, speech.Data.Length);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    /// <summary>
    /// Verifies a stream already at the backend sample rate reaches inference as the same instance without a copy.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_Play_WithBackendRateStream_PassesSameStreamInstanceToInference()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncInferenceIdentityTestRoot",
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
        };

        AudioStreamWav speech = CreateMonoPcm16Stream([0, 16, 32, 48], mixRate: 16000);

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
            Assert.Same(speech, player.LastInferenceStream);
            Assert.Same(speech, audioPlayer.Stream);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    /// <summary>
    /// Verifies a non-PCM16 stream fails playback before inference runs, surfacing the format mismatch.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_Play_WithNonPcm16Stream_FailsPlaybackWithoutRunningInference()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncFormatRejectTestRoot",
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
        };

        AudioStreamWav speech = new()
        {
            Data = [0x01, 0x02, 0x03, 0x04],
            Format = AudioStreamWav.FormatEnum.Format8Bits,
            MixRate = 16000,
            Stereo = false,
        };

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

            Assert.False(string.IsNullOrWhiteSpace(player.PlaybackError));
            Assert.Contains("expected AudioStreamWav format", player.PlaybackError, StringComparison.Ordinal);
            Assert.Equal(0, player.InferenceCallCount);
            Assert.Null(player.LastInferenceStream);
            Assert.Null(audioPlayer.Stream);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    /// <summary>
    /// Verifies a stereo stream fails playback before inference runs, surfacing the mono-only contract.
    /// </summary>
    [Fact]
    [Headless]
    public async Task LipSyncPlayer_Play_WithStereoStream_FailsPlaybackWithoutRunningInference()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "LipSyncStereoRejectTestRoot",
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
        };

        AudioStreamWav speech = new()
        {
            Data = [0x01, 0x02, 0x03, 0x04],
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = 16000,
            Stereo = true,
        };

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

            Assert.False(string.IsNullOrWhiteSpace(player.PlaybackError));
            Assert.Contains("stereo", player.PlaybackError, StringComparison.Ordinal);
            Assert.Equal(0, player.InferenceCallCount);
            Assert.Null(player.LastInferenceStream);
            Assert.Null(audioPlayer.Stream);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 5);
        }
    }

    private static AudioStreamWav CreateSilenceStream(double seconds)
    {
        int sampleCount = (int)(16000d * seconds);
        return new AudioStreamWav
        {
            Data = new byte[sampleCount * 2],
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = 16000,
            Stereo = false,
        };
    }

    private static AudioStreamWav CreateMonoPcm16Stream(short[] samples, int mixRate)
    {
        byte[] data = new byte[samples.Length * sizeof(short)];
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(sampleIndex * sizeof(short)),
                samples[sampleIndex]);
        }

        return new AudioStreamWav
        {
            Data = data,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = mixRate,
            Stereo = false,
        };
    }

    /// <summary>
    /// Attaches a fixture root to the root window only after the boot frame has completed: adding children to the
    /// root window from the autoload ready phase (frame 0) silently drops them, leaving the fixture orphaned.
    /// </summary>
    private static async Task AttachToRootAfterBootSettleAsync(SceneTree sceneTree, Node root)
    {
        await WaitForFramesAsync(sceneTree, 2);
        Assert.True(root.GetParent() is null, "The fixture root must not already be attached.");
        sceneTree.Root.AddChild(root);
        await WaitForNextFrameAsync(sceneTree);
        Assert.True(root.IsInsideTree(), "The fixture root failed to attach to the scene tree root.");
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

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (predicate())
            {
                return;
            }

            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }
}

internal sealed partial class FakeLipSyncPlayer : LipSyncPlayer
{
    public int InferenceCallCount
    {
        get;
        private set;
    }

    public AudioStreamWav? LastInferenceStream
    {
        get;
        private set;
    }

    protected override int BackendSampleRate => 16000;

    protected override void InitialiseBackend()
    {
    }

    public override void _Ready() => base._Ready();

    protected override LipSyncInferenceResult RunBackendInference(
        AudioStreamWav speech,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InferenceCallCount++;
        LastInferenceStream = speech;

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

internal sealed partial class ConfigurableFrameLipSyncPlayer(int frameCount) : LipSyncPlayer
{
    public override void _Ready() => base._Ready();

    protected override int BackendSampleRate => 16000;

    protected override void InitialiseBackend()
    {
    }

    protected override LipSyncInferenceResult RunBackendInference(
        AudioStreamWav speech,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = speech;

        float[][] frames = new float[frameCount][];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            frames[frameIndex] = [frameIndex % 2 == 0 ? 0.5f : 1f];
        }

        return new LipSyncInferenceResult(frames, ["jawOpen"], 30f);
    }

    protected override void DisposeBackend()
    {
    }
}
