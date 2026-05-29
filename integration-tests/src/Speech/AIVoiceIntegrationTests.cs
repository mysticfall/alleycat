using System.Text;
using AlleyCat.Body.Voice;
using AlleyCat.Core;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using AlleyCat.Speech.Transcription;
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
    /// The abstract base voice contract must remain locatable in 3D space and expose an authoring Id through subclasses.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Voice_TestSubclass_IsNode3DWithEmptyIdAndIVoice()
    {
        SceneTree sceneTree = GetSceneTree();
        TestVoice voice = new();

        AddTestNode(sceneTree, voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            Node3D node = Assert.IsAssignableFrom<Node3D>(voice);
            IVoice voiceComponent = Assert.IsAssignableFrom<IVoice>(voice);
            Assert.Same(voice, node);
            Assert.Same(voice, voiceComponent);
            Assert.Equal(string.Empty, voice.Id);
            Assert.Equal(voice.GlobalPosition, voiceComponent.Origin);

            voice.GlobalPosition = new Vector3(1.5f, 2.25f, -3.75f);
            voice.Id = "reference-head-voice";

            Assert.Equal("reference-head-voice", voice.Id);
            Assert.Equal(voice.GlobalPosition, voiceComponent.Origin);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice);
        }
    }

    /// <summary>
    /// The base voice placeholder hook must only fire when voice output is enabled.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Voice_Speak_InvokesPostGenerationHookOnlyWhenEnabled()
    {
        SceneTree sceneTree = GetSceneTree();
        TestVoice voice = new();

        AddTestNode(sceneTree, voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.Speak("Hello");
            voice.Enabled = false;
            voice.Speak("Muted");

            Assert.Equal(1, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice);
        }
    }

    /// <summary>
    /// Generated speech must notify grouped voice listeners with speech and source voice details.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Voice_Speak_NotifiesGroupedVoiceListenersWithSpeechAndSource()
    {
        SceneTree sceneTree = GetSceneTree();
        TestVoice voice = new();
        RecordingVoiceListener listener = new();
        Node nonListener = new();

        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        AddTestNode(sceneTree, nonListener);
        await WaitForFramesAsync(sceneTree, 2);
        listener.AddToGroup(new StringName(IVoiceListener.GroupName));
        nonListener.AddToGroup(new StringName(IVoiceListener.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            Assert.Equal("voice_listeners", IVoiceListener.GroupName);
            Assert.True(listener.IsInGroup(new StringName(IVoiceListener.GroupName)));
            Assert.True(nonListener.IsInGroup(new StringName(IVoiceListener.GroupName)));

            Assert.True(voice.IsInsideTree());

            voice.Speak("Hello listener");

            VoiceListenerEvent listenerEvent = Assert.Single(listener.Events);
            Assert.Equal("Hello listener", listenerEvent.Speech);
            Assert.Same(voice, listenerEvent.Source);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, listener, nonListener);
        }
    }

    /// <summary>
    /// Disabled voice output must not notify grouped voice listeners.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Voice_Speak_WhenDisabled_DoesNotNotifyGroupedVoiceListeners()
    {
        SceneTree sceneTree = GetSceneTree();
        TestVoice voice = new()
        {
            Enabled = false,
        };
        RecordingVoiceListener listener = new();

        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        await WaitForFramesAsync(sceneTree, 2);
        listener.AddToGroup(new StringName(IVoiceListener.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.Speak("Muted listener");

            Assert.Empty(listener.Events);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, listener);
        }
    }

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
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
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
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
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
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
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
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
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
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
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

    /// <summary>
    /// Player voice remains a locatable voice node while consuming transcription results.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_DefaultInstance_IsVoiceAndNode3D()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerVoice voice = new();

        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            Voice baseVoice = Assert.IsAssignableFrom<Voice>(voice);
            IVoice voiceComponent = Assert.IsAssignableFrom<IVoice>(voice);
            Node3D node = Assert.IsAssignableFrom<Node3D>(voice);
            Assert.Same(voice, baseVoice);
            Assert.Same(voice, voiceComponent);
            Assert.Same(voice, node);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice);
        }
    }

    /// <summary>
    /// Non-empty transcription results must be forwarded through the inherited voice contract.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_OnNonEmptyTranscriptionCompletion_InvokesPostGenerationHookOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeTranscriber transcriber = new();
        TestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        sceneTree.Root.AddChild(transcriber);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.HandleTestTranscriptionCompleted("Hello from the player");
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(1, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber);
        }
    }

    /// <summary>
    /// Empty or whitespace-only transcription results must be ignored.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_OnBlankTranscriptionCompletion_DoesNotInvokePostGenerationHook()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeTranscriber transcriber = new();
        TestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        sceneTree.Root.AddChild(transcriber);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.HandleTestTranscriptionCompleted(string.Empty);
            voice.HandleTestTranscriptionCompleted("   ");
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(0, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber);
        }
    }

    /// <summary>
    /// Player voice must not bypass the base Enabled guard when consuming transcription results.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_WhenDisabled_DoesNotInvokePostGenerationHookForTranscriptionCompletion()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeTranscriber transcriber = new();
        TestPlayerVoice voice = new()
        {
            Enabled = false,
            Transcriber = transcriber,
        };
        sceneTree.Root.AddChild(transcriber);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            voice.HandleTestTranscriptionCompleted("Muted player speech");
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(0, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber);
        }
    }

    /// <summary>
    /// Player voice must disconnect from the transcriber when it leaves the tree.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_AfterExitTree_DoesNotReceiveTranscriptionCompletions()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeTranscriber transcriber = new();
        TestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        sceneTree.Root.AddChild(transcriber);
        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);
        voice.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);

        transcriber.EmitTranscriptionCompleted("Ignored after exit");
        await WaitForFramesAsync(sceneTree, 2);

        Assert.Equal(0, voice.SpeechGeneratedCallCount);
    }

    /// <summary>
    /// Holder traits must resolve a single composed voice capability through component helper extensions.
    /// </summary>
    [Fact]
    [Headless]
    public async Task IHasVoice_ResolvesSingleVoiceComponent()
    {
        SceneTree sceneTree = GetSceneTree();
        TestVoice voice = new();
        IHasVoice holder = new TestVoiceHolder(voice);

        sceneTree.Root.AddChild(voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            Assert.True(holder.TryGetVoice(out IVoice? resolvedVoice));
            Assert.Same(voice, resolvedVoice);
            Assert.Same(voice, holder.RequireVoice());
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice);
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

    private static void AddTestNode(SceneTree sceneTree, Node node)
    {
        Node parent = sceneTree.CurrentScene ?? sceneTree.Root;
        parent.AddChild(node);
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

    private sealed partial class TestVoice : Voice
    {
        public int SpeechGeneratedCallCount
        {
            get;
            private set;
        }

        public string? LastGeneratedSpeech
        {
            get;
            private set;
        }

        public override void Speak(string speech)
            => _ = TryNotifySpeechGeneratedWhenEnabled(speech);

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            LastGeneratedSpeech = speech;
            SpeechGeneratedCallCount++;
        }
    }

    private sealed class TestVoiceHolder(params IVoice[] voices) : IHasVoice
    {
        public IReadOnlyList<IComponent> Components { get; } = voices;
    }

    private sealed partial class TestAIVoice : AIVoice
    {
        public int PlayGeneratedSpeechCallCount
        {
            get;
            private set;
        }

        public int SpeechGeneratedCallCount
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

        protected override Task<LipSyncPlayer.PreparedPlayback> PrepareGeneratedSpeechAsync(AudioStreamWav speechStream)
            => Task.FromResult(new LipSyncPlayer.PreparedPlayback(speechStream, [[0f]], ["jawOpen"], 30f));

        protected override void PlayGeneratedSpeech(LipSyncPlayer.PreparedPlayback preparedPlayback)
        {
            PlayGeneratedSpeechCallCount++;
            LastPlayedSpeech = preparedPlayback.Speech;
        }

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            SpeechGeneratedCallCount++;
        }

        protected override void EmitSpeechFailedSignal(string error)
            => FailureErrors.Add(error);
    }

    private sealed partial class TestPlayerVoice : PlayerVoice
    {
        public int SpeechGeneratedCallCount
        {
            get;
            private set;
        }

        public void HandleTestTranscriptionCompleted(string text) => OnTranscriptionCompleted(text);

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            SpeechGeneratedCallCount++;
        }
    }

    private sealed partial class RecordingVoiceListener : Node, IVoiceListener
    {
        public List<VoiceListenerEvent> Events { get; } = [];

        public void ReceiveVoice(string speech, IVoice source)
            => Events.Add(new VoiceListenerEvent(speech, source));
    }

    private sealed record VoiceListenerEvent(string Speech, IVoice Source);

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

    private sealed partial class FakeTranscriber : Transcriber
    {
        public override Task<string> Transcribe(AudioStreamWav audioStream)
        {
            _ = audioStream;
            return Task.FromResult(string.Empty);
        }

        public void EmitTranscriptionCompleted(string text)
            => _ = EmitSignal(SignalName.TranscriptionCompleted, text);
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
