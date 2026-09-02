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

            // The second item generates and prepares in the background while the first utterance plays; its
            // playback hand-off stays gated until the first playback completes.
            await WaitUntilAsync(
                sceneTree,
                () => speechGenerator.GenerateCallCount == 2 && voice.PrepareGeneratedSpeechCallCount == 2,
                30);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(0, endedCount);

            // The first item's playback ends while the second item was waiting at the gate; the successor hands
            // off now and the window must remain open for its own playback.
            fixture.LipSyncPlayer.CompletePlaybackForTesting();
            await WaitUntilAsync(sceneTree, () => voice.PlayGeneratedSpeechCallCount == 2, 60);
            Assert.True(voice.IsSpeaking);
            Assert.Equal(0, endedCount);

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
    /// Cancelling a prepared-but-waiting successor at the playback gate withdraws it silently while the active
    /// utterance plays to completion (SPCH-005 TR-32).
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_SpeakCancellableAsync_WhenPreparedSuccessorCancelledAtGate_WithdrawsSilently()
    {
        SceneTree sceneTree = GetSceneTree();
        QueuedSpeechGenerator speechGenerator = new()
        {
            NextResult = CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16),
        };
        AIVoiceFixture fixture = await CreateAIVoiceFixtureAsync(sceneTree, speechGenerator);
        WindowTestAIVoice voice = fixture.Voice;

        int endedCount = 0;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            await voice.SpeakAsync("active utterance");
            await WaitUntilAsync(sceneTree, () => voice.PlayGeneratedSpeechCallCount == 1, 30);

            using CancellationTokenSource cancellation = new();
            ValueTask submission = voice.SpeakCancellableAsync("waiting successor", cancellation.Token);
            await WaitUntilAsync(sceneTree, () => voice.PrepareGeneratedSpeechCallCount == 2, 30);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(submission.IsCompleted);
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);

            cancellation.Cancel();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(submission.AsTask);
            await voice.PumpSettlement.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForFramesAsync(sceneTree, 2);

            // The withdrawn successor commits no hand-off, failure signal, or listener notification.
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            Assert.Empty(voice.FailureErrors);
            Assert.Equal("active utterance", Assert.Single(fixture.Listener.Events).Speech);

            // The active utterance is unaffected and plays to completion.
            Assert.True(voice.IsSpeaking);
            Assert.Equal(0, endedCount);
            fixture.LipSyncPlayer.CompletePlaybackForTesting();
            await WaitUntilAsync(sceneTree, () => !voice.IsSpeaking, 30);
            Assert.Equal(1, endedCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture.Root);
        }
    }

    /// <summary>
    /// An explicit cut releases the playback gate so a prepared successor starts immediately, with correct
    /// speaking-window bookkeeping (SPCH-005 TR-32).
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_CutSpeech_ReleasesGateSoPreparedSuccessorStartsImmediately()
    {
        SceneTree sceneTree = GetSceneTree();
        QueuedSpeechGenerator speechGenerator = new()
        {
            NextResult = CreateWaveFileBytes([0x00, 0x00], sampleRate: 16000, channelCount: 1, bitsPerSample: 16),
        };
        AIVoiceFixture fixture = await CreateAIVoiceFixtureAsync(sceneTree, speechGenerator);
        WindowTestAIVoice voice = fixture.Voice;

        int startedCount = 0;
        int endedCount = 0;
        voice.SpeechStarted += _ => startedCount++;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            await voice.SpeakAsync("first utterance");
            await WaitUntilAsync(sceneTree, () => voice.PlayGeneratedSpeechCallCount == 1, 30);

            await voice.SpeakAsync("cut successor");
            await WaitUntilAsync(sceneTree, () => voice.PrepareGeneratedSpeechCallCount == 2, 30);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);

            // Cutting stops the active utterance and must release the gate: the lip-sync player's stop never
            // raises the playback-completed notification the prepared successor is waiting for.
            voice.CutSpeech();
            await WaitUntilAsync(sceneTree, () => voice.PlayGeneratedSpeechCallCount == 2, 30);

            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(0, endedCount);

            fixture.LipSyncPlayer.CompletePlaybackForTesting();
            await WaitUntilAsync(sceneTree, () => !voice.IsSpeaking, 30);

            Assert.Equal(1, startedCount);
            Assert.Equal(1, endedCount);
            Assert.Equal(2, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, fixture.Root);
        }
    }

    /// <summary>
    /// Node teardown while a prepared item waits at the gate settles the pump and the cancellable submission
    /// without a late hand-off, listener notification, or failure signal (SPCH-005 TR-32).
    /// </summary>
    [Fact]
    [Headless]
    public async Task AIVoice_TeardownWhilePreparedItemWaitsAtGate_SettlesWithoutLateHandOff()
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
            await voice.SpeakAsync("active utterance");
            await WaitUntilAsync(sceneTree, () => voice.PlayGeneratedSpeechCallCount == 1, 30);

            using CancellationTokenSource cancellation = new();
            ValueTask submission = voice.SpeakCancellableAsync("waiting successor", cancellation.Token);
            Task pumpSettlement = voice.PumpSettlement;
            await WaitUntilAsync(sceneTree, () => voice.PrepareGeneratedSpeechCallCount == 2, 30);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(submission.IsCompleted);

            voice.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(submission.AsTask);
            await pumpSettlement.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(1, voice.PlayGeneratedSpeechCallCount);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            Assert.Empty(voice.FailureErrors);
            Assert.Equal("active utterance", Assert.Single(fixture.Listener.Events).Speech);
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

    /// <summary>Automatic resume is forwarded as lifecycle metadata without a speech publication.</summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_AutomaticSpeechResumed_ForwardsLifecycleWithoutHearingPublication()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();
        Hearing hearing = new();
        List<(IVoice Source, SpeechSegmentMetadata Metadata)> resumed = [];
        List<SpeechPercept> percepts = [];

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        AddTestNode(sceneTree, hearing);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        voice.SpeechResumed += (source, metadata) => resumed.Add((source, metadata));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitAutomaticSpeechResumed("automatic-group", 1, continued: false);

            (IVoice source, SpeechSegmentMetadata metadata) = Assert.Single(resumed);
            Assert.Same(voice, source);
            Assert.Equal("automatic-group", metadata.SpeechGroupID);
            Assert.Equal(1, metadata.SegmentIndex);
            Assert.True(metadata.Continued);
            Assert.Empty(listener.Events);
            Assert.Empty(percepts);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener, hearing);
        }
    }

    /// <summary>
    /// The automatic qualified onset raises the textless start cue with the real group identity before the
    /// compatibility recording-started window event, without any hearing publication.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_AutomaticOnset_RaisesStartedWithGroupIdentityBeforeWindowEvent()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();
        List<string> order = [];
        List<(IVoice Source, SpeechSegmentMetadata Metadata)> started = [];

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        voice.SpeechSegmentStarted += (source, metadata) =>
        {
            started.Add((source, metadata));
            order.Add("segment-started");
        };
        voice.SpeechStarted += _ => order.Add("window-started");
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitAutomaticGroupOpened("automatic-group");
            transcriber.EmitRecordingStarted();

            (IVoice source, SpeechSegmentMetadata metadata) = Assert.Single(started);
            Assert.Same(voice, source);
            Assert.Equal("automatic-group", metadata.SpeechGroupID);
            Assert.Equal(0, metadata.SegmentIndex);
            Assert.False(metadata.Continued);
            Assert.Equal(["segment-started", "window-started"], order);
            Assert.True(voice.IsSpeaking);
            Assert.Empty(listener.Events);
            Assert.Null(listener.LastBroadcastMetadata);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// A manual recording press raises the textless start cue with a fresh opaque synthetic token before the
    /// window event; blank and failed completions each settle the pending token exactly once, and a manual
    /// completion publishes ungrouped speech.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_ManualPress_RaisesSyntheticStartedAndSettlesBlankAndFailureOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();
        List<string> order = [];
        List<SpeechSegmentMetadata> started = [];
        List<(IVoice Source, SpeechSegmentSettlement Settlement)> settlements = [];

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        voice.SpeechSegmentStarted += (_, metadata) =>
        {
            started.Add(metadata);
            order.Add("segment-started");
        };
        voice.SpeechStarted += _ => order.Add("window-started");
        voice.SpeechSegmentSettled += (source, settlement) => settlements.Add((source, settlement));
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitRecordingStarted();
            SpeechSegmentMetadata blankToken = Assert.Single(started);
            Assert.False(string.IsNullOrWhiteSpace(blankToken.SpeechGroupID));
            Assert.Equal(0, blankToken.SegmentIndex);
            Assert.False(blankToken.Continued);
            Assert.Equal(["segment-started", "window-started"], order);
            Assert.True(voice.IsSpeaking);

            transcriber.EmitTranscriptionCompleted("   ");
            Assert.False(voice.IsSpeaking);
            (IVoice source, SpeechSegmentSettlement settlement) = Assert.Single(settlements);
            Assert.Same(voice, source);
            Assert.Equal(SpeechSegmentSettlementKind.Blank, settlement.Kind);
            Assert.Equal(blankToken, settlement.Metadata);
            Assert.Empty(listener.Events);

            transcriber.EmitRecordingStarted();
            SpeechSegmentMetadata failedToken = Assert.Single(started, metadata => !Equals(metadata, blankToken));
            Assert.NotEqual(blankToken.SpeechGroupID, failedToken.SpeechGroupID);

            transcriber.EmitTranscriptionFailed("Backend unavailable");
            Assert.False(voice.IsSpeaking);
            Assert.Equal(2, settlements.Count);
            Assert.Equal(SpeechSegmentSettlementKind.Failed, settlements[1].Settlement.Kind);
            Assert.Equal(failedToken, settlements[1].Settlement.Metadata);

            // A late duplicate failure for the settled manual session never settles a second time.
            transcriber.EmitTranscriptionFailed("Late backend failure");
            Assert.Equal(2, settlements.Count);
            Assert.Empty(listener.Events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// A manual completion settles the pending synthetic token as Published and its broadcast stays ungrouped.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_ManualCompletion_SettlesSyntheticTokenAndPublishesUngroupedSpeech()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();
        List<string> order = [];
        List<SpeechSegmentMetadata> started = [];
        List<(IVoice Source, SpeechSegmentSettlement Settlement)> settlements = [];

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        voice.SpeechSegmentStarted += (_, metadata) => started.Add(metadata);
        voice.SpeechSegmentSettled += (source, settlement) =>
        {
            settlements.Add((source, settlement));
            order.Add($"settled:{settlement.Kind}");
        };
        listener.ActivityOrder = order;
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitRecordingStarted();
            SpeechSegmentMetadata token = Assert.Single(started);

            transcriber.EmitTranscriptionCompleted("Manual speech");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            (IVoice source, SpeechSegmentSettlement settlement) = Assert.Single(settlements);
            Assert.Same(voice, source);
            Assert.Equal(SpeechSegmentSettlementKind.Published, settlement.Kind);
            Assert.Equal(token, settlement.Metadata);
            Assert.Equal(["broadcast", "settled:Published"], order);
            _ = Assert.Single(listener.Events);
            Assert.Equal("Manual speech", listener.Events[0].Speech);
            Assert.Null(listener.LastBroadcastMetadata);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// Manual pre-emption during an automatic group settles the automatic segment as abandoned before the manual
    /// synthetic start cue, keeping one continuously open public speaking window.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_ManualPreemption_SettlesAutomaticAbandonedBeforeManualStarted()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();
        List<string> order = [];
        List<string> startedTokens = [];
        List<SpeechSegmentSettlement> settlements = [];

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        voice.SpeechSegmentStarted += (_, metadata) =>
        {
            startedTokens.Add(metadata.SpeechGroupID);
            order.Add("segment-started");
        };
        voice.SpeechSegmentSettled += (_, settlement) =>
        {
            settlements.Add(settlement);
            order.Add($"settled:{settlement.Kind}");
        };
        int startedCount = 0;
        int endedCount = 0;
        voice.SpeechStarted += _ => startedCount++;
        voice.SpeechEnded += _ => endedCount++;
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitAutomaticGroupOpened("automatic-group");
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(0, endedCount);

            // A manual press first abandons the automatic group, then announces its own synthetic start.
            transcriber.EmitAutomaticGroupAbandoned("automatic-group");
            transcriber.EmitRecordingStarted();

            Assert.Equal(["automatic-group"], startedTokens[..1]);
            Assert.Equal(SpeechSegmentSettlementKind.Abandoned, Assert.Single(settlements).Kind);
            Assert.Equal("automatic-group", settlements[0].Metadata.SpeechGroupID);
            Assert.Equal(2, startedTokens.Count);
            Assert.NotEqual("automatic-group", startedTokens[1]);
            Assert.Equal(["segment-started", "settled:Abandoned", "segment-started"], order);
            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(0, endedCount);
            Assert.Empty(listener.Events);

            transcriber.EmitTranscriptionCompleted("Manual speech");
            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, endedCount);
            Assert.Equal(
                [SpeechSegmentSettlementKind.Abandoned, SpeechSegmentSettlementKind.Published],
                settlements.Select(settlement => settlement.Kind));
            Assert.Equal(startedTokens[1], settlements[1].Metadata.SpeechGroupID);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// Transcriber teardown, replacement, and the explicit abandonment signal each settle a pending synthetic
    /// token as abandoned exactly once, and a late manual outcome never settles a second time.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_TeardownSwapAndAbandonmentSignal_SettleManualTokenExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        SignalFakeTranscriber replacementTranscriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        List<string> startedTokens = [];
        List<SpeechSegmentSettlement> settlements = [];

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, replacementTranscriber);
        AddTestNode(sceneTree, voice);
        voice.SpeechSegmentStarted += (_, metadata) => startedTokens.Add(metadata.SpeechGroupID);
        voice.SpeechSegmentSettled += (_, settlement) => settlements.Add(settlement);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            // The explicit abandonment signal settles the pending token and closes the silent stop's window.
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);
            transcriber.EmitRecordingAbandoned();
            Assert.False(voice.IsSpeaking);
            Assert.Equal(SpeechSegmentSettlementKind.Abandoned, Assert.Single(settlements).Kind);
            Assert.Equal(startedTokens[0], settlements[0].Metadata.SpeechGroupID);

            transcriber.EmitTranscriptionCompleted("late completion");
            _ = Assert.Single(settlements);
            Assert.Empty(voice.FailureErrors);

            // Swapping the transcriber mid-recording settles the stale token before the new source takes over.
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);
            voice.Transcriber = replacementTranscriber;
            Assert.False(voice.IsSpeaking);
            Assert.Equal(2, settlements.Count);
            Assert.Equal(SpeechSegmentSettlementKind.Abandoned, settlements[1].Kind);
            Assert.Equal(startedTokens[1], settlements[1].Metadata.SpeechGroupID);

            // The disconnected transcriber's late abandonment signal never settles the replacement's token.
            transcriber.EmitRecordingAbandoned();
            Assert.Equal(2, settlements.Count);

            // Node teardown settles the replacement source's pending token exactly once.
            replacementTranscriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);
            voice.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(voice.IsSpeaking);
            Assert.Equal(3, settlements.Count);
            Assert.Equal(SpeechSegmentSettlementKind.Abandoned, settlements[2].Kind);
            Assert.Equal(startedTokens[2], settlements[2].Metadata.SpeechGroupID);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, transcriber, replacementTranscriber);
        }
    }

    /// <summary>
    /// Automatic segment terminals remain textless lifecycle outcomes: publication settles after hearing, while
    /// blank, failure, and manual-pre-emption abandonment settle exactly once without a hearing broadcast.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_AutomaticSegmentSettlements_AreTextlessAndExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();
        List<(IVoice Source, SpeechSegmentSettlement Settlement)> settlements = [];
        List<string> order = [];

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        listener.ActivityOrder = order;
        voice.SpeechSegmentSettled += (source, settlement) =>
        {
            settlements.Add((source, settlement));
            order.Add($"settled:{settlement.Kind}");
        };
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            transcriber.EmitAutomaticGroupOpened("blank");
            transcriber.EmitRecordingStarted();
            transcriber.EmitAutomaticGroupClosed("blank", outcomesWerePublished: false);
            transcriber.EmitAutomaticSegmentCompleted("  ", "blank", 0, continued: false);

            transcriber.EmitAutomaticGroupOpened("failed");
            transcriber.EmitRecordingStarted();
            transcriber.EmitAutomaticGroupClosed("failed", outcomesWerePublished: false);
            transcriber.EmitAutomaticSegmentFailed("backend unavailable", "failed", 0, continued: false);

            transcriber.EmitAutomaticGroupOpened("abandoned");
            transcriber.EmitRecordingStarted();
            transcriber.EmitAutomaticGroupAbandoned("abandoned");
            transcriber.EmitRecordingStarted(); // Manual pre-emption keeps the shared speaking window open.
            transcriber.EmitTranscriptionCompleted("manual completion");

            transcriber.EmitAutomaticGroupOpened("published");
            transcriber.EmitRecordingStarted();
            transcriber.EmitAutomaticGroupClosed("published", outcomesWerePublished: false);
            transcriber.EmitAutomaticSegmentCompleted("published completion", "published", 0, continued: false);

            // The manual pre-emption's synthetic token settles as Published after its ungrouped broadcast, between
            // the automatic group outcomes.
            string manualToken = settlements[3].Settlement.Metadata.SpeechGroupID;
            Assert.NotEqual("abandoned", manualToken);
            Assert.NotEqual("published", manualToken);
            Assert.Equal(0, settlements[3].Settlement.Metadata.SegmentIndex);
            Assert.Equal(
                [
                    SpeechSegmentSettlementKind.Blank,
                    SpeechSegmentSettlementKind.Failed,
                    SpeechSegmentSettlementKind.Abandoned,
                    SpeechSegmentSettlementKind.Published,
                    SpeechSegmentSettlementKind.Published,
                ],
                settlements.Select(entry => entry.Settlement.Kind));
            Assert.Equal(
                ["blank", "failed", "abandoned", manualToken, "published"],
                settlements.Select(entry => entry.Settlement.Metadata.SpeechGroupID));
            Assert.All(settlements, entry => Assert.Same(voice, entry.Source));
            Assert.Equal(["broadcast", "settled:Published"], order.Skip(order.Count - 2));
            Assert.Equal(["manual completion", "published completion"], listener.Events.Select(entry => entry.Speech));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
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

    /// <summary>
    /// The automatic speaking window opens once at the automatic onset's ordinary recording-started signal, stays
    /// continuously open across the utterance, and closes exactly once at the final transcript's broadcast.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_AutomaticUtterance_KeepsWindowOpenThroughUtteranceAndClosesOnceAtFinalBroadcast()
    {
        SceneTree sceneTree = GetSceneTree();
        SignalFakeTranscriber transcriber = new();
        WindowTestPlayerVoice voice = new()
        {
            Transcriber = transcriber,
        };
        OrderingVoiceListener listener = new();
        OrderingVoiceListener ungroupedListener = new();

        AddTestNode(sceneTree, transcriber);
        AddTestNode(sceneTree, voice);
        AddTestNode(sceneTree, listener);
        AddTestNode(sceneTree, ungroupedListener);
        listener.AddToGroup(new StringName(IHearing.GroupName));
        await WaitForFramesAsync(sceneTree, 2);

        List<string> activityOrder = [];
        int startedCount = 0;
        int endedCount = 0;
        voice.SpeechStarted += _ =>
        {
            startedCount++;
            activityOrder.Add("started");
        };
        voice.SpeechEnded += _ => endedCount++;
        listener.ActivityOrder = activityOrder;

        try
        {
            // A locally qualified automatic onset emits the ordinary recording-started signal: it opens the same
            // single public window the manual path uses.
            transcriber.EmitRecordingStarted();

            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(0, endedCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Empty(voice.FailureErrors);
            Assert.Empty(listener.Events);
            Assert.Empty(ungroupedListener.Events);

            transcriber.EmitTranscriptionCompleted("Hello there again, everyone");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, endedCount);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            Assert.Equal(["started", "broadcast"], activityOrder);
            Assert.True(listener.IsSpeakingAtBroadcast.HasValue, "The broadcast did not observe the speaking state.");
            Assert.False(listener.IsSpeakingAtBroadcast.Value);
            _ = Assert.Single(listener.Events);
            Assert.Equal("Hello there again, everyone", listener.Events[0].Speech);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener, ungroupedListener);
        }
    }

    /// <summary>
    /// A blank automatic final transcript closes the speaking window silently: no broadcast, no failure, and no
    /// completed-speech percept.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_BlankAutomaticFinal_ClosesWindowSilentlyWithoutBroadcast()
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

        int endedCount = 0;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);

            transcriber.EmitTranscriptionCompleted("   ");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, endedCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Empty(voice.FailureErrors);
            Assert.Empty(listener.Events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// An automatic finalisation failure closes the speaking window without notifying listeners.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_AutomaticFailure_ClosesWindowWithoutBroadcast()
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

        int endedCount = 0;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);

            transcriber.EmitTranscriptionFailed("Transcription backend unavailable");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, endedCount);
            Assert.Equal(0, voice.SpeechGeneratedCallCount);
            Assert.Empty(listener.Events);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// Player voice teardown during an in-flight automatic utterance closes the window its onset opened.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_TeardownDuringAutomaticUtterance_ClosesWindow()
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
        await DestroyFixtureAsync(sceneTree, transcriber);
    }

    /// <summary>
    /// Manual preemption during an automatic utterance sustains exactly one public speaking window: the silent
    /// automatic abandonment neither closes nor duplicates the window, and the manual session's completion closes it
    /// exactly once.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_ManualPreemptionDuringAutomaticUtterance_SustainsSingleWindowUntilManualClose()
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

        int startedCount = 0;
        int endedCount = 0;
        voice.SpeechStarted += _ => startedCount++;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(0, endedCount);

            // A manual press abandons the automatic utterance without any public terminal for it: the window the
            // automatic onset opened stays continuously open — the manual session's own recording-started signal is
            // idempotent against it — and no second window opens. The abandoned utterance emits no aggregate
            // (the transcriber suite covers that contract), so only the manual session settles the window.
            transcriber.EmitRecordingStarted();

            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(0, endedCount);

            transcriber.EmitTranscriptionCompleted("Manual speech");

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, startedCount);
            Assert.Equal(1, endedCount);
            Assert.Equal(1, voice.SpeechGeneratedCallCount);
            _ = Assert.Single(listener.Events);
            Assert.Equal("Manual speech", listener.Events[0].Speech);
            Assert.True(listener.IsSpeakingAtBroadcast.HasValue, "The broadcast did not observe the speaking state.");
            Assert.False(listener.IsSpeakingAtBroadcast.Value);
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
        }
    }

    /// <summary>
    /// Independently settling automatic groups publish their ordered structured results without allowing an older
    /// group's completion to close the newer group's speaking window.
    /// </summary>
    [Fact]
    [Headless]
    public async Task PlayerVoice_OverlappingAutomaticGroups_KeepWindowOpenUntilEachGroupSettles()
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

        const string firstGroup = "first-group";
        const string secondGroup = "second-group";
        int endedCount = 0;
        voice.SpeechEnded += _ => endedCount++;

        try
        {
            transcriber.EmitAutomaticGroupOpened(firstGroup);
            transcriber.EmitRecordingStarted();
            transcriber.EmitAutomaticGroupOpened(secondGroup);
            transcriber.EmitRecordingStarted();
            Assert.True(voice.IsSpeaking);

            transcriber.EmitAutomaticGroupClosed(firstGroup, outcomesWerePublished: false);
            transcriber.EmitAutomaticSegmentCompleted("first", firstGroup, 0, continued: false);
            Assert.True(voice.IsSpeaking);
            Assert.Equal(0, endedCount);

            transcriber.EmitAutomaticGroupClosed(secondGroup, outcomesWerePublished: false);
            transcriber.EmitAutomaticSegmentCompleted("second", secondGroup, 0, continued: false);

            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, endedCount);
            Assert.Equal(["first", "second"], listener.Events.Select(entry => entry.Speech));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, voice, transcriber, listener);
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

        public List<string> FailureErrors { get; } = [];

        protected override async Task<LipSyncPlayer.PreparedPlayback> PrepareGeneratedSpeechAsync(
            AudioStreamWav speechStream,
            CancellationToken cancellationToken)
        {
            LipSyncPlayer.PreparedPlayback preparedPlayback =
                await base.PrepareGeneratedSpeechAsync(speechStream, cancellationToken);
            PrepareGeneratedSpeechCallCount++;
            return preparedPlayback;
        }

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

    // Internal so the transcriber-side mid-session failure test can reuse this speaking-window fixture.
    internal sealed partial class WindowTestPlayerVoice : PlayerVoice
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

        public void EmitRecordingAbandoned()
            => _ = EmitSignal(SignalName.RecordingAbandoned);

        public void EmitTranscriptionCompleted(string text)
            => _ = EmitSignal(SignalName.TranscriptionCompleted, text);

        public void EmitTranscriptionFailed(string error)
            => _ = EmitSignal(SignalName.TranscriptionFailed, error);

        public void EmitAutomaticGroupOpened(string speechGroupID)
            => _ = EmitSignal(SignalName.AutomaticGroupOpened, speechGroupID);

        public void EmitAutomaticGroupClosed(string speechGroupID, bool outcomesWerePublished)
            => _ = EmitSignal(SignalName.AutomaticGroupClosed, speechGroupID, outcomesWerePublished);

        public void EmitAutomaticSegmentCompleted(string text, string speechGroupID, int segmentIndex, bool continued)
            => _ = EmitSignal(SignalName.AutomaticSegmentCompleted, text, speechGroupID, segmentIndex, continued);

        public void EmitAutomaticSegmentFailed(string error, string speechGroupID, int segmentIndex, bool continued)
            => _ = EmitSignal(SignalName.AutomaticSegmentFailed, error, speechGroupID, segmentIndex, continued);

        public void EmitAutomaticGroupAbandoned(string speechGroupID)
            => _ = EmitSignal(SignalName.AutomaticGroupAbandoned, speechGroupID);

        public void EmitAutomaticSpeechResumed(string speechGroupID, int segmentIndex, bool continued)
            => _ = EmitSignal(SignalName.AutomaticSpeechResumed, speechGroupID, segmentIndex, continued);
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

        public SpeechSegmentMetadata? LastBroadcastMetadata
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

        public void ReceiveVoice(string speech, IVoice source, SpeechSegmentMetadata? metadata)
        {
            LastBroadcastMetadata = metadata;
            ReceiveVoice(speech, source);
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
