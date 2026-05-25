using System.Text;
using AlleyCat.Body.Voice;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for AI voice orchestration without backend dependencies.
/// </summary>
public sealed partial class AIVoiceIntegrationTests
{
    /// <summary>
    /// Valid WAV output must be handed off through the lip-sync playback boundary.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Speak_WithCompatibleWaveAudio_PlaysGeneratedSpeechThroughLipSyncBoundary()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            NextResult = CreateWaveFileBytes([0x34, 0x12, 0x78, 0x56], sampleRate: 16000, channelCount: 1, bitsPerSample: 16),
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };

        sceneTree.Root.AddChild(speechGenerator);
        sceneTree.Root.AddChild(lipSyncPlayer);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.Speak("Hello alley cat");
            await WaitUntilAsync(sceneTree, () => voice.LastPlayedSpeech is not null || voice.FailureErrors.Count > 0, 30);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
            AudioStreamWav playedSpeech = Assert.IsType<AudioStreamWav>(voice.LastPlayedSpeech);
            Assert.Equal(AudioStreamWav.FormatEnum.Format16Bits, playedSpeech.Format);
            Assert.Equal(16000, playedSpeech.MixRate);
            Assert.False(playedSpeech.Stereo);
            Assert.Equal(new byte[] { 0x34, 0x12, 0x78, 0x56 }, playedSpeech.Data);
            Assert.Empty(voice.FailureErrors);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Incompatible audio must fail gracefully instead of starting playback.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Speak_WithIncompatibleAudio_EmitsSpeechFailed_AndSkipsPlayback()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            NextResult = [0x01, 0x02, 0x03],
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };

        sceneTree.Root.AddChild(speechGenerator);
        sceneTree.Root.AddChild(lipSyncPlayer);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.Speak("Hello alley cat");
            await WaitUntilAsync(sceneTree, () => voice.FailureErrors.Count > 0, 30);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Null(voice.LastPlayedSpeech);
            string error = Assert.Single(voice.FailureErrors);
            Assert.Equal("Audio format incompatible", error);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Generator failures must emit the failure signal and stop before playback is attempted.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Speak_WhenSpeechGenerationThrows_EmitsSpeechFailed_AndDoesNotAttemptPlayback()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            GenerateException = new InvalidOperationException("speech backend unavailable"),
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };

        sceneTree.Root.AddChild(speechGenerator);
        sceneTree.Root.AddChild(lipSyncPlayer);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.Speak("Hello alley cat");
            await WaitUntilAsync(sceneTree, () => voice.FailureErrors.Count > 0, 30);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Null(voice.LastPlayedSpeech);
            string error = Assert.Single(voice.FailureErrors);
            Assert.Equal("speech backend unavailable", error);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Disabled voices must short-circuit before generation or playback work begins.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Speak_WhenDisabled_ShortCircuitsWithoutGenerationPlaybackOrFailure()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new();
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            Enabled = false,
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };

        sceneTree.Root.AddChild(speechGenerator);
        sceneTree.Root.AddChild(lipSyncPlayer);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.Speak("Hello alley cat");
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(0, speechGenerator.GenerateCallCount);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Null(voice.LastPlayedSpeech);
            Assert.Empty(voice.FailureErrors);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Voice playback must succeed when the generator normalises a non-16000 Hz WAV before the voice consumes it.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Speak_WithGeneratorNormalisingWaveSampleRate_PlaysNormalisedSpeech()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            TargetSampleRate = 16000,
            NextResult = CreateWaveFileBytes([0x00, 0x00, 0x10, 0x00, 0x20, 0x00, 0x30, 0x00], sampleRate: 8000, channelCount: 1, bitsPerSample: 16),
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };

        sceneTree.Root.AddChild(speechGenerator);
        sceneTree.Root.AddChild(lipSyncPlayer);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.Speak("Hello alley cat");
            await WaitUntilAsync(sceneTree, () => voice.LastPlayedSpeech is not null || voice.FailureErrors.Count > 0, 30);

            Assert.Equal(1, speechGenerator.GenerateCallCount);
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
            AudioStreamWav playedSpeech = Assert.IsType<AudioStreamWav>(voice.LastPlayedSpeech);
            Assert.Equal(16000, playedSpeech.MixRate);
            Assert.Equal(16, playedSpeech.Data.Length);
            Assert.Empty(voice.FailureErrors);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    private static byte[] CreateWaveFileBytes(byte[] data, int sampleRate, short channelCount, short bitsPerSample)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        short blockAlign = (short)(channelCount * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
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

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, params Node[] nodes)
    {
        foreach (Node node in nodes)
        {
            node.QueueFree();
        }

        await WaitForFramesAsync(sceneTree, 2);
    }

    private sealed partial class TestAIVoice : AIVoice
    {
        public int PlayGeneratedSpeechCallCount
        {
            get;
            private set;
        }

        public AudioStreamWav? LastPlayedSpeech
        {
            get;
            private set;
        }

        public List<string> FailureErrors { get; } = [];

        protected override void PlayGeneratedSpeech(AudioStreamWav speechStream)
        {
            PlayGeneratedSpeechCallCount++;
            LastPlayedSpeech = speechStream;
        }

        protected override void EmitSpeechFailedSignal(string error)
            => FailureErrors.Add(error);
    }

    private sealed partial class FakeSpeechGenerator : SpeechGenerator
    {
        public Exception? GenerateException
        {
            get;
            set;
        }

        public byte[] NextResult { get; set; } = [];

        public int GenerateCallCount
        {
            get;
            private set;
        }

        protected override Task<byte[]> GenerateCore(string text, string? instruction = null)
        {
            _ = text;
            _ = instruction;
            GenerateCallCount++;

            return GenerateException is not null
                ? Task.FromException<byte[]>(GenerateException)
                : Task.FromResult(NextResult);
        }
    }

    private sealed partial class StubLipSyncPlayer : LipSyncPlayer
    {
        protected override void InitialiseBackend()
        {
        }

        protected override LipSyncInferenceResult RunBackendInference(AudioStreamWav speech)
        {
            _ = speech;
            return new LipSyncInferenceResult([[0f]], ["jawOpen"], 30f);
        }

        protected override void DisposeBackend()
        {
        }
    }
}
