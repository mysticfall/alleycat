using System.Text;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using AlleyCat.Speech.Transcription;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for voice speaking-window activity state, window boundaries, and cancellable submissions.
/// </summary>
public sealed partial class VoiceSpeakingWindowIntegrationTests
{
    /// <summary>
    /// The base synchronous path opens the window at admission and closes it before the listener broadcast.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Voice_Speak_OpensWindowAtAdmissionAndClosesItBeforeListenerBroadcast()
    {
        SceneTree sceneTree = GetSceneTree();
        SyncPathTestVoice voice = new();
        OrderingVoiceListener listener = new();

        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        List<string> activityOrder = [];
        voice.SpeechStarted += _ => activityOrder.Add("started");
        voice.SpeechEnded += _ => activityOrder.Add("ended");
        listener.ActivityOrder = activityOrder;

        try
        {
            voice.Speak("Hello window");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            Assert.Equal(["started", "ended", "broadcast"], activityOrder);
            Assert.True(listener.IsSpeakingAtBroadcast.HasValue, "The broadcast did not observe the speaking state.");
            Assert.False(listener.IsSpeakingAtBroadcast.Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, listener);
        }
    }

    /// <summary>
    /// The AI voice window opens at first admission, stays open across queued items, and closes at last playback completion.
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_WindowOpensAtFirstAdmission_StaysOpenAcrossQueuedItems_AndClosesAtLastPlaybackCompletion()
    {
        SceneTree sceneTree = GetSceneTree();
        TaskCompletionSource<byte[]> firstGeneration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueuedSpeechGenerator speechGenerator = new();
        speechGenerator.EnqueueFactory(() => firstGeneration.Task);
        speechGenerator.EnqueueResult(CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16));
        AIVoiceFixture fixture = await CreateAIVoiceFixtureAsync(sceneTree, speechGenerator);
        WindowTestAIVoice voice = fixture.Voice;

