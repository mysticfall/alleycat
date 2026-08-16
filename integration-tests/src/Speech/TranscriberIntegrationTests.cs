using System.Diagnostics;
using System.Reflection;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Speech.Transcription;
using AlleyCat.UI;
using AlleyCat.XR;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for transcription completion and failure orchestration.
/// </summary>
public sealed partial class TranscriberIntegrationTests : IDisposable
{
    private readonly AIPipelineDebugLogFixture _debugLogFixture = new();

    private static readonly MethodInfo _invokeTranscriptionAsyncMethod = typeof(Transcriber)
        .GetMethod("InvokeTranscriptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected Transcriber.InvokeTranscriptionAsync for runtime speech tests.");

    /// <summary>
    /// Clears the isolated AI pipeline logger override after each test.
    /// </summary>
    public void Dispose() => _debugLogFixture.Dispose();

    /// <summary>
    /// Verifies successful transcription emits the completion signal without posting transcript notifications by default.
    /// </summary>
    [Fact]
    public async Task InvokeTranscriptionAsync_OnSuccess_EmitsCompletionSignal_DoesNotPostTranscriptByDefault_AndResetsLifecycleState()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        (Node global, NotificationWidget notificationWidget) = await CreateNotificationHostAsync(sceneTree);
        int godotThreadId = System.Environment.CurrentManagedThreadId;
        List<(TranscriberLifecycleState State, bool Value, int ThreadId)> lifecycleTransitions = [];
        FakeTranscriber transcriber = new()
        {
            NextResultFactory = _ => Task.FromResult("Transcript Ready"),
            LifecycleStateChangedForTesting = (state, value, threadId) =>
                lifecycleTransitions.Add((state, value, threadId)),
        };

        sceneTree.Root.AddChild(transcriber);
        await WaitForFramesAsync(sceneTree, 2);

