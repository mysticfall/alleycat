using System.Text;
using AlleyCat.Character;
using AlleyCat.Context;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using AlleyCat.Speech.Transcription;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for AI voice orchestration without backend dependencies.
/// </summary>
public sealed partial class AIVoiceIntegrationTests : IDisposable
{
    private readonly PipelineDebugLogFixture _debugLogFixture = new();

    /// <summary>
    /// Clears the isolated pipeline logger override after each test.
    /// </summary>
    public void Dispose() => _debugLogFixture.Dispose();

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
        listener.AddToGroup(new StringName(IHearing.GroupName));
        nonListener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            Assert.Equal("voice_listeners", IHearing.GroupName);
            Assert.True(listener.IsInGroup(new StringName(IHearing.GroupName)));
            Assert.True(nonListener.IsInGroup(new StringName(IHearing.GroupName)));

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
        listener.AddToGroup(new StringName(IHearing.GroupName));
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
            await voice.SpeakAsync("Hello alley cat");
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
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await voice.SpeakAsync("Hello alley cat"));
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
    /// Blank speech is explicitly rejected before generation, playback, or listener notification.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeakAsync_WhenSpeechIsBlank_ThrowsWithoutStartingOutput()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new();
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        RecordingVoiceListener listener = new();

        AddTestNode(sceneTree, speechGenerator);
        AddTestNode(sceneTree, lipSyncPlayer);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            _ = await Assert.ThrowsAsync<ArgumentException>(
                async () => await voice.SpeakAsync(" \t\r\n "));

            Assert.Equal(0, voice.GenerateSpeechAudioCallCount);
            Assert.Equal(0, speechGenerator.GenerateCallCount);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Null(voice.LastPlayedSpeech);
            Assert.Empty(listener.Events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, listener, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Missing required generation and playback dependencies reject speech before any output work starts.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeakAsync_WhenUnconfigured_ThrowsWithoutStartingOutput()
    {
        SceneTree sceneTree = GetSceneTree();
        TestAIVoice voice = new();
        RecordingVoiceListener listener = new();

        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await voice.SpeakAsync("Configured speech text"));

            Assert.Equal(0, voice.GenerateSpeechAudioCallCount);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Null(voice.LastPlayedSpeech);
            Assert.Empty(listener.Events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, listener);
        }
    }

    /// <summary>
    /// Cancellation observed before acceptance must surface without starting any configured voice output work.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeakAsync_WhenAlreadyCancelled_ThrowsWithoutStartingOutput()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new();
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        RecordingVoiceListener listener = new();

        AddTestNode(sceneTree, speechGenerator);
        AddTestNode(sceneTree, lipSyncPlayer);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await voice.SpeakAsync("Cancelled before acceptance", cancellation.Token));
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(0, voice.GenerateSpeechAudioCallCount);
            Assert.Equal(0, speechGenerator.GenerateCallCount);
            Assert.Equal(0, voice.PrepareGeneratedSpeechCallCount);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Null(voice.LastPlayedSpeech);
            Assert.Empty(listener.Events);
            Assert.Empty(voice.FailureErrors);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, listener, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Busy submissions are admitted immediately and generated in exact FIFO order through one serial pipeline.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeakAsync_WhileGenerationIsBusy_AdmitsAndProcessesFIFOWithoutConcurrency()
    {
        SceneTree sceneTree = GetSceneTree();
        TaskCompletionSource<byte[]> generationResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSpeechGenerator speechGenerator = new()
        {
            PendingResult = generationResult,
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
            await voice.SpeakAsync("First request");
            await voice.SpeakAsync("Second request");
            await voice.SpeakAsync("Third request");

            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1, 30);
            Assert.Equal(["First request"], speechGenerator.RequestedTexts);

            _ = generationResult.TrySetResult(
                CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16));
            await WaitUntilAsync(sceneTree, () => voice.SpeechGeneratedCallCount == 3, 60);
            Assert.Equal(["First request", "Second request", "Third request"], speechGenerator.RequestedTexts);
            Assert.Equal(3, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(1, voice.MaximumConcurrentPipelines);
        }
        finally
        {
            _ = generationResult.TrySetCanceled();
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Failure of the first queued item emits failure and does not block the later FIFO item.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeakAsync_WhenFirstQueuedItemFails_ContinuesWithLaterItem()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new();
        speechGenerator.EnqueueFailure(new InvalidOperationException("first failed"));
        speechGenerator.EnqueueResult(
            CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16));
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer
        };
        AddTestNode(sceneTree, speechGenerator);
        AddTestNode(sceneTree, lipSyncPlayer);
        AddTestNode(sceneTree, voice);

        try
        {
            await voice.SpeakAsync("failing first");
            await voice.SpeakAsync("successful second");
            await WaitUntilAsync(
                sceneTree,
                () => voice.FailureErrors.Count == 1 && voice.SpeechGeneratedCallCount == 1,
                60);

            Assert.Equal(["failing first", "successful second"], speechGenerator.RequestedTexts);
            Assert.Equal("first failed", Assert.Single(voice.FailureErrors));
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(1, voice.MaximumConcurrentPipelines);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Caller cancellation after atomic admission does not retract committed speech work.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeakAsync_WhenCancelledAfterAdmission_DoesNotRetractWork()
    {
        SceneTree sceneTree = GetSceneTree();
        TaskCompletionSource<byte[]> generation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSpeechGenerator speechGenerator = new()
        {
            PendingResult = generation
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer
        };
        AddTestNode(sceneTree, speechGenerator);
        AddTestNode(sceneTree, lipSyncPlayer);
        AddTestNode(sceneTree, voice);

        try
        {
            using CancellationTokenSource cancellation = new();
            await voice.SpeakAsync("committed speech", cancellation.Token);
            cancellation.Cancel();
            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1, 30);

            _ = generation.TrySetResult(
                CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16));
            await WaitUntilAsync(sceneTree, () => voice.SpeechGeneratedCallCount == 1, 30);

            Assert.Equal(["committed speech"], speechGenerator.RequestedTexts);
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
        }
        finally
        {
            _ = generation.TrySetCanceled();
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// A refused playback hand-off must still emit the lip-sync preparation entry with the last-known mapped mesh
    /// count rather than zero, because the mapping from the previous utterance persists (SPCH-005 TR-29).
    /// </summary>
    [Fact]
    public async Task SpeakCancellableAsync_WhenHandOffRefused_LipSyncEntryReportsLastKnownMeshCount()
    {
        SceneTree sceneTree = GetSceneTree();
        NotificationLoggingFixture loggingFixture = await NotificationLoggingFixture.CreateAsync(sceneTree);
        Node3D fixtureRoot = new()
        {
            Name = "AIVoiceRefusedHandOffTestRoot",
        };

        try
        {
            AudioStreamPlayer3D audioPlayer = new();
            Skeleton3D skeleton = new();
            skeleton.AddChild(CreateJawOpenMeshInstance());
            FakeSpeechGenerator speechGenerator = new()
            {
                NextResult = CreateWaveFileBytes(
                    new byte[16000],
                    sampleRate: 16000,
                    channelCount: 1,
                    bitsPerSample: 16),
            };
            FakeLipSyncPlayer lipSyncPlayer = new()
            {
                AudioPlayer = audioPlayer,
                Skeleton = skeleton,
            };
            RefusedHandOffTestAIVoice voice = new()
            {
                SpeechGenerator = speechGenerator,
                LipSyncPlayer = lipSyncPlayer,
            };

            fixtureRoot.AddChild(audioPlayer);
            fixtureRoot.AddChild(skeleton);
            fixtureRoot.AddChild(lipSyncPlayer);
            fixtureRoot.AddChild(speechGenerator);
            fixtureRoot.AddChild(voice);
            AddTestNode(sceneTree, fixtureRoot);
            await WaitForFramesAsync(sceneTree, 2);

            // The first utterance commits its hand-off and binds one mesh, so its mapping persists as last-known.
            await voice.SpeakAsync("first utterance");
            await WaitUntilAsync(
                sceneTree,
                () => voice.PlayGeneratedSpeechCallCount == 1 && lipSyncPlayer.MappedMeshCount == 1,
                30);
            await WaitUntilAsync(sceneTree, () => LipSyncPreparedEntries(loggingFixture).Count == 1, 30);
            int lastKnownMeshCount = lipSyncPlayer.MappedMeshCount;
            Assert.True(lastKnownMeshCount > 0, "The fixture must map at least one mesh for the first utterance.");

            // The second utterance is cancelled right before its deferred hand-off dispatch, so preparation has
            // completed but the commit is refused through the real cancellation path.
            using CancellationTokenSource cancellation = new();
            voice.ArmCancellationBeforeNextDispatch(cancellation);
            ValueTask submission = voice.SpeakCancellableAsync("second utterance", cancellation.Token);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(submission.AsTask);
            await WaitUntilAsync(sceneTree, () => LipSyncPreparedEntries(loggingFixture).Count == 2, 30);

            // The refused hand-off must not bind playback or notify listeners, and the mapping stays last-known.
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            Assert.Equal(lastKnownMeshCount, lipSyncPlayer.MappedMeshCount);

            // Both the committed and the refused hand-off entries must report the same mapped mesh count.
            IReadOnlyList<string> entries = LipSyncPreparedEntries(loggingFixture);
            Assert.Equal(2, entries.Count);
            Assert.All(entries, entry => Assert.Contains($"{lastKnownMeshCount} mesh(es)", entry));
            Assert.DoesNotContain(entries, entry => entry.Contains("0 mesh(es)", StringComparison.Ordinal));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixtureRoot);
            await loggingFixture.DestroyAsync(sceneTree);
        }
    }

    /// <summary>
    /// Compatibility Speak observes both synchronous validation and asynchronous production failures.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Speak_CompatibilityAdapter_ReportsValidationAndProductionFailures()
    {
        SceneTree sceneTree = GetSceneTree();
        FakeSpeechGenerator speechGenerator = new()
        {
            GenerateException = new InvalidOperationException("asynchronous production failed"),
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer
        };
        AddTestNode(sceneTree, speechGenerator);
        AddTestNode(sceneTree, lipSyncPlayer);
        AddTestNode(sceneTree, voice);

        try
        {
            voice.Speak("   ");
            voice.Speak("valid but failing");
            await WaitUntilAsync(sceneTree, () => voice.FailureErrors.Count == 2, 60);

            Assert.Contains(voice.FailureErrors, error => error.Contains("blank", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("asynchronous production failed", voice.FailureErrors);
            Assert.Equal(1, speechGenerator.GenerateCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Voice teardown discards queued work and prevents delayed playback or misleading failure signals.
    /// </summary>
    [Fact]
    [Headless]
    public async Task NodeExit_WithActiveAndQueuedSpeech_PreventsPostExitWork()
    {
        SceneTree sceneTree = GetSceneTree();
        TaskCompletionSource<byte[]> generation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSpeechGenerator speechGenerator = new()
        {
            PendingResult = generation
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer
        };
        AddTestNode(sceneTree, speechGenerator);
        AddTestNode(sceneTree, lipSyncPlayer);
        AddTestNode(sceneTree, voice);

        try
        {
            await voice.SpeakAsync("active");
            await voice.SpeakAsync("queued");
            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1, 30);

            voice.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
            _ = generation.TrySetResult(
                CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16));
            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(["active"], speechGenerator.RequestedTexts);
            Assert.Equal(0, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Empty(voice.FailureErrors);
        }
        finally
        {
            _ = generation.TrySetCanceled();
            await DestroyFixtureAsync(sceneTree, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Common-parent teardown cancels active lip-sync preparation before backend disposal or freed-node access.
    /// </summary>
    [Fact]
    [Headless]
    public async Task CommonParentExit_DuringBlockedPreparation_SettlesBeforeBackendDisposal()
    {
        SceneTree sceneTree = GetSceneTree();
        PreparationLifecycleProbe probe = new();
        var commonParent = new Node3D { Name = "VoicePreparationFixture" };
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        var audioPlayer = new AudioStreamPlayer3D { Name = "AudioPlayer" };
        FakeSpeechGenerator speechGenerator = new()
        {
            Name = "Generator",
            NextResult = CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16),
        };
        BlockingPreparationLipSyncPlayer lipSyncPlayer = new()
        {
            Name = "LipSyncPlayer",
            Probe = probe,
            Skeleton = skeleton,
            AudioPlayer = audioPlayer,
        };
        LifecycleTestAIVoice voice = new()
        {
            Name = "Voice",
            Probe = probe,
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        RecordingVoiceListener listener = new();

        commonParent.AddChild(skeleton);
        commonParent.AddChild(audioPlayer);
        commonParent.AddChild(speechGenerator);
        commonParent.AddChild(lipSyncPlayer);
        commonParent.AddChild(voice);
        AddTestNode(sceneTree, commonParent);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            await voice.SpeakAsync("blocked preparation");
            Task pumpSettlement = voice.PumpSettlement;
            await probe.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            commonParent.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(GodotObject.IsInstanceValid(commonParent));
            probe.ReleasePreparation.Set();

            await probe.PreparationSettled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await probe.BackendDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await pumpSettlement.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(1, probe.CancellationObservedCount);
            Assert.Equal(0, probe.BackendAccessAfterExitCount);
            Assert.Equal(0, probe.BackendDisposalRaceCount);
            Assert.Equal(1, probe.BackendDisposalCount);
            Assert.Equal(0, probe.PlaybackCount);
            Assert.Equal(0, probe.ListenerNotificationCount);
            Assert.Empty(probe.FailureErrors);
            Assert.Empty(listener.Events);
        }
        finally
        {
            probe.ReleasePreparation.Set();
            if (GodotObject.IsInstanceValid(commonParent))
            {
                commonParent.QueueFree();
            }

            listener.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Multiple SpeechTool calls queue in order, each committing exactly one owner-stamped observation at playback
    /// hand-off, while pre-hand-off cancellation commits none.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeechTool_WithAIVoice_CommitsOneObservationPerPlaybackHandOff()
    {
        SceneTree sceneTree = GetSceneTree();
        TaskCompletionSource<byte[]> generationResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSpeechGenerator speechGenerator = new()
        {
            PendingResult = generationResult
        };
        StubLipSyncPlayer lipSyncPlayer = new();
        TestAIVoice voice = new()
        {
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer
        };
        TestAIVoice disabledVoice = new()
        {
            Enabled = false,
            SpeechGenerator = speechGenerator,
            LipSyncPlayer = lipSyncPlayer,
        };
        ToolMind acceptedMind = new(voice);
        ToolMind disabledMind = new(disabledVoice);
        SpeechTool tool = new();
        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
        AIFunction activeFunction = tool.CreateFunction(
            new ScenarioContext(acceptedMind.Owner, new ToolSceneContext([acceptedMind.Owner])),
            acceptedMind,
            dispatcher);
        AIFunction disabledFunction = tool.CreateFunction(
            new ScenarioContext(disabledMind.Owner, new ToolSceneContext([disabledMind.Owner])),
            disabledMind,
            dispatcher);

        sceneTree.Root.AddChild(speechGenerator);
        sceneTree.Root.AddChild(lipSyncPlayer);
        sceneTree.Root.AddChild(voice);
        sceneTree.Root.AddChild(disabledVoice);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            // One submission is cancelled mid-generation, before playback hand-off: it commits nothing and reports
            // the non-throwing cut-short result (AI-002 TR-27).
            using CancellationTokenSource cancelledSubmission = new();
            Task<object?> cancelled = activeFunction.InvokeAsync(
                new AIFunctionArguments { ["speech"] = "Cancelled request" },
                cancelledSubmission.Token).AsTask();
            await WaitUntilAsync(sceneTree, () => speechGenerator.GenerateCallCount == 1, 30);
            cancelledSubmission.Cancel();
            string? cancelledResult = await cancelled as string;
            Assert.NotNull(cancelledResult);
            Assert.Contains("cut short", Assert.IsType<string>(cancelledResult), StringComparison.Ordinal);

            // Two further submissions are admitted while generation is still pending, so neither has reached the
            // playback hand-off boundary and neither may commit an observation yet.
            Task<object?> accepted = InvokeSpeechToolAsync(activeFunction, "Accepted request");
            Task<object?> queued = InvokeSpeechToolAsync(activeFunction, "Queued request");
            await WaitUntilAsync(sceneTree, () => voice.IsSpeaking, 30);
            Assert.Empty(acceptedMind.Timeline);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InvokeSpeechToolAsync(disabledFunction, "Disabled request"));

            _ = generationResult.TrySetResult(
                CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16));
            object? acceptedResult = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
            object? queuedResult = await queued.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("Spoken through the configured voice.", acceptedResult);
            Assert.Equal("Spoken through the configured voice.", queuedResult);
            Assert.Equal(
                ["Accepted request", "Queued request"],
                acceptedMind.Timeline.Cast<ObservedSpeech>().Select(observation => observation.Content));
            Assert.All(
                acceptedMind.Timeline.Cast<ObservedSpeech>(),
                observation => Assert.Equal(((IIdentifiable)acceptedMind.Owner).FullId, observation.ActorId));
            Assert.All(
                acceptedMind.Timeline.Cast<ObservedSpeech>(),
                observation => Assert.Null(observation.VoiceId));
            Assert.Empty(disabledMind.Timeline);
            await WaitUntilAsync(sceneTree, () => voice.SpeechGeneratedCallCount == 2, 30);
            // The cancelled item did reach generation before its withdrawal, but never playback hand-off.
            Assert.Equal(["Cancelled request", "Accepted request", "Queued request"], speechGenerator.RequestedTexts);
        }
        finally
        {
            _ = generationResult.TrySetCanceled();
            acceptedMind.Free();
            disabledMind.Free();
            await DestroyFixtureAsync(sceneTree, voice, disabledVoice, speechGenerator, lipSyncPlayer);
        }
    }

    /// <summary>
    /// Invalid, unavailable, ambiguous, failed, and cancelled speech submissions never become observations.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SpeechTool_UncommittedRequests_ProduceNoObservation()
    {
        var acceptingVoice = new ToolFailureVoice();
        var failingVoice = new ToolFailureVoice { Failure = new InvalidOperationException("speech failed") };
        var missingMind = new ToolMind([]);
        var duplicateMind = new ToolMind([acceptingVoice, failingVoice]);
        var failingMind = new ToolMind([failingVoice]);
        var cancelledMind = new ToolMind([acceptingVoice]);
        SpeechTool tool = new();
        IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();

        AIFunction missing = CreateSpeechFunction(tool, missingMind, dispatcher);
        AIFunction duplicate = CreateSpeechFunction(tool, duplicateMind, dispatcher);
        AIFunction failing = CreateSpeechFunction(tool, failingMind, dispatcher);
        AIFunction cancelled = CreateSpeechFunction(tool, cancelledMind, dispatcher);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => InvokeSpeechToolAsync(missing, "   "));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeSpeechToolAsync(missing, "Missing"));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeSpeechToolAsync(duplicate, "Duplicate"));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeSpeechToolAsync(failing, "Failing"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled.InvokeAsync(new AIFunctionArguments { ["speech"] = "Cancelled" }, cancellation.Token).AsTask());

        Assert.Empty(missingMind.Timeline);
        Assert.Empty(duplicateMind.Timeline);
        Assert.Empty(failingMind.Timeline);
        Assert.Empty(cancelledMind.Timeline);
        missingMind.Free();
        duplicateMind.Free();
        failingMind.Free();
        cancelledMind.Free();
        acceptingVoice.Free();
        failingVoice.Free();
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

    private static IReadOnlyList<string> LipSyncPreparedEntries(NotificationLoggingFixture loggingFixture)
        =>
        [
            .. loggingFixture
                .GetPipelineLogMessages()
                .Where(message => message.Contains("TTS lip-sync prepared in", StringComparison.Ordinal)),
        ];

    private static MeshInstance3D CreateJawOpenMeshInstance()
    {
        ArrayMesh mesh = new();
        mesh.AddBlendShape("JawOpen");

        return new MeshInstance3D
        {
            Name = "GeneratedFace",
            Mesh = mesh,
        };
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

    private static async Task<object?> InvokeSpeechToolAsync(AIFunction function, string speech)
        => await function.InvokeAsync(
            new AIFunctionArguments { ["speech"] = speech },
            CancellationToken.None);

    private static AIFunction CreateSpeechFunction(
        SpeechTool tool,
        ToolMind mind,
        IMainThreadDispatcher dispatcher)
        => tool.CreateFunction(
            new ScenarioContext(mind.Owner, new ToolSceneContext([mind.Owner])),
            mind,
            dispatcher);

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

    /// <summary>
    /// Test voice that runs the real production pipeline but can cancel a cancellable submission right before its
    /// deferred playback hand-off dispatch, deterministically landing in the refusal window between completed
    /// preparation and the hand-off commit.
    /// </summary>
    private sealed partial class RefusedHandOffTestAIVoice : AIVoice
    {
        private CancellationTokenSource? _armedCancellation;

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

        public void ArmCancellationBeforeNextDispatch(CancellationTokenSource cancellation)
            => _armedCancellation = cancellation;

        protected override Task DispatchDeferredGodotActionAsync(Action action)
        {
            CancellationTokenSource? armed = Interlocked.Exchange(ref _armedCancellation, null);
            armed?.Cancel();
            return base.DispatchDeferredGodotActionAsync(action);
        }

        protected override void PlayGeneratedSpeech(LipSyncPlayer.PreparedPlayback preparedPlayback)
        {
            PlayGeneratedSpeechCallCount++;
            base.PlayGeneratedSpeech(preparedPlayback);
        }

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            SpeechGeneratedCallCount++;
        }
    }

    private sealed partial class TestAIVoice : AIVoice
    {
        private int _activePipelines;
        public int GenerateSpeechAudioCallCount
        {
            get;
            private set;
        }

        public int PlayGeneratedSpeechCallCount
        {
            get;
            private set;
        }

        public int PrepareGeneratedSpeechCallCount
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

        public int MaximumConcurrentPipelines
        {
            get; private set;
        }

        protected override async Task<byte[]> GenerateSpeechAudioAsync(string speech)
        {
            GenerateSpeechAudioCallCount++;
            EnterPipelineOperation();
            try
            {
                return await base.GenerateSpeechAudioAsync(speech);
            }
            finally
            {
                ExitPipelineOperation();
            }
        }

        protected override Task<LipSyncPlayer.PreparedPlayback> PrepareGeneratedSpeechAsync(
            AudioStreamWav speechStream,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareGeneratedSpeechCallCount++;
            EnterPipelineOperation();
            try
            {
                return Task.FromResult(new LipSyncPlayer.PreparedPlayback(speechStream, [[0f]], ["jawOpen"], 30f));
            }
            finally
            {
                ExitPipelineOperation();
            }
        }

        protected override void PlayGeneratedSpeech(LipSyncPlayer.PreparedPlayback preparedPlayback)
        {
            EnterPipelineOperation();
            try
            {
                PlayGeneratedSpeechCallCount++;
                LastPlayedSpeech = preparedPlayback.Speech;
            }
            finally
            {
                ExitPipelineOperation();
            }
        }

        protected override void OnSpeechGenerated(string speech)
        {
            base.OnSpeechGenerated(speech);
            SpeechGeneratedCallCount++;
        }

        protected override void EmitSpeechFailedSignal(string error)
            => FailureErrors.Add(error);

        private void EnterPipelineOperation()
        {
            _activePipelines++;
            MaximumConcurrentPipelines = Math.Max(MaximumConcurrentPipelines, _activePipelines);
        }

        private void ExitPipelineOperation() => _activePipelines--;
    }

    private sealed partial class LifecycleTestAIVoice : AIVoice
    {
        public PreparationLifecycleProbe Probe { get; set; } = null!;

        protected override void PlayGeneratedSpeech(LipSyncPlayer.PreparedPlayback preparedPlayback)
        {
            _ = preparedPlayback;
            Probe.PlaybackCount++;
        }

        protected override void OnSpeechGenerated(string speech)
        {
            _ = speech;
            Probe.ListenerNotificationCount++;
            base.OnSpeechGenerated(speech);
        }

        protected override void EmitSpeechFailedSignal(string error)
            => Probe.FailureErrors.Add(error);
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

    private sealed partial class RecordingVoiceListener : Node, IHearing
    {
        public List<VoiceListenerEvent> Events { get; } = [];

        public IReadOnlyList<Type> PerceptTypes { get; } = [typeof(SpeechPercept)];

        public event Action<IPercept>? Perceived;

        public void ReceiveVoice(string speech, IVoice source)
        {
            Events.Add(new VoiceListenerEvent(speech, source));
            Perceived?.Invoke(new SpeechPercept(speech, source.Id));
        }
    }

    private sealed record VoiceListenerEvent(string Speech, IVoice Source);

    private sealed partial class ToolMind(IReadOnlyList<IComponent> components) : AlleyCat.Mind.Mind
    {
        public ToolMind(IVoice voice)
            : this([voice])
        {
        }

        public new ToolCharacter Owner
        {
            get;
        } = new ToolCharacter(components);

        public IReadOnlyList<Observation> Timeline => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => Owner;
    }

    private sealed class ToolCharacter(IReadOnlyList<IComponent> components) : ICharacter
    {
        public string Id { get; set; } = "voice_tool_owner";

        public IReadOnlyList<IComponent> Components => components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public IReadOnlyDictionary<string, object?> GetContext(ISceneContext scene, IContextual? observer)
            => new Dictionary<string, object?>();
    }

    private sealed partial class ToolFailureVoice : Voice
    {
        public Exception? Failure
        {
            get; init;
        }

        public override ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(Failure);
        }
    }

    private sealed record ToolSceneContext(IReadOnlyCollection<ICharacter> Characters) : ISceneContext
    {
        public ICharacter Player => throw new InvalidOperationException(
            "Scene context contains no player character. Scene authoring guarantees the player is present.");

        public ContentContext Content => ContentContext.Default;

        public IIdentifiable? Find(string fullId)
            => Characters.FirstOrDefault(character => string.Equals(character.FullId, fullId, StringComparison.Ordinal));

        public IIdentifiable Resolve(string fullId)
            => Find(fullId) ?? throw new InvalidOperationException();
    }

    private sealed partial class FakeSpeechGenerator : SpeechGenerator
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

        public List<string> RequestedTexts { get; } = [];

        public void EnqueueResult(byte[] result)
            => _queuedResults.Enqueue(() => Task.FromResult(result));

        public void EnqueueFailure(Exception exception)
            => _queuedResults.Enqueue(() => Task.FromException<byte[]>(exception));

        protected override Task<byte[]> GenerateCore(string text, string? instruction = null)
        {
            _ = instruction;
            GenerateCallCount++;
            RequestedTexts.Add(text);

            return _queuedResults.TryDequeue(out Func<Task<byte[]>>? result)
                ? result()
                : PendingResult?.Task ?? (GenerateException is not null
                ? Task.FromException<byte[]>(GenerateException)
                : Task.FromResult(NextResult));
        }
    }

    private sealed partial class FakeTranscriber : Transcriber
    {
        public override Task<string> Transcribe(RecordedAudioData recording)
        {
            _ = recording;
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

        protected override LipSyncInferenceResult RunBackendInference(
            AudioStreamWav speech,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = speech;
            return new LipSyncInferenceResult([[0f]], ["jawOpen"], 30f);
        }

        protected override void DisposeBackend()
        {
        }
    }

    private sealed partial class BlockingPreparationLipSyncPlayer : LipSyncPlayer
    {
        public PreparationLifecycleProbe Probe { get; set; } = null!;

        protected override void InitialiseBackend()
        {
        }

        public override void _ExitTree()
        {
            Probe.LifetimeExitStarted = true;
            base._ExitTree();
        }

        protected override LipSyncInferenceResult RunBackendInference(
            AudioStreamWav speech,
            CancellationToken cancellationToken)
        {
            _ = speech;
            _ = Interlocked.Increment(ref Probe.ActiveBackendAccessCount);
            _ = Probe.PreparationStarted.TrySetResult();
            try
            {
                int signalled = WaitHandle.WaitAny(
                    [cancellationToken.WaitHandle, Probe.ReleasePreparation.WaitHandle],
                    TimeSpan.FromSeconds(5));
                if (signalled == 0)
                {
                    _ = Interlocked.Increment(ref Probe.CancellationObservedCount);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (Probe.LifetimeExitStarted)
                {
                    _ = Interlocked.Increment(ref Probe.BackendAccessAfterExitCount);
                }

                return new LipSyncInferenceResult([[0f]], ["jawOpen"], 30f);
            }
            finally
            {
                _ = Interlocked.Decrement(ref Probe.ActiveBackendAccessCount);
                _ = Probe.PreparationSettled.TrySetResult();
            }
        }

        protected override void DisposeBackend()
        {
            if (Volatile.Read(ref Probe.ActiveBackendAccessCount) != 0)
            {
                _ = Interlocked.Increment(ref Probe.BackendDisposalRaceCount);
            }

            _ = Interlocked.Increment(ref Probe.BackendDisposalCount);
            _ = Probe.BackendDisposed.TrySetResult();
        }
    }

    private sealed class PreparationLifecycleProbe
    {
        public ManualResetEventSlim ReleasePreparation { get; } = new(initialState: false);

        public TaskCompletionSource PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PreparationSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BackendDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> FailureErrors { get; } = [];

        public volatile bool LifetimeExitStarted;
        public int ActiveBackendAccessCount;
        public int CancellationObservedCount;
        public int BackendAccessAfterExitCount;
        public int BackendDisposalRaceCount;
        public int BackendDisposalCount;
        public int PlaybackCount;
        public int ListenerNotificationCount;
    }
}