        int startedCount = 0;
        int endedCount = 0;
        voice.SpeechStarted += _ => startedCount++;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            await voice.SpeakAsync("first request");
            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);

            await voice.SpeakAsync("second request");
            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);

            _ = firstGeneration.TrySetResult(
                CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16));
            await WaitUntilAsync(sceneTree, () => voice.PlayGeneratedSpeechCallCount == 1, 30);
            Assert.True(voice.IsSpeaking);

            // The first item's playback ends while the second item is still in flight; the window must remain open.
            fixture.LipSyncPlayer.CompletePlaybackForTesting();
            await WaitForFramesAsync(sceneTree, 2);
            Assert.True(voice.IsSpeaking);
            Assert.Equal(0, endedCount);

            await WaitUntilAsync(sceneTree, () => voice.PlayGeneratedSpeechCallCount == 2, 60);
            Assert.True(voice.IsSpeaking);

            fixture.LipSyncPlayer.CompletePlaybackForTesting();
            await WaitUntilAsync(sceneTree, () => !voice.IsSpeaking, 30);

            Assert.Equal(1, startedCount);
            Assert.Equal(1, endedCount);
            Assert.Equal(2, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            _ = firstGeneration.TrySetCanceled();
            await DestroyFixtureAsync(sceneTree, fixture.Root);
        }
    }

    /// <summary>
    /// Failure of the last admitted AI voice item closes the window without notifying listeners.
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_WhenLastAdmittedItemFails_ClosesWindowWithoutListenerNotification()
    {
        SceneTree sceneTree = GetSceneTree();
        QueuedSpeechGenerator speechGenerator = new()
        {
            GenerateException = new InvalidOperationException("speech backend unavailable"),
        };
        AIVoiceFixture fixture = await CreateAIVoiceFixtureAsync(sceneTree, speechGenerator);
        WindowTestAIVoice voice = fixture.Voice;

        int endedCount = 0;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            await voice.SpeakAsync("failing request");
            await WaitUntilAsync(sceneTree, () => voice.FailureErrors.Count == 1 && !voice.IsSpeaking, 30);

            Assert.Equal(1, endedCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Empty(fixture.Listener.Events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture.Root);
        }
    }

    /// <summary>
    /// Pre-hand-off cancellation of an explicitly cancellable submission aborts silently with a closed window.
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_SpeakCancellableAsync_WhenCancelledBeforeHandOff_AbortsSilently()
    {
        SceneTree sceneTree = GetSceneTree();
        TaskCompletionSource<byte[]> generation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueuedSpeechGenerator speechGenerator = new()
        {
            PendingResult = generation,
        };
        AIVoiceFixture fixture = await CreateAIVoiceFixtureAsync(sceneTree, speechGenerator);
        WindowTestAIVoice voice = fixture.Voice;

        int endedCount = 0;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            using CancellationTokenSource cancellation = new();
            ValueTask submission = voice.SpeakCancellableAsync("cancellable speech", cancellation.Token);
            Assert.True(voice.IsSpeaking);
            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1, 30);
            Assert.False(submission.IsCompleted);

            cancellation.Cancel();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(submission.AsTask);
            await voice.PumpSettlement.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForFramesAsync(sceneTree, 2);

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, endedCount);
            Assert.Empty(voice.FailureErrors);
            Assert.Empty(fixture.Listener.Events);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            _ = generation.TrySetCanceled();
            await DestroyFixtureAsync(sceneTree, fixture.Root);
        }
    }

    /// <summary>
    /// The explicitly cancellable submission completes at playback hand-off and later cancellation never retracts it.
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_SpeakCancellableAsync_CompletesAtPlaybackHandOff_AndPostHandOffCancellationDoesNotRetract()
    {
        SceneTree sceneTree = GetSceneTree();
        QueuedSpeechGenerator speechGenerator = new()
        {
            NextResult = CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16),
        };
        AIVoiceFixture fixture = await CreateAIVoiceFixtureAsync(sceneTree, speechGenerator);
        WindowTestAIVoice voice = fixture.Voice;

        try
        {
            using CancellationTokenSource cancellation = new();
            ValueTask submission = voice.SpeakCancellableAsync("committed speech", cancellation.Token);

            await WaitUntilAsync(
                sceneTree,
                () => voice.PlayGeneratedSpeechCallCount == 1 && submission.IsCompletedSuccessfully,
                30);
            Assert.True(voice.IsSpeaking);
            _ = Assert.Single(fixture.Listener.Events);

            cancellation.Cancel();
            await WaitForFramesAsync(sceneTree, 3);
            Assert.True(submission.IsCompletedSuccessfully);
            Assert.True(voice.IsSpeaking);
            Assert.Empty(voice.FailureErrors);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);

            fixture.LipSyncPlayer.CompletePlaybackForTesting();
            await WaitUntilAsync(sceneTree, () => !voice.IsSpeaking, 30);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            _ = Assert.Single(fixture.Listener.Events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture.Root);
        }
    }

    /// <summary>
    /// Teardown during an active cancellable generation closes the window and settles the submission as cancellation.
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_TeardownDuringCancellableGeneration_ClosesWindowAndSettlesSubmission()
    {
        SceneTree sceneTree = GetSceneTree();
        TaskCompletionSource<byte[]> generation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueuedSpeechGenerator speechGenerator = new()
        {
            PendingResult = generation,
        };
        AIVoiceFixture fixture = await CreateAIVoiceFixtureAsync(sceneTree, speechGenerator);
        WindowTestAIVoice voice = fixture.Voice;

        try
        {
            using CancellationTokenSource cancellation = new();
            ValueTask submission = voice.SpeakCancellableAsync("teardown speech", cancellation.Token);
            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1, 30);
            Assert.True(voice.IsSpeaking);

            voice.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(submission.AsTask);

            Assert.False(voice.IsSpeaking);
            Assert.Empty(voice.FailureErrors);
        }
        finally
        {
            _ = generation.TrySetCanceled();
            await DestroyFixtureAsync(sceneTree, fixture.Root);
        }
    }

    /// <summary>
    /// The player voice window opens from the transcriber's recording-started signal and closes at the broadcast.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_RecordingStarted_OpensWindow_AndNonBlankTranscriptionClosesAtBroadcast()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        List<string> activityOrder = [];
        voice.SpeechStarted += _ => activityOrder.Add("started");
        voice.SpeechEnded += _ => activityOrder.Add("ended");
        listener.ActivityOrder = activityOrder;

        try
        {
            transcriber.EmitRecordingStarted();

            Assert.True(voice.IsSpeaking);

            transcriber.EmitTranscriptionCompleted("Hello from the player");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            Assert.Equal(["started", "ended", "broadcast"], activityOrder);
            Assert.True(listener.IsSpeakingAtBroadcast.HasValue, "The broadcast did not observe the speaking state.");
            Assert.False(listener.IsSpeakingAtBroadcast.Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// Blank transcripts close the player voice window so an abandoned recording cannot mute attending minds.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_OnBlankTranscription_ClosesWindowWithoutSpeech()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);

            transcriber.EmitTranscriptionCompleted("   ");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber);
        }
    }

    /// <summary>
    /// Transcription failures close the player voice window opened at recording start.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_OnTranscriptionFailure_ClosesWindow()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);

            transcriber.EmitTranscriptionFailed("Backend unavailable");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber);
        }
    }

    /// <summary>
    /// A disabled player voice still closes its window after transcription so the turn-taking gate cannot jam.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_WhenDisabled_ClosesWindowAfterTranscription()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Enabled = false,
            Transcriber = transcriber,
        };

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);

            transcriber.EmitTranscriptionCompleted("Muted player speech");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            _ = Assert.Single(voice.FailureErrors);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber);
        }
    }

    /// <summary>
    /// Player voice teardown closes the window opened by an in-progress recording.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_TeardownWhileRecording_ClosesWindow()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        await WaitForFramesAsync(sceneTree, 2);

        transcriber.EmitRecordingStarted();
        Assert.True(voice.IsSpeaking);

        voice.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);

        Assert.False(voice.IsSpeaking);
    }

    /// <summary>
    /// Swapping the transcriber mid-recording disconnects the old source, so the window it opened must close instead
    /// of waiting forever for a completion signal that can never arrive.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_WhenTranscriberSwappedMidRecording_ClosesWindow()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        SignalFakeTranscriber replacementTranscriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, replacementTranscriber);
        AddTestNode(sceneTree, voice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);

            voice.Transcriber = replacementTranscriber;

            Assert.False(voice.IsSpeaking);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);

            // The replacement source must be able to open a fresh window.
            replacementTranscriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);
            replacementTranscriber.EmitTranscriptionCompleted("Replacement speech");
            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, replacementTranscriber);
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

    private static async Task<AIVoiceFixture> CreateAIVoiceFixtureAsync(
        SceneTree sceneTree,
        QueuedSpeechGenerator speechGenerator)
    {
        AudioStreamPlayer3D audioPlayer = new()
        {
            Name = "AudioPlayer",
        };
        Skeleton3D skeleton = new()
        {
            Name = "Skeleton",
        };
        FakeLipSyncPlayer lipSyncPlayer = new()
        {
            Name = "LipSyncPlayer",
            AudioPlayer = audioPlayer,
            Skeleton = skeleton,
        };
        WindowTestAIVoice voice = new()
        {
            Name = "AIVoice",
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        OrderingVoiceListener listener = new()
        {
            Name = "Listener",
        };

        Node3D root = new()
        {
            Name = "AIVoiceWindowFixture",
        };
        root.AddChild(audioPlayer);
        root.AddChild(skeleton);
        root.AddChild(speechGenerator);
        root.AddChild(lipSyncPlayer);
        root.AddChild(voice);
        root.AddChild(listener);
        AddTestNode(sceneTree, root);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        return new AIVoiceFixture(voice, speechGenerator, lipSyncPlayer, listener, root);
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

    private sealed record AIVoiceFixture(
        WindowTestAIVoice Voice,
        QueuedSpeechGenerator SpeechGenerator,
        FakeLipSyncPlayer LipSyncPlayer,
        OrderingVoiceListener Listener,
        Node3D Root);

    private sealed partial class SyncPathTestVoice : Voice
    {
        public int SpeechGeneratedCallCount
        {
            get;
            private set;
        }

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            SpeechGeneratedCallCount++;
        }
    }

    private sealed partial class WindowTestAIVoice : AIVoice
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

        public List<string> FailureErrors { get; } = [];

        protected override void PlayGeneratedSpeech(LipSyncPlayer.PreparedPlayback preparedPlayback)
        {
            _ = preparedPlayback;
            PlayGeneratedSpeechCallCount++;
        }

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            SpeechGeneratedCallCount++;
        }

        protected override void EmitSpeechFailedSignal(string error)
            => FailureErrors.Add(error);
    }

    private sealed partial class WindowTestPlayerVoice : PlayerVoice
    {
        public int SpeechGeneratedCallCount
        {
            get;
            private set;
        }

        public List<string> FailureErrors { get; } = [];

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            SpeechGeneratedCallCount++;
        }

        protected override void EmitSpeechFailedSignal(string error)
            => FailureErrors.Add(error);
    }

    private sealed partial class SignalFakeTranscriber : Transcriber
    {
        public override Task<string> Transcribe(RecordedAudioData recording)
        {
            _ = recording;
            return Task.FromResult(string.Empty);
        }

        public void EmitRecordingStarted()
            => _ = EmitSignal(SignalName.RecordingStarted);

        public void EmitTranscriptionCompleted(string text)
            => _ = EmitSignal(SignalName.TranscriptionCompleted, text);

        public void EmitTranscriptionFailed(string error)
            => _ = EmitSignal(SignalName.TranscriptionFailed, error);
    }

    private sealed partial class OrderingVoiceListener : Node, IHearing
    {
        private readonly List<ListenerEvent> _events = [];

        public List<string>? ActivityOrder
        {
            get;
            set;
        }

        public bool? IsSpeakingAtBroadcast
        {
            get;
            private set;
        }

        public IReadOnlyList<ListenerEvent> Events => _events;

        public IReadOnlyList<Type> PerceptTypes { get; } = [typeof(SpeechPercept)];

#pragma warning disable CS0067
        public event Action<IPercept>? Perceived;
#pragma warning restore CS0067

        public void ReceiveVoice(string speech, IVoice source)
        {
            IsSpeakingAtBroadcast = source.IsSpeaking;
            ActivityOrder?.Add("broadcast");
            _events.Add(new ListenerEvent(speech, source));
        }
    }

    private sealed record ListenerEvent(string Speech, IVoice Source);

    private sealed partial class QueuedSpeechGenerator : SpeechGenerator
    {
        private readonly Queue<Func<Task<byte[]>>> _queuedResults = [];

        public Exception? GenerateException
        {
            get;
            set;
        }

        public byte[] NextResult { get; set; } = [];

        public TaskCompletionSource<byte[]>? PendingResult
        {
            get;
            set;
        }

        public int GenerateCallCount
        {
            get;
            private set;
        }

        public void EnqueueFactory(Func<Task<byte[]>> resultFactory)
            => _queuedResults.Enqueue(resultFactory);

        public void EnqueueResult(byte[] result)
            => _queuedResults.Enqueue(() => Task.FromResult(result));

        protected override Task<byte[]> GenerateCore(string text, string? instruction = null)
        {
            _ = instruction;
            GenerateCallCount++;

            return _queuedResults.TryDequeue(out Func<Task<byte[]>>? result)
                ? result()
                : PendingResult?.Task ?? (GenerateException is not null
                ? Task.FromException<byte[]>(GenerateException)
                : Task.FromResult(NextResult));
        }
    }
}