        string? completedText = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(text =>
            {
                completedCount++;
                completedText = text;
            }));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(_ => failedCount++));

        try
        {
            await InvokeTranscriptionAsync(transcriber);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(1, completedCount);
            Assert.Equal("Transcript Ready", completedText);
            Assert.Equal(0, failedCount);
            Assert.False(notificationWidget.Visible);
            Assert.Empty(GetNotificationTexts(notificationWidget));
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, true, _));
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, false, _));
            Assert.All(lifecycleTransitions, transition => Assert.Equal(godotThreadId, transition.ThreadId));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, transcriber, global);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies opt-in transcript notifications are posted before synchronous completion listeners run.
    /// </summary>
    [Fact]
    public async Task InvokeTranscriptionAsync_OnSuccess_PostsTranscriptBeforeCompletionSignalListeners()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        (Node global, NotificationWidget notificationWidget) = await CreateNotificationHostAsync(sceneTree);
        FakeTranscriber transcriber = new()
        {
            NextResultFactory = _ => Task.FromResult("Prompt Transcript"),
            TranscriptNotificationEnabled = true,
        };

        sceneTree.Root.AddChild(transcriber);
        await WaitForFramesAsync(sceneTree, 2);

        bool notificationPostedBeforeListener = false;
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(_ => notificationPostedBeforeListener = HasNotification(notificationWidget, "Prompt Transcript")));

        try
        {
            await InvokeTranscriptionAsync(transcriber);
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(notificationPostedBeforeListener);
            Assert.True(notificationWidget.Visible);
            Assert.Equal("Prompt Transcript", GetNewestNotificationText(notificationWidget));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, transcriber, global);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies failed transcription emits the failure signal without posting a direct UI notification.
    /// </summary>
    [Fact]
    public async Task InvokeTranscriptionAsync_OnFailure_EmitsFailureSignal_DoesNotPostNotification_AndResetsLifecycleState()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        (Node global, NotificationWidget notificationWidget) = await CreateNotificationHostAsync(sceneTree);
        int dispatchingThreadId = System.Environment.CurrentManagedThreadId;
        int? backendInvocationThreadId = null;
        List<(TranscriberLifecycleState State, bool Value, int ThreadId)> lifecycleTransitions = [];
        FakeTranscriber transcriber = new()
        {
            NextResultFactory = async _ =>
            {
                backendInvocationThreadId = System.Environment.CurrentManagedThreadId;
                await Task.Run(static () => { });
                throw new InvalidOperationException("Backend unavailable");
            },
            LifecycleStateChangedForTesting = (state, value, threadId) =>
                lifecycleTransitions.Add((state, value, threadId)),
        };

        sceneTree.Root.AddChild(transcriber);
        await WaitForFramesAsync(sceneTree, 2);

        string? failureText = null;
        int? failureSignalThreadId = null;
        int completedCount = 0;
        int failedCount = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(_ => completedCount++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(error =>
            {
                failedCount++;
                failureText = error;
                failureSignalThreadId = System.Environment.CurrentManagedThreadId;
            }));

        try
        {
            await InvokeTranscriptionAsync(transcriber);
            await WaitForNextFrameAsync(sceneTree);

            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(0, completedCount);
            Assert.Equal(1, failedCount);
            Assert.Equal("Backend unavailable", failureText);
            Assert.True(backendInvocationThreadId.HasValue);
            Assert.NotEqual(dispatchingThreadId, backendInvocationThreadId);
            Assert.Equal(dispatchingThreadId, failureSignalThreadId);
            Assert.False(notificationWidget.Visible);
            Assert.Empty(GetNotificationTexts(notificationWidget));
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, true, _));
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, false, _));
            Assert.All(lifecycleTransitions, transition => Assert.Equal(dispatchingThreadId, transition.ThreadId));
        }
        finally
        {
            await DestroyFixtureAsync(sceneTree, transcriber, global);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies the public recording-started signal fires exactly once per recording session on the Godot thread.
    /// </summary>
    [Fact]
    public async Task XRRecordButton_OnRecordingStart_EmitsRecordingStartedExactlyOncePerRecordingOnGodotThread()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);
        int godotThreadId = System.Environment.CurrentManagedThreadId;

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            fixture.Transcriber.RecordButton = new StringName("speech_record");

            int startedCount = 0;
            List<int> signalThreadIds = [];
            _ = transcriber.Connect(
                Transcriber.SignalName.RecordingStarted,
                Callable.From(() =>
                {
                    startedCount++;
                    signalThreadIds.Add(System.Environment.CurrentManagedThreadId);
                }));

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(fixture.Transcriber.IsRecording);
            Assert.Equal(1, startedCount);
            Assert.Single(signalThreadIds, godotThreadId);

            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 1,
                maxFrames: 60);

            // Stop, finalisation, and transcription must not re-emit the recording-started signal.
            Assert.Equal(1, startedCount);

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(fixture.Transcriber.IsRecording);
            Assert.Equal(2, startedCount);

            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 2,
                maxFrames: 60);

            Assert.Equal(2, startedCount);
            Assert.All(signalThreadIds, threadId => Assert.Equal(godotThreadId, threadId));
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies the configured XR record button starts recording on press and stops/transcribes on release.
    /// </summary>
    [Fact]
    public async Task XRRecordButton_OnConfiguredPressAndRelease_StartsRecording_StopsRecording_AndTranscribes()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            fixture.Transcriber.RecordButton = new StringName("speech_record");

            fixture.LeftController.TriggerActionButtonPressed("other_action");
            await WaitForNextFrameAsync(sceneTree);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.Equal(0, transcriber.TranscribeCallCount);

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(fixture.Transcriber.IsRecording);
            Assert.Equal(0, transcriber.TranscribeCallCount);

            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 1,
                maxFrames: 30);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(1, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies capture buffers are cleared and reused safely across consecutive recording sessions.
    /// </summary>
    [Fact]
    public async Task XRRecordButton_OnTwoRecordingSessions_TranscribesIndependentManagedAudio()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        List<RecordedAudioData> recordings = [];
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 1);
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            NextResultFactory = recording =>
            {
                recordings.Add(recording);
                return Task.FromResult("Repeated transcript");
            },
            AudioCaptureForTesting = capture,
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            transcriber.RecordButton = new StringName("speech_record");
            for (int session = 0; session < 2; session++)
            {
                capture.FrameValue = session == 0 ? new Vector2(1f, 0.5f) : new Vector2(-1f, -0.5f);
                fixture.LeftController.TriggerActionButtonPressed("speech_record");
                await WaitForNextFrameAsync(sceneTree);
                fixture.LeftController.TriggerActionButtonReleased("speech_record");
                await WaitUntilAsync(
                    sceneTree,
                    () => !transcriber.IsRecording
                        && !transcriber.IsTranscribing
                        && transcriber.TranscribeCallCount == session + 1,
                    maxFrames: 60);
            }

            Assert.Collection(
                recordings,
                first => Assert.Equal([0xff, 0x5f], first.PCMData.ToArray()),
                second => Assert.Equal([0x01, 0xa0], second.PCMData.ToArray()));
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies frames published by the final audio mix after stop are included by one bounded deferred drain.
    /// </summary>
    [Fact]
    public async Task StopRecording_WhenCapturePublishesAfterStop_IncludesLateFramesInExactlyOneFinalRead()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0)
        {
            FrameValue = new Vector2(0.5f, -0.5f),
        };
        FakeAudioMixClock mixClock = new();
        RecordedAudioData? observedRecording = null;
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            AudioMixClockForTesting = mixClock,
            NextResultFactory = recording =>
            {
                observedRecording = recording;
                return Task.FromResult("Late capture transcript");
            },
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            transcriber.StartRecording();
            transcriber.StopRecording();

            Assert.False(transcriber.IsRecording);
            Assert.True(transcriber.IsFinalising);
            Assert.Equal(0, capture.ReadCallCount);

            transcriber.StartRecording();
            Assert.False(transcriber.IsRecording);
            Assert.True(transcriber.IsFinalising);

            capture.PublishFrames(2);
            mixClock.CrossMixBoundary();
            await WaitUntilAsync(
                sceneTree,
                () => transcriber.TranscribeCallCount == 1 && !transcriber.IsTranscribing,
                maxFrames: 30);

            RecordedAudioData recording = Assert.IsType<RecordedAudioData>(observedRecording);
            Assert.Equal(2, recording.FrameCount);
            Assert.Equal([0x00, 0x00, 0x00, 0x00], recording.PCMData.ToArray());
            Assert.Equal(1, capture.ReadCallCount);
            Assert.Equal(2, capture.LargestReadRequest);
            Assert.False(transcriber.IsFinalising);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a stalled mix clock cannot leave recording finalisation pending indefinitely.
    /// </summary>
    [Fact]
    public async Task StopRecording_WhenMixClockDoesNotAdvance_UsesBoundedProcessFallback()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 1);
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            AudioMixClockForTesting = new FakeAudioMixClock(),
            NextResultFactory = _ => Task.FromResult("Fallback transcript"),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            transcriber.StartRecording();
            transcriber.StopRecording();

            for (int frame = 0; frame < 3; frame++)
            {
                transcriber._Process(0);
                Assert.True(transcriber.IsFinalising);
                Assert.Equal(0, capture.ReadCallCount);
            }

            transcriber._Process(0);
            Assert.False(transcriber.IsFinalising);
            Assert.Equal(1, capture.ReadCallCount);
            await WaitUntilAsync(
                sceneTree,
                () => transcriber.TranscribeCallCount == 1 && !transcriber.IsTranscribing,
                maxFrames: 30);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies synchronous backend setup cannot block XR release or prevent Godot frames from advancing.
    /// </summary>
    [Fact]
    public async Task XRRecordButton_OnRelease_WithSynchronousBackendDelay_RemainsResponsiveAndCompletesOnGodotThread()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        int godotThreadId = System.Environment.CurrentManagedThreadId;
        TaskCompletionSource backendInvocationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBackendInvocation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource backendInvocationReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int? backendThreadId = null;
        List<(TranscriberLifecycleState State, bool Value, int ThreadId)> lifecycleTransitions = [];
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            NextResultFactory = async _ =>
            {
                backendThreadId = System.Environment.CurrentManagedThreadId;
                backendInvocationEntered.SetResult();
                await releaseBackendInvocation.Task;
                backendInvocationReturned.SetResult();
                return "Responsive XR Transcript";
            },
            LifecycleStateChangedForTesting = (state, value, threadId) =>
                lifecycleTransitions.Add((state, value, threadId)),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            int completedCount = 0;
            int? completionThreadId = null;
            _ = transcriber.Connect(
                Transcriber.SignalName.TranscriptionCompleted,
                Callable.From<string>(_ =>
                {
                    completedCount++;
                    completionThreadId = System.Environment.CurrentManagedThreadId;
                }));
            transcriber.RecordButton = new StringName("speech_record");

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(transcriber.IsRecording);

            var releaseStopwatch = Stopwatch.StartNew();
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            releaseStopwatch.Stop();

            Assert.True(
                releaseStopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"XR release was blocked for {releaseStopwatch.Elapsed.TotalMilliseconds:F0} ms by synchronous backend setup.");
            await WaitUntilAsync(sceneTree, () => backendInvocationEntered.Task.IsCompleted, maxFrames: 30);
            Assert.True(transcriber.IsTranscribing);

            int advancedFrames = 0;
            for (; advancedFrames < 3; advancedFrames++)
            {
                await WaitForNextFrameAsync(sceneTree);
                Assert.False(backendInvocationReturned.Task.IsCompleted, "Backend invocation returned before the frame responsiveness check completed.");
                Assert.Equal(0, completedCount);
            }

            releaseBackendInvocation.SetResult();
            await WaitUntilAsync(
                sceneTree,
                () => !transcriber.IsTranscribing && completedCount == 1,
                maxFrames: 60);

            Assert.Equal(3, advancedFrames);
            Assert.True(backendThreadId.HasValue);
            Assert.NotEqual(godotThreadId, backendThreadId);
            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.Equal(godotThreadId, completionThreadId);
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, true, _));
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, false, _));
            Assert.All(lifecycleTransitions, transition => Assert.Equal(godotThreadId, transition.ThreadId));
        }
        finally
        {
            _ = releaseBackendInvocation.TrySetResult();
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Guards the capture boundary that previously bulk-materialised the full native recording on XR release.
    /// </summary>
    [Fact]
    public async Task XRRecordButton_OnRelease_WithLargeCaptureBacklog_DrainsBoundedBatchAndFramesAdvance()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        TaskCompletionSource backendInvocationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBackendInvocation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Collections.Concurrent.ConcurrentDictionary<TranscriberPipelineStage, TimeSpan> stageTimings = new();
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 1_000_000);
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            NextResultFactory = async _ =>
            {
                backendInvocationEntered.SetResult();
                await releaseBackendInvocation.Task;
                return "Capture Boundary Transcript";
            },
            AudioCaptureForTesting = capture,
            PipelineStageMeasuredForTesting = (stage, elapsed) => stageTimings[stage] = elapsed,
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        long observedProcessFrames = 0;
        sceneTree.ProcessFrame += OnProcessFrame;

        try
        {
            transcriber.RecordButton = new StringName("speech_record");
            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(transcriber.IsRecording);

            long frameBeforeRelease = observedProcessFrames;
            int readsBeforeRelease = capture.ReadCallCount;
            var releaseStopwatch = Stopwatch.StartNew();
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            releaseStopwatch.Stop();

            Assert.True(
                releaseStopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                $"XR release took {releaseStopwatch.Elapsed.TotalMilliseconds:F2} ms while a large capture backlog was pending.");
            Assert.Equal(frameBeforeRelease, observedProcessFrames);
            Assert.True(stageTimings[TranscriberPipelineStage.MicrophonePlayerStop] < TimeSpan.FromMilliseconds(500));
            Assert.True(transcriber.IsFinalising);
            Assert.Equal(readsBeforeRelease, capture.ReadCallCount);

            await WaitUntilAsync(sceneTree, () => backendInvocationEntered.Task.IsCompleted, maxFrames: 30);
            Assert.True(stageTimings[TranscriberPipelineStage.FinalCaptureDrain] < TimeSpan.FromMilliseconds(500));
            Assert.Equal(readsBeforeRelease + 1, capture.ReadCallCount);
            Assert.Equal(2048, capture.LargestReadRequest);
            Assert.True(capture.FramesAvailable > 900_000, "Deferred finalisation unexpectedly drained the injected backlog.");
            Assert.True(stageTimings.ContainsKey(TranscriberPipelineStage.WorkerDispatch));
            Assert.False(transcriber.IsFinalising);
            long frameAfterRelease = observedProcessFrames;
            await WaitForFramesAsync(sceneTree, 3);
            Assert.True(observedProcessFrames >= frameAfterRelease + 3);
            Assert.True(transcriber.IsTranscribing);
            long framesWhileBackendPending = observedProcessFrames - frameAfterRelease;

            releaseBackendInvocation.SetResult();
            await WaitUntilAsync(
                sceneTree,
                () => !transcriber.IsTranscribing
                    && stageTimings.ContainsKey(TranscriberPipelineStage.CompletionDispatch),
                maxFrames: 60);
            Assert.True(stageTimings.ContainsKey(TranscriberPipelineStage.CompletionDispatch));

            Console.WriteLine(
                "Bounded capture evidence: release={0:F2} ms, player-stop={1:F2} ms, final-drain={2:F2} ms, largest-read={3}, worker-dispatch={4:F2} ms, frames-while-backend-pending={5}.",
                releaseStopwatch.Elapsed.TotalMilliseconds,
                stageTimings[TranscriberPipelineStage.MicrophonePlayerStop].TotalMilliseconds,
                stageTimings[TranscriberPipelineStage.FinalCaptureDrain].TotalMilliseconds,
                capture.LargestReadRequest,
                stageTimings[TranscriberPipelineStage.WorkerDispatch].TotalMilliseconds,
                framesWhileBackendPending);
        }
        finally
        {
            _ = releaseBackendInvocation.TrySetResult();
            sceneTree.ProcessFrame -= OnProcessFrame;
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }

        void OnProcessFrame()
        {
            observedProcessFrames++;
        }
    }

    /// <summary>
    /// Verifies recording auto-stops and transcribes when the maximum duration timer expires.
    /// </summary>
    [Fact]
    public async Task Recording_WhenMaxDurationExpires_AutoStopsAndTranscribes()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            fixture.Transcriber.RecordButton = new StringName("speech_record");
            fixture.Transcriber.MaxRecordingDuration = 0.1f;

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);

            Assert.True(fixture.Transcriber.IsRecording);
            Assert.Equal(0, transcriber.TranscribeCallCount);

            await WaitForSecondsAsync(sceneTree, 0.25);
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 1,
                maxFrames: 60);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(1, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies disabled transcribers ignore XR and manual start attempts.
    /// </summary>
    [Fact]
    public async Task StartRecording_WhenDisabled_DoesNotStartFromXRorManualPaths()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            fixture.Transcriber.Enabled = false;
            fixture.Transcriber.RecordButton = new StringName("speech_record");

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);

            fixture.Transcriber.StartRecording();
            await WaitForNextFrameAsync(sceneTree);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(0, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies disabling before stop prevents a new transcription request from starting.
    /// </summary>
    [Fact]
    public async Task StopRecording_WhenDisabledBeforeStop_DoesNotStartTranscriptionRequest()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);

            fixture.Transcriber.RecordButton = new StringName("speech_record");
            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(fixture.Transcriber.IsRecording);

            fixture.Transcriber.Enabled = false;
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsFinalising
                    && !fixture.Transcriber.IsTranscribing,
                maxFrames: 30);

            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(0, transcriber.TranscribeCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a minimal stereo capture is downmixed to exact mono PCM and runtime format metadata.
    /// </summary>
    [Fact]
    public async Task StopRecording_WithSingleStereoFrame_TranscribesExactPCMAndFormat()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RecordedAudioData? observedRecording = null;
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = new FakeAudioFrameCapture(framesAvailableAfterClear: 1)
            {
                FrameValue = new Vector2(1f, -1f),
            },
            NextResultFactory = recording =>
            {
                observedRecording = recording;
                return Task.FromResult("Short transcript");
            },
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            transcriber.StartRecording();
            transcriber.StopRecording();
            await WaitUntilAsync(sceneTree, () => transcriber.TranscribeCallCount == 1 && !transcriber.IsTranscribing, maxFrames: 60);

            RecordedAudioData recording = Assert.IsType<RecordedAudioData>(observedRecording);
            Assert.Equal(1, recording.FrameCount);
            Assert.Equal(1, recording.ChannelCount);
            Assert.Equal(Math.Max(1, (int)MathF.Round(AudioServer.GetMixRate())), recording.SampleRate);
            Assert.Equal([0x00, 0x00], recording.PCMData.ToArray());
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies capture storage remains duration-bounded and discarded native frames do not prevent safe transcription.
    /// </summary>
    [Fact]
    public async Task Recording_WithOverflowedBacklog_BoundsStoredFramesAndTranscribesSafely()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        int? recordedFrameCount = null;
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 1_000_000)
        {
            DiscardedFrames = 123,
        };
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            MaxRecordingDuration = 0.1f,
            AudioCaptureForTesting = capture,
            NextResultFactory = recording =>
            {
                recordedFrameCount = recording.FrameCount;
                return Task.FromResult("Bounded transcript");
            },
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            transcriber.StartRecording();
            for (int batch = 0; batch < 10; batch++)
            {
                transcriber._Process(0);
            }

            transcriber.StopRecording();
            await WaitUntilAsync(sceneTree, () => transcriber.TranscribeCallCount == 1 && !transcriber.IsTranscribing, maxFrames: 60);

            int expectedMaximumFrames = checked((int)Math.Ceiling(AudioServer.GetMixRate() * 0.1f));
            Assert.Equal(expectedMaximumFrames, recordedFrameCount);
            Assert.Equal(2048, capture.LargestReadRequest);
            Assert.True(capture.FramesAvailable > 900_000);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies teardown stops an active recording without dispatching partial audio to a backend.
    /// </summary>
    [Fact]
    public async Task Teardown_WhileRecording_StopsWithoutTranscribing()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree);

        try
        {
            fixture.Transcriber.StartRecording();
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(fixture.Transcriber.IsRecording);

            fixture.Global.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);

            FakeTranscriber transcriber = Assert.IsType<FakeTranscriber>(fixture.Transcriber);
            Assert.False(transcriber.IsRecording);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(0, transcriber.TranscribeCallCount);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(fixture.Global))
            {
                await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            }

            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies teardown abandons deferred finalisation without draining, transcribing, or emitting callbacks.
    /// </summary>
    [Fact]
    public async Task Teardown_WhileFinalising_AbandonsSessionWithoutCallbacks()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 1);
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            AudioMixClockForTesting = new FakeAudioMixClock(),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        int callbackCount = 0;
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(_ => callbackCount++));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionFailed, Callable.From<string>(_ => callbackCount++));

        try
        {
            transcriber.StartRecording();
            transcriber.StopRecording();
            int clearsBeforeTeardown = capture.ClearCallCount;
            Assert.True(transcriber.IsFinalising);

            fixture.Global.QueueFree();
            await WaitForFramesAsync(sceneTree, 6);

            Assert.False(transcriber.IsRecording);
            Assert.False(transcriber.IsFinalising);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(0, transcriber.TranscribeCallCount);
            Assert.Equal(0, capture.ReadCallCount);
            Assert.Equal(clearsBeforeTeardown + 1, capture.ClearCallCount);
            Assert.Equal(0, callbackCount);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(fixture.Global))
            {
                await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            }

            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a backend completing after teardown cannot dispatch signals to a freed Godot node.
    /// </summary>
    [Fact]
    public async Task Teardown_WhileTranscribing_SuppressesLateGodotCallbacks()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        TaskCompletionSource backendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBackend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource backendCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int godotThreadId = System.Environment.CurrentManagedThreadId;
        int? backendThreadId = null;
        List<(TranscriberLifecycleState State, bool Value, int ThreadId)> lifecycleTransitions = [];
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            NextResultFactory = async _ =>
            {
                backendThreadId = System.Environment.CurrentManagedThreadId;
                backendEntered.SetResult();
                await releaseBackend.Task;
                backendCompleted.SetResult();
                return "Late transcript";
            },
            LifecycleStateChangedForTesting = (state, value, threadId) =>
                lifecycleTransitions.Add((state, value, threadId)),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        int callbackCount = 0;
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(_ => callbackCount++));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionFailed, Callable.From<string>(_ => callbackCount++));

        try
        {
            transcriber.StartRecording();
            transcriber.StopRecording();
            await WaitUntilAsync(sceneTree, () => backendEntered.Task.IsCompleted, maxFrames: 30);
            Assert.True(transcriber.IsTranscribing);

            fixture.Global.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
            releaseBackend.SetResult();
            await WaitUntilAsync(sceneTree, () => backendCompleted.Task.IsCompleted, maxFrames: 30);
            await WaitForFramesAsync(sceneTree, 3);

            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(0, callbackCount);
            Assert.True(backendThreadId.HasValue);
            Assert.NotEqual(godotThreadId, backendThreadId);
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, true, _));
            Assert.Contains(
                lifecycleTransitions,
                transition => transition is (TranscriberLifecycleState.Transcribing, false, _));
            Assert.All(lifecycleTransitions, transition => Assert.Equal(godotThreadId, transition.ThreadId));
        }
        finally
        {
            _ = releaseBackend.TrySetResult();
            if (GodotObject.IsInstanceValid(fixture.Global))
            {
                await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            }

            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies teardown atomically cancels a completed backend action that is queued but not yet flushed.
    /// </summary>
    [Fact]
    public async Task Teardown_AfterCompletionDispatchQueued_CancelsStaleActionAndSettlesWorker()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        TaskCompletionSource backendCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource actionQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int actionExecutionCount = 0;
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            PauseDeferredGodotActionFlushForTesting = true,
            DeferredGodotActionQueuedForTesting = () => actionQueued.TrySetResult(),
            DeferredGodotActionExecutingForTesting = () => actionExecutionCount++,
            NextResultFactory = recording =>
            {
                _ = recording;
                _ = backendCompleted.TrySetResult();
                return Task.FromResult("Must not escape old lifetime");
            },
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        int completedSignalCount = 0;
        int failedSignalCount = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(_ => completedSignalCount++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(_ => failedSignalCount++));

        try
        {
            Task invocation = InvokeTranscriptionTask(transcriber);
            await WaitUntilAsync(
                sceneTree,
                () => backendCompleted.Task.IsCompleted && actionQueued.Task.IsCompleted,
                maxFrames: 30);

            Assert.True(transcriber.IsTranscribing);
            Assert.False(invocation.IsCompleted);
            Assert.Equal(0, actionExecutionCount);
            Assert.Equal(0, transcriber.CompletionHookCallCount);

            fixture.Global.QueueFree();
            await WaitUntilAsync(sceneTree, () => invocation.IsCompleted, maxFrames: 30);
            await invocation;
            await WaitForFramesAsync(sceneTree, 3);

            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(0, completedSignalCount);
            Assert.Equal(0, failedSignalCount);
            Assert.Equal(0, actionExecutionCount);
            Assert.Equal(0, transcriber.CompletionHookCallCount);
            Assert.True(backendCompleted.Task.IsCompletedSuccessfully);
            Assert.True(invocation.IsCompletedSuccessfully);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(fixture.Global))
            {
                await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            }

            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies an empty capture reports failure without dispatching a backend or leaving lifecycle state active.
    /// </summary>
    [Fact]
    public async Task StopRecording_WhenCaptureIsEmpty_EmitsFailureAndPreservesLifecycle()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeTranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = new FakeAudioFrameCapture(framesAvailableAfterClear: 0),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        string? failure = null;
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(message => failure = message));

        try
        {
            transcriber.StartRecording();
            await WaitForNextFrameAsync(sceneTree);
            transcriber.StopRecording();
            await WaitUntilAsync(sceneTree, () => !transcriber.IsFinalising, maxFrames: 30);

            Assert.False(transcriber.IsRecording);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(0, transcriber.TranscribeCallCount);
            Assert.Equal("Microphone recording contained no audio frames.", failure);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies OpenAI transcriber recording flow no longer posts pipeline debug notifications.
    /// </summary>
    [Fact]
    public async Task OpenAITranscriber_RecordingLifecycle_DoesNotPostPipelineDebugNotifications_AndCompletes()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        int dispatchingThreadId = System.Environment.CurrentManagedThreadId;
        int? backgroundThreadId = null;
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            NextResultFactory = async _ => await Task.Run(() =>
                {
                    backgroundThreadId = System.Environment.CurrentManagedThreadId;
                    return "XR Debug Transcript";
                }),
        };

        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            string? completedText = null;
            int completedCount = 0;
            int? completionSignalThreadId = null;
            _ = transcriber.Connect(
                Transcriber.SignalName.TranscriptionCompleted,
                Callable.From<string>(text =>
                {
                    completedCount++;
                    completedText = text;
                    completionSignalThreadId = System.Environment.CurrentManagedThreadId;
                }));

            fixture.Transcriber.RecordButton = new StringName("speech_record");

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(fixture.Transcriber.IsRecording);
            Assert.Empty(GetNotificationTexts(fixture.NotificationWidget));

            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => !fixture.Transcriber.IsRecording
                    && !fixture.Transcriber.IsTranscribing
                    && transcriber.TranscribeCallCount == 1,
                maxFrames: 60);

            Assert.True(backgroundThreadId.HasValue);
            Assert.NotEqual(dispatchingThreadId, backgroundThreadId);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.Empty(GetNotificationTexts(fixture.NotificationWidget));
            Assert.Equal(1, completedCount);
            Assert.Equal("XR Debug Transcript", completedText);
            Assert.Equal(dispatchingThreadId, completionSignalThreadId);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    private static async Task InvokeTranscriptionAsync(Transcriber transcriber)
        => await InvokeTranscriptionTask(transcriber);

    private static Task InvokeTranscriptionTask(Transcriber transcriber)
    {
        Task invocation = (Task?)_invokeTranscriptionAsyncMethod.Invoke(transcriber, [CreateRecording()])
            ?? throw new InvalidOperationException("Expected transcription invocation task.");

        return invocation;
    }

    private static RecordedAudioData CreateRecording()
        => new([0x01, 0x02, 0x03, 0x04], sampleRate: 16000, channelCount: 1);

    private static string GetNewestNotificationText(NotificationWidget notificationWidget)
    {
        VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");
        Label newestLabel = Assert.IsType<Label>(messages.GetChild(0), exactMatch: false);
        return newestLabel.Text;
    }

    private static bool HasNotification(NotificationWidget notificationWidget, string text)
        => GetNotificationTexts(notificationWidget).Contains(text, StringComparer.Ordinal);

    private static IReadOnlyList<string> GetNotificationTexts(NotificationWidget notificationWidget)
    {
        VBoxContainer messages = notificationWidget.GetNode<VBoxContainer>("Messages");
        List<string> notificationTexts = [];

        foreach (Node child in messages.GetChildren())
        {
            if (child is Label label)
            {
                notificationTexts.Add(label.Text);
            }
        }

        return notificationTexts;
    }

    private static async Task<(Node global, NotificationWidget notificationWidget)> CreateNotificationHostAsync(SceneTree sceneTree)
    {
        Node global = new()
        {
            Name = "Global",
        };

        Node xr = new()
        {
            Name = "XR",
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
        };

        UIOverlay overlay = new()
        {
            Name = "UIOverlay",
        };

        NotificationWidget notificationWidget = new()
        {
            Name = "NotificationOverlay",
        };

        VBoxContainer messages = new()
        {
            Name = "Messages",
        };

        notificationWidget.AddChild(messages);
        overlay.AddChild(notificationWidget);
        subViewport.AddChild(overlay);
        xr.AddChild(subViewport);
        global.AddChild(xr);
        sceneTree.Root.AddChild(global);
        await WaitForFramesAsync(sceneTree, 2);

        return (global, notificationWidget);
    }

    private static async Task<RuntimeSpeechFixture> CreateRuntimeSpeechFixtureAsync(SceneTree sceneTree)
        => await CreateRuntimeSpeechFixtureAsync(
            sceneTree,
            new FakeTranscriber
            {
                Name = "Transcriber",
                NextResultFactory = _ => Task.FromResult("XR Transcript"),
            });

    private static async Task<RuntimeSpeechFixture> CreateRuntimeSpeechFixtureAsync(SceneTree sceneTree, Transcriber transcriber)
    {
        transcriber.AudioCaptureForTesting ??= new FakeAudioFrameCapture(framesAvailableAfterClear: 1);
        Game global = new()
        {
            Name = "Global",
        };

        FakeXRManager xrManager = new()
        {
            Name = "XR",
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
            Disable3D = true,
        };

        UIOverlay overlay = new()
        {
            Name = "UIOverlay",
        };

        NotificationWidget notificationWidget = new()
        {
            Name = "NotificationOverlay",
        };

        VBoxContainer messages = new()
        {
            Name = "Messages",
        };

        notificationWidget.AddChild(messages);
        overlay.AddChild(notificationWidget);
        subViewport.AddChild(overlay);
        xrManager.AddChild(subViewport);
        global.AddChild(xrManager);

        global.AddChild(transcriber);
        global._EnterTree();
        sceneTree.Root.AddChild(global);
        await WaitForFramesAsync(sceneTree, 3);

        return new RuntimeSpeechFixture(global, xrManager, transcriber, xrManager.LeftController, notificationWidget);
    }

    private static async Task DestroyRuntimeSpeechFixtureAsync(SceneTree sceneTree, RuntimeSpeechFixture fixture)
    {
        fixture.Global.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);
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

    private static async Task DestroyFixtureAsync(SceneTree sceneTree, Transcriber transcriber, Node global)
    {
        transcriber.QueueFree();
        global.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);
    }

    private sealed partial class FakeTranscriber : Transcriber
    {
        public Func<RecordedAudioData, Task<string>> NextResultFactory
        {
            get;
            set;
        } = _ => Task.FromResult(string.Empty);

        public int TranscribeCallCount
        {
            get;
            private set;
        }

        public int CompletionHookCallCount
        {
            get;
            private set;
        }

        public override Task<string> Transcribe(RecordedAudioData recording)
        {
            TranscribeCallCount++;
            return NextResultFactory(recording);
        }

        protected override void OnTranscriptionCompleted(string text)
        {
            _ = text;
            CompletionHookCallCount++;
        }
    }

    private sealed partial class FakeOpenAITranscriber : OpenAITranscriber
    {
        public Func<RecordedAudioData, Task<string>> NextResultFactory
        {
            get;
            set;
        } = _ => Task.FromResult(string.Empty);

        public int TranscribeCallCount
        {
            get;
            private set;
        }

        public override Task<string> Transcribe(RecordedAudioData recording)
        {
            TranscribeCallCount++;
            return NextResultFactory(recording);
        }
    }

    private sealed class FakeAudioFrameCapture(int framesAvailableAfterClear) : IAudioFrameCapture
    {
        public long FramesAvailable
        {
            get; private set;
        }

        public long DiscardedFrames
        {
            get; set;
        }

        public int LargestReadRequest
        {
            get; private set;
        }

        public int ReadCallCount
        {
            get; private set;
        }

        public int ClearCallCount
        {
            get;
            private set;
        }

        public Vector2 FrameValue
        {
            get; set;
        } = new(0.25f, -0.25f);

        public Vector2[] ReadFrames(int maximumFrames)
        {
            ReadCallCount++;
            LargestReadRequest = Math.Max(LargestReadRequest, maximumFrames);
            int count = (int)Math.Min(maximumFrames, FramesAvailable);
            var frames = new Vector2[count];
            Array.Fill(frames, FrameValue);
            FramesAvailable -= count;
            return frames;
        }

        public void PublishFrames(int frameCount) => FramesAvailable += frameCount;

        public void Clear()
        {
            ClearCallCount++;
            FramesAvailable = framesAvailableAfterClear;
        }
    }

    private sealed class FakeAudioMixClock : IAudioMixClock
    {
        public double TimeSinceLastMix
        {
            get;
            private set;
        } = 0.008;

        public void CrossMixBoundary() => TimeSinceLastMix = 0.001;
    }

    private sealed record RuntimeSpeechFixture(
        Node Global,
        XRManager XRManager,
        Transcriber Transcriber,
        FakeXRHandController LeftController,
        NotificationWidget NotificationWidget);

    private sealed partial class FakeXRManager : XRManager
    {
        private static readonly FieldInfo _runtimeBackingField = typeof(XRManager)
            .GetField("<Runtime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected XRManager runtime backing field for speech tests.");

        private readonly FakeXRRuntime _runtime = new();

        public FakeXRHandController LeftController => _runtime.LeftControllerNode;

        public override void _Ready()
        {
            _runtimeBackingField.SetValue(this, _runtime);
            InitialisationAttempted = true;
            InitialisationSucceeded = true;
            _ = EmitSignal("Initialised", true);
        }
    }

    private sealed class FakeXRRuntime : IXRRuntime
    {
        public FakeXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            LeftControllerNode = new FakeXRHandController();
            RightControllerNode = new FakeXRHandController();
        }

        public IXROrigin Origin => new FakeXROrigin(OriginNode);

        public IXRCamera Camera => new FakeXRCamera(CameraNode);

        public IXRHandController RightHandController => RightControllerNode;

        public IXRHandController LeftHandController => LeftControllerNode;

#pragma warning disable CS0067
        public event Action? PoseRecentered;
#pragma warning restore CS0067

        public Node3D OriginNode
        {
            get;
        }

        public Camera3D CameraNode
        {
            get;
        }

        public FakeXRHandController LeftControllerNode
        {
            get;
        }

        public FakeXRHandController RightControllerNode
        {
            get;
        }

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
        }
    }

    private sealed partial class FakeXRHandController : Node3D, IXRHandController
    {
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

#pragma warning disable CS0067
        public event Action<string, float>? ActionFloatInputChanged;

        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;

        public void TriggerActionButtonPressed(string actionName)
            => ActionButtonPressed?.Invoke(actionName);

        public void TriggerActionButtonReleased(string actionName)
            => ActionButtonReleased?.Invoke(actionName);
    }

    private sealed record FakeXROrigin(Node3D OriginNode) : IXROrigin
    {
        public float WorldScale { get; set; } = 1.0f;
    }

    private sealed record FakeXRCamera(Camera3D CameraNode) : IXRCamera;

    private sealed class ExistingGlobalScope(SceneTree sceneTree, Node? existingGlobal, string? originalName)
    {
        public static async Task<ExistingGlobalScope> CreateAsync(SceneTree sceneTree)
        {
            Node? existingGlobal = sceneTree.Root.GetNodeOrNull<Node>("Global");
            if (existingGlobal is null)
            {
                return new ExistingGlobalScope(sceneTree, null, null);
            }

            string originalName = existingGlobal.Name;
            existingGlobal.Name = "Global_PreSpeechIntegrationTest";
            await WaitForNextFrameAsync(sceneTree);
            return new ExistingGlobalScope(sceneTree, existingGlobal, originalName);
        }

        public async Task DisposeAsync()
        {
            if (existingGlobal is null || originalName is null)
            {
                return;
            }

            existingGlobal.Name = originalName;
            await WaitForNextFrameAsync(sceneTree);
        }
    }
}
