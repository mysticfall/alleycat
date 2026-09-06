using System.Buffers.Binary;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using AlleyCat.Core.Logging;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Rigging;
using AlleyCat.Sense;
using AlleyCat.Speech;
using AlleyCat.Speech.Transcription;
using AlleyCat.Speech.Voice;
using AlleyCat.UI;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for transcription completion and failure orchestration.
/// </summary>
public sealed partial class TranscriberIntegrationTests : IDisposable
{
    private readonly PipelineDebugLogFixture _debugLogFixture = new();

    private static readonly MethodInfo _invokeTranscriptionAsyncMethod = typeof(Transcriber)
        .GetMethod("InvokeTranscriptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected Transcriber.InvokeTranscriptionAsync for runtime speech tests.");

    /// <summary>
    /// Clears the isolated pipeline logger override after each test.
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
    /// Verifies a manual capture dropped by disabling the node while its finalisation was still pending ends its
    /// session through exactly one terminal <c>RecordingAbandoned</c> signal — with no transcription dispatch and
    /// no double emission from the later teardown (SPCH-003 TR-28).
    /// </summary>
    [Fact]
    public async Task Disabled_WhileFinalising_AbandonsManualSessionExactlyOnce()
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
        int abandonedCount = 0;
        int terminalCount = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingAbandoned,
            Callable.From(() => abandonedCount++));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(_ => terminalCount++));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionFailed, Callable.From<string>(_ => terminalCount++));

        try
        {
            transcriber.StartRecording();
            transcriber.StopRecording();
            Assert.True(transcriber.IsFinalising);

            // Disabling the node before the finalisation's bounded drain completes drops the captured manual
            // recording at the transcription-dispatch guard: the disable path owns the session's abandonment.
            transcriber.Enabled = false;
            await WaitForFramesAsync(sceneTree, 8);

            Assert.False(transcriber.IsRecording);
            Assert.False(transcriber.IsFinalising);
            Assert.False(transcriber.IsTranscribing);
            Assert.Equal(1, abandonedCount);
            Assert.Equal(0, transcriber.TranscribeCallCount);
            Assert.Equal(0, terminalCount);

            // Teardown after the disable-path settlement never emits a second abandonment: each manual session
            // ends in exactly one terminal outcome.
            fixture.Global.QueueFree();
            await WaitForFramesAsync(sceneTree, 4);

            Assert.Equal(1, abandonedCount);
            Assert.Equal(0, terminalCount);
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
    /// Verifies the OpenAI transcriber recording flow posts exactly one STT dispatch notification per logical
    /// request — through the real REST boundary, the shipped YAML child-category toggle, and the real notification
    /// routing — with zero other STT notifications, while the completion signal still settles on the Godot thread.
    /// </summary>
    [Fact]
    public async Task OpenAITranscriber_RecordingLifecycle_PostsExactlyOneSttDispatchNotification_AndCompletes()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        int dispatchingThreadId = System.Environment.CurrentManagedThreadId;
        int? backgroundThreadId = null;
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            RouteThroughRestBoundary = true,
            NextResultFactory = async _ => await Task.Run(() =>
                {
                    backgroundThreadId = System.Environment.CurrentManagedThreadId;
                    return "XR Debug Transcript";
                }),
        };

        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            // Route the pipeline diagnostics helper through the fixture game's real logging stack so the shipped
            // YAML's STT child-category toggle governs the dispatch marker exactly as it does for players.
            PipelineDebugLog.SetLoggerFactoryForTesting(
                Game.Instance.GetRequiredService<ILoggerFactory>());

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

            VBoxContainer messages = fixture.NotificationWidget.GetNode<VBoxContainer>("Messages");
            SignalAwaiter transcriptionCompleted = sceneTree.ToSignal(
                transcriber,
                Transcriber.SignalName.TranscriptionCompleted);
            SignalAwaiter dispatchNotificationDelivered = sceneTree.ToSignal(
                messages,
                Node.SignalName.ChildEnteredTree);

            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            _ = await dispatchNotificationDelivered;
            _ = await transcriptionCompleted;

            // Exactly one dispatch toast per logical request, and no other STT notification.
            IReadOnlyList<string> notificationTexts = GetNotificationTexts(fixture.NotificationWidget);
            string dispatchText = Assert.Single(notificationTexts);
            Assert.StartsWith("Dispatching manual audio to STT (", dispatchText, StringComparison.Ordinal);
            Assert.EndsWith(" PCM bytes)", dispatchText, StringComparison.Ordinal);

            Assert.True(backgroundThreadId.HasValue);
            Assert.NotEqual(dispatchingThreadId, backgroundThreadId);
            Assert.False(fixture.Transcriber.IsRecording);
            Assert.False(fixture.Transcriber.IsTranscribing);
            Assert.Equal(1, transcriber.TranscribeCallCount);
            Assert.Equal(1, completedCount);
            Assert.Equal("XR Debug Transcript", completedText);
            Assert.Equal(dispatchingThreadId, completionSignalThreadId);
        }
        finally
        {
            PipelineDebugLog.SetLoggerFactoryForTesting(null);
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies the automatic input path creates one public logical window per utterance — signalled through the
    /// ordinary recording-started signal — and commits exactly one REST final result.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_CompletesOnceWithRESTFinal()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = new AmplitudeFakeVoiceActivityDetector(0.5f),
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 1,
            AutomaticContinuationGapMilliseconds = 2,
            AutomaticMaximumDuration = 1f,
            AutomaticFinalResult = "authoritative REST final",
        };

        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<AutomaticSegmentResult> completions = [];
        int recordingStarts = 0;
        int legacyCompletions = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((text, groupID, segmentIndex, continued) =>
                completions.Add(new AutomaticSegmentResult(text, groupID, segmentIndex, continued))));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(_ => legacyCompletions++));

        try
        {
            Assert.Equal(VoiceInputMode.ButtonAndAutomatic, transcriber.InputMode);

            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(sceneTree, () => recordingStarts == 1 && transcriber.IsRecording, maxFrames: 60);

            capture.PublishFrames(new Vector2[2048]);
            capture.PublishFrames(new Vector2[2048]);
            await WaitUntilAsync(sceneTree, () => completions.Count == 1 && !transcriber.IsTranscribing, maxFrames: 120);

            Assert.Equal(1, recordingStarts);
            AutomaticSegmentResult completion = Assert.Single(completions);
            Assert.Equal("authoritative REST final", completion.Text);
            Assert.NotEqual(Guid.Empty.ToString(), completion.SpeechGroupID);
            Assert.Equal(0, completion.SegmentIndex);
            Assert.False(completion.Continued);
            Assert.Equal(1, transcriber.AutomaticFinalisationCallCount);
            Assert.Equal(0, legacyCompletions);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a REST result which settles before the continuation gap still produces one group terminal and closes
    /// the speaking window, without changing the already-published nonblank segment's normal window ordering.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_ResultSettledBeforeContinuationClose_ClosesGroupAndSpeakingWindowExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        TaskCompletionSource<string> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeOpenAITranscriber transcriber = CreateAutomaticTranscriber(capture, new AmplitudeFakeVoiceActivityDetector(0.5f));
        transcriber.AutomaticResultFactory = (_, _, _) => result.Task;
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        VoiceSpeakingWindowIntegrationTests.WindowTestPlayerVoice voice = new()
        {
            Name = "PlayerVoice",
            Transcriber = transcriber,
        };
        SpeechPublicationObserver publicationObserver = new()
        {
            Name = "PlayerVoicePublicationObserver",
        };
        int groupClosedCount = 0;
        int segmentCompletedCount = 0;
        int speechEndedCount = 0;
        fixture.Global.AddChild(voice);
        fixture.Global.AddChild(publicationObserver);
        publicationObserver.AddToGroup(IHearing.GroupName);
        voice.SpeechEnded += _ => speechEndedCount++;
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticGroupClosed,
            Callable.From<string, bool>((_, _) => groupClosedCount++));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((_, _, _, _) => segmentCompletedCount++));

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(sceneTree, () => voice.IsSpeaking && transcriber.IsRecording, maxFrames: 60);
            capture.PublishFrames(new Vector2[2048]);
            await WaitForNextFrameAsync(sceneTree);
            capture.PublishFrames(new Vector2[2048]);
            await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 1, maxFrames: 30);

            // Complete the fenced REST request while the group remains inside its continuation gap.
            result.SetResult("published before group close");
            await WaitUntilAsync(
                sceneTree,
                () => segmentCompletedCount == 1 && !transcriber.IsTranscribing,
                maxFrames: 90);

            Assert.True(voice.IsSpeaking);
            Assert.Equal(0, speechEndedCount);
            _ = Assert.Single(publicationObserver.Events);
            Assert.True(publicationObserver.IsSpeakingAtPublication);
            Assert.Equal("published before group close", Assert.Single(publicationObserver.Events).Speech);
            Assert.Equal(0, groupClosedCount);

            await CloseAutomaticGroupAsync(sceneTree, capture, transcriber);
            await WaitUntilAsync(
                sceneTree,
                () => groupClosedCount == 1 && !voice.IsSpeaking && !transcriber.IsRecording && !transcriber.IsTranscribing,
                maxFrames: 60);

            Assert.Equal(1, groupClosedCount);
            Assert.Equal(1, speechEndedCount);
            _ = Assert.Single(publicationObserver.Events);
        }
        finally
        {
            _ = result.TrySetCanceled();
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies concurrent automatic REST requests settle and publish in speaking order rather than backend-completion
    /// order, preserving one player speaking window through the group's final settlement.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_TwoSegments_OrdersOutOfOrderRESTResultsThroughPlayerVoiceAndHearing()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        TaskCompletionSource<string> firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> secondResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeOpenAITranscriber transcriber = CreateAutomaticTranscriber(capture, new AmplitudeFakeVoiceActivityDetector(0.5f));
        transcriber.AutomaticResultFactory = (_, segmentIndex, _) => segmentIndex switch
        {
            0 => firstResult.Task,
            1 => secondResult.Task,
            _ => throw new InvalidOperationException($"Unexpected automatic segment index {segmentIndex}."),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        VoiceSpeakingWindowIntegrationTests.WindowTestPlayerVoice voice = new()
        {
            Name = "PlayerVoice",
            Transcriber = transcriber,
        };
        Hearing hearing = new()
        {
            Name = "Hearing",
        };
        SpeechPublicationObserver publicationObserver = new()
        {
            Name = "PlayerVoicePublicationObserver",
        };
        List<AutomaticSegmentResult> segmentSignals = [];
        List<SpeechPercept> percepts = [];
        fixture.Global.AddChild(voice);
        fixture.Global.AddChild(hearing);
        fixture.Global.AddChild(publicationObserver);
        publicationObserver.AddToGroup(IHearing.GroupName);
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((text, groupID, segmentIndex, continued) =>
                segmentSignals.Add(new AutomaticSegmentResult(text, groupID, segmentIndex, continued))));

        try
        {
            await WaitForFramesAsync(sceneTree, 2);
            await CaptureTwoSegmentAutomaticGroupAsync(sceneTree, capture, transcriber);
            Assert.Equal(2, transcriber.AutomaticFinalisationCallCount);
            Assert.True(voice.IsSpeaking);

            // The later segment reaches the completion boundary first, but the group's ordered settlement gate holds it.
            secondResult.SetResult("second segment");
            await WaitForFramesAsync(sceneTree, 3);
            Assert.Empty(segmentSignals);
            Assert.Empty(publicationObserver.Events);
            Assert.Empty(percepts);
            Assert.True(voice.IsSpeaking);

            firstResult.SetResult("first segment");
            await WaitUntilAsync(
                sceneTree,
                () => segmentSignals.Count == 2 && percepts.Count == 2 && !voice.IsSpeaking,
                maxFrames: 90);

            Assert.Equal(["first segment", "second segment"], segmentSignals.Select(result => result.Text));
            Assert.Equal([0, 1], segmentSignals.Select(result => result.SegmentIndex));
            Assert.Equal([false, true], segmentSignals.Select(result => result.Continued));
            string groupID = Assert.Single(segmentSignals.Select(result => result.SpeechGroupID).Distinct());
            Assert.NotEqual(Guid.Empty.ToString(), groupID);

            Assert.Equal(["first segment", "second segment"], publicationObserver.Events.Select(entry => entry.Speech));
            Assert.Equal(["first segment", "second segment"], percepts.Select(percept => percept.Content));
            Assert.All(percepts, percept => Assert.Equal(groupID, percept.SpeechGroupID));
            Assert.Equal([0, 1], percepts.Select(percept => percept.SegmentIndex));
            Assert.Equal([false, true], percepts.Select(percept => percept.Continued));
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies endpoint slicing retains only each segment's exact endpoint suffix and starts the continued segment at
    /// its resumed voiced frame on the mono 16 kHz capture path.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_TwoSegments_CapturesExactEndpointAndResumeBoundaries()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = CreateAutomaticTranscriber(capture, new AmplitudeFakeVoiceActivityDetector(0.5f));
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            int detectorRegionFrames = checked((int)Math.Ceiling(
                (SileroVoiceActivityDetector.FrameSampleCount - 1) * AudioServer.GetMixRate() / 16000d) + 17);
            // One source region maps to one detector frame. The endpoint is 16 samples (one millisecond), making any
            // over-retained continuation or resumed audio visible independently of the test runner's mix rate.
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), detectorRegionFrames)]);
            await WaitUntilAsync(sceneTree, () => transcriber.IsRecording, maxFrames: 30);
            capture.PublishFrames(new Vector2[detectorRegionFrames]);
            await WaitForNextFrameAsync(sceneTree);
            capture.PublishFrames(new Vector2[detectorRegionFrames]);
            await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 1, maxFrames: 30);
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.6f, 0.6f), detectorRegionFrames)]);
            await WaitForNextFrameAsync(sceneTree);
            capture.PublishFrames(new Vector2[detectorRegionFrames]);
            await WaitForNextFrameAsync(sceneTree);
            capture.PublishFrames(new Vector2[detectorRegionFrames]);
            await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 2, maxFrames: 30);
            await CloseAutomaticGroupAsync(sceneTree, capture, transcriber);

            Assert.Equal(2, transcriber.AutomaticFinalRecordings.Count);
            RecordedAudioData first = transcriber.AutomaticFinalRecordings[0];
            RecordedAudioData second = transcriber.AutomaticFinalRecordings[1];
            Assert.All(transcriber.AutomaticFinalRecordings, recording =>
            {
                Assert.Equal(16000, recording.SampleRate);
                Assert.Equal(1, recording.ChannelCount);
            });

            short[] firstSamples = GetPCM16Samples(first);
            short[] secondSamples = GetPCM16Samples(second);
            Assert.Equal(16, firstSamples.Length % SileroVoiceActivityDetector.FrameSampleCount);
            Assert.Equal(16, secondSamples.Length % SileroVoiceActivityDetector.FrameSampleCount);
            Assert.InRange(firstSamples[0], 26213, 26215);
            Assert.Equal(0, firstSamples[^1]);
            Assert.DoesNotContain(firstSamples, sample => sample is >= 19659 and <= 19661);
            Assert.Contains(secondSamples, sample => sample is >= 19659 and <= 19661);
            Assert.Equal(0, secondSamples[^1]);
            Assert.DoesNotContain(secondSamples, sample => sample is >= 26213 and <= 26215);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies manual precedence abandons an already-dispatched automatic segment and suppresses its deliberate late
    /// backend completion while retaining a single continuous player speaking window.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_ManualPreemption_SuppressesLateAutomaticCallbackAndKeepsOneSpeakingWindow()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        TaskCompletionSource<string> automaticResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeOpenAITranscriber transcriber = CreateAutomaticTranscriber(capture, new AmplitudeFakeVoiceActivityDetector(0.5f));
        transcriber.AutomaticResultFactory = (_, _, _) => automaticResult.Task;
        transcriber.NextResultFactory = _ => Task.FromResult("manual wins");
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        VoiceSpeakingWindowIntegrationTests.WindowTestPlayerVoice voice = new()
        {
            Name = "PlayerVoice",
            Transcriber = transcriber,
        };
        Hearing hearing = new()
        {
            Name = "Hearing",
        };
        List<AutomaticSegmentResult> automaticSignals = [];
        List<SpeechPercept> percepts = [];
        int started = 0;
        int ended = 0;
        fixture.Global.AddChild(voice);
        fixture.Global.AddChild(hearing);
        hearing.Perceived += percept => percepts.Add(Assert.IsType<SpeechPercept>(percept));
        voice.SpeechStarted += _ => started++;
        voice.SpeechEnded += _ => ended++;
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((text, groupID, segmentIndex, continued) =>
                automaticSignals.Add(new AutomaticSegmentResult(text, groupID, segmentIndex, continued))));

        try
        {
            transcriber.RecordButton = new StringName("speech_record");
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(sceneTree, () => voice.IsSpeaking && transcriber.IsRecording, maxFrames: 60);
            capture.PublishFrames(new Vector2[2048]);
            await WaitForNextFrameAsync(sceneTree);
            capture.PublishFrames(new Vector2[2048]);
            await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 1, maxFrames: 30);

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitUntilAsync(sceneTree, () => transcriber.IsRecording, maxFrames: 30);
            Assert.True(voice.IsSpeaking);
            Assert.Equal(1, started);
            Assert.Equal(0, ended);

            capture.PublishFrames([new Vector2(0.5f, 0.5f)]);
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(sceneTree, () => percepts.Count == 1 && !transcriber.IsTranscribing, maxFrames: 90);

            automaticResult.SetResult("stale automatic result");
            await WaitForFramesAsync(sceneTree, 4);

            Assert.Empty(automaticSignals);
            SpeechPercept manualPercept = Assert.Single(percepts);
            Assert.Equal("manual wins", manualPercept.Content);
            Assert.Null(manualPercept.SpeechGroupID);
            Assert.Equal(1, started);
            Assert.Equal(1, ended);
            Assert.False(voice.IsSpeaking);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies force-close slices its active prefix at the maximum deadline, creates no empty tail, and requires the
    /// configured clean-silence rearm interval before another automatic group is admitted.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_ForceClose_TrimsPrefixWithoutEmptyTailAndRearmsAfterCleanSilence()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = CreateAutomaticTranscriber(capture, new AmplitudeFakeVoiceActivityDetector(0.5f));
        transcriber.AutomaticEndpointSilenceMilliseconds = 100;
        transcriber.AutomaticContinuationGapMilliseconds = 200;
        transcriber.AutomaticMaximumDuration = 1f;
        int recordingStarts = 0;
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        _ = transcriber.Connect(Transcriber.SignalName.RecordingStarted, Callable.From(() => recordingStarts++));

        try
        {
            for (int batch = 0; batch < 30 && transcriber.AutomaticFinalisationCallCount == 0; batch++)
            {
                capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 1536)]);
                await WaitForNextFrameAsync(sceneTree);
            }

            await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 1, maxFrames: 60);
            RecordedAudioData prefix = Assert.Single(transcriber.AutomaticFinalRecordings);
            Assert.Equal(16000, prefix.FrameCount);
            Assert.Equal(1, transcriber.AutomaticFinalisationCallCount);

            // One 512-sample silent detector frame is shorter than the 100 ms (1,600-sample) rearm requirement.
            capture.PublishFrames(new Vector2[1536]);
            await WaitForNextFrameAsync(sceneTree);
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 1536)]);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.Equal(1, recordingStarts);
            Assert.Equal(1, transcriber.AutomaticFinalisationCallCount);

            // Clean silence accumulates independently of frame pacing; only then may a fresh voiced frame admit group two.
            for (int batch = 0; batch < 4; batch++)
            {
                capture.PublishFrames(new Vector2[1536]);
                await WaitForNextFrameAsync(sceneTree);
            }

            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 1536)]);
            await WaitUntilAsync(sceneTree, () => recordingStarts == 2, maxFrames: 30);
            Assert.Equal(1, transcriber.AutomaticFinalisationCallCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies the real REST-boundary fixture emits one STT dispatch marker for each automatic segment.
    /// </summary>
    /// <remarks>
    /// Requires the real notification-widget lifecycle of a windowed run and is not headless-safe: under
    /// <c>--headless</c> the second dispatch-marker toast deterministically never appears on the widget. This is
    /// a known environment sensitivity of the widget lifecycle, so do not diagnose product regressions from
    /// headless failures of this test — always run it windowed, per the project contract.
    /// </remarks>
    [Fact]
    public async Task AutomaticInput_TwoSegments_PostsExactlyOneSTTDispatchMarkerPerRESTRequest()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = CreateAutomaticTranscriber(capture, new AmplitudeFakeVoiceActivityDetector(0.5f));
        transcriber.RouteThroughRestBoundary = true;
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);

        try
        {
            PipelineDebugLog.SetLoggerFactoryForTesting(Game.Instance.GetRequiredService<ILoggerFactory>());
            await CaptureTwoSegmentAutomaticGroupAsync(sceneTree, capture, transcriber);
            await WaitUntilAsync(
                sceneTree,
                () => transcriber.AutomaticFinalisationCallCount == 2 && !transcriber.IsTranscribing,
                maxFrames: 120);
            await WaitUntilAsync(sceneTree, () => GetNotificationTexts(fixture.NotificationWidget).Count == 2, maxFrames: 30);

            IReadOnlyList<string> markers = GetNotificationTexts(fixture.NotificationWidget);
            Assert.Equal(2, markers.Count);
            Assert.All(markers, marker =>
            {
                Assert.StartsWith("Dispatching automatic audio to STT (", marker, StringComparison.Ordinal);
                Assert.EndsWith(" PCM bytes)", marker, StringComparison.Ordinal);
            });
        }
        finally
        {
            PipelineDebugLog.SetLoggerFactoryForTesting(null);
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>Verifies a manual button press abandons the active automatic logical window without a stale aggregate.</summary>
    [Fact]
    public async Task AutomaticInput_ManualPressPreemptsAutomaticUtteranceWithoutPublicOverlap()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = new AmplitudeFakeVoiceActivityDetector(0.5f),
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 1,
            AutomaticContinuationGapMilliseconds = 2,
            AutomaticMaximumDuration = 1f,
            NextResultFactory = _ => Task.FromResult("manual final"),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<string> completions = [];
        int failures = 0;
        int starts = 0;
        _ = transcriber.Connect(Transcriber.SignalName.RecordingStarted, Callable.From(() => starts++));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(completions.Add));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionFailed, Callable.From<string>(_ => failures++));

        try
        {
            transcriber.RecordButton = new StringName("speech_record");
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(sceneTree, () => starts == 1 && transcriber.IsRecording, maxFrames: 60);

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitUntilAsync(sceneTree, () => starts == 2 && transcriber.IsRecording, maxFrames: 30);
            capture.PublishFrames([new Vector2(0.5f, 0.5f)]);
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(sceneTree, () => completions.Count == 1 && !transcriber.IsTranscribing, maxFrames: 90);

            Assert.Equal(["manual final"], completions);
            Assert.Equal(0, failures);
            Assert.Equal(2, starts);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a failed REST final rejects each automatic utterance through exactly one failure signal with no
    /// completion and a settled lifecycle.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_OnRESTFinalFailure_EmitsExactlyOneFailurePerUtteranceWithoutCompletion()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = new AmplitudeFakeVoiceActivityDetector(0.5f),
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 1,
            AutomaticContinuationGapMilliseconds = 2,
            AutomaticMaximumDuration = 1f,
            AutomaticFinalException = new InvalidOperationException("REST final unavailable"),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<AutomaticSegmentResult> completions = [];
        List<AutomaticSegmentFailure> failures = [];
        int recordingStarts = 0;
        int legacyCompletions = 0;
        int legacyFailures = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((text, groupID, segmentIndex, continued) =>
                completions.Add(new AutomaticSegmentResult(text, groupID, segmentIndex, continued))));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentFailed,
            Callable.From<string, string, int, bool>((error, groupID, segmentIndex, continued) =>
                failures.Add(new AutomaticSegmentFailure(error, groupID, segmentIndex, continued))));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(_ => legacyCompletions++));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionFailed, Callable.From<string>(_ => legacyFailures++));

        try
        {
            for (int utterance = 1; utterance <= 2; utterance++)
            {
                capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
                await WaitUntilAsync(
                    sceneTree,
                    () => recordingStarts == utterance && transcriber.IsRecording,
                    maxFrames: 60);
                capture.PublishFrames(new Vector2[2048]);
                capture.PublishFrames(new Vector2[2048]);
                await WaitUntilAsync(
                    sceneTree,
                    () => failures.Count == utterance
                        && !transcriber.IsRecording
                        && !transcriber.IsTranscribing
                        && !transcriber.IsFinalising,
                    maxFrames: 120);
            }

            Assert.Equal(["REST final unavailable", "REST final unavailable"], failures.Select(failure => failure.Error));
            Assert.All(failures, failure =>
            {
                Assert.NotEqual(Guid.Empty.ToString(), failure.SpeechGroupID);
                Assert.Equal(0, failure.SegmentIndex);
                Assert.False(failure.Continued);
            });
            Assert.Empty(completions);
            Assert.Equal(2, recordingStarts);
            Assert.Equal(0, legacyCompletions);
            Assert.Equal(0, legacyFailures);
            Assert.False(transcriber.IsRecording);
            Assert.False(transcriber.IsTranscribing);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a blank REST final settles as one blank completion that consumers can filter, with no failure signal
    /// and no extra terminal events.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_OnBlankRESTFinal_CompletesOnceWithBlankTextAndNoFailure()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = new AmplitudeFakeVoiceActivityDetector(0.5f),
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 1,
            AutomaticContinuationGapMilliseconds = 2,
            AutomaticMaximumDuration = 1f,
            AutomaticFinalResult = "   ",
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<AutomaticSegmentResult> completions = [];
        List<AutomaticSegmentFailure> failures = [];
        int recordingStarts = 0;
        int legacyCompletions = 0;
        int legacyFailures = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((text, groupID, segmentIndex, continued) =>
                completions.Add(new AutomaticSegmentResult(text, groupID, segmentIndex, continued))));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentFailed,
            Callable.From<string, string, int, bool>((error, groupID, segmentIndex, continued) =>
                failures.Add(new AutomaticSegmentFailure(error, groupID, segmentIndex, continued))));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(_ => legacyCompletions++));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionFailed, Callable.From<string>(_ => legacyFailures++));

        try
        {
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(
                sceneTree,
                () => recordingStarts == 1 && transcriber.IsRecording,
                maxFrames: 60);
            capture.PublishFrames(new Vector2[2048]);
            capture.PublishFrames(new Vector2[2048]);
            await WaitUntilAsync(
                sceneTree,
                () => completions.Count == 1
                    && !transcriber.IsRecording
                    && !transcriber.IsTranscribing
                    && !transcriber.IsFinalising,
                maxFrames: 120);

            AutomaticSegmentResult completion = Assert.Single(completions);
            Assert.Equal("   ", completion.Text);
            Assert.NotEqual(Guid.Empty.ToString(), completion.SpeechGroupID);
            Assert.Equal(0, completion.SegmentIndex);
            Assert.False(completion.Continued);
            Assert.Empty(failures);
            Assert.Equal(1, recordingStarts);
            Assert.Equal(0, legacyCompletions);
            Assert.Equal(0, legacyFailures);
            Assert.False(transcriber.IsRecording);
            Assert.False(transcriber.IsTranscribing);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies manual push-to-talk keeps working when the node stays in its default button-only input mode.
    /// </summary>
    [Fact]
    public async Task ManualInput_WithButtonOnlyDefault_TranscribesNormally()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 1);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            NextResultFactory = _ => Task.FromResult("button-only final"),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<string> completions = [];

        try
        {
            Assert.Equal(VoiceInputMode.ButtonOnly, transcriber.InputMode);
            transcriber.RecordButton = new StringName("speech_record");
            _ = transcriber.Connect(
                Transcriber.SignalName.TranscriptionCompleted,
                Callable.From<string>(completions.Add));

            // Sustained microphone-level audio never opens an automatic window in button-only mode.
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitForFramesAsync(sceneTree, 5);
            Assert.False(transcriber.IsRecording);

            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(transcriber.IsRecording);
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => completions.Count == 1 && !transcriber.IsTranscribing,
                maxFrames: 90);

            Assert.Equal(["button-only final"], completions);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies automatic utterances are finalised as mono 16 kHz audio assembled from exact 512-sample frames on
    /// the continuous resampled clock.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_FinalisesMono16KHzUtteranceAudio()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = new AmplitudeFakeVoiceActivityDetector(0.5f),
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 1,
            AutomaticContinuationGapMilliseconds = 2,
            AutomaticMaximumDuration = 1f,
            AutomaticFinalResult = "mono final",
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        int recordingStarts = 0;
        List<AutomaticSegmentResult> completions = [];
        int legacyCompletions = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((text, groupID, segmentIndex, continued) =>
                completions.Add(new AutomaticSegmentResult(text, groupID, segmentIndex, continued))));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(_ => legacyCompletions++));

        try
        {
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(sceneTree, () => recordingStarts == 1 && transcriber.IsRecording, maxFrames: 60);
            capture.PublishFrames(new Vector2[2048]);
            capture.PublishFrames(new Vector2[2048]);
            await WaitUntilAsync(sceneTree, () => completions.Count == 1, maxFrames: 120);

            RecordedAudioData recording = Assert.IsType<RecordedAudioData>(transcriber.AutomaticFinalRecording);
            Assert.Equal(16000, recording.SampleRate);
            Assert.Equal(1, recording.ChannelCount);
            Assert.True(recording.FrameCount > 0);
            // The endpoint is one millisecond after the final voiced detector frame, so retention is sliced at the
            // exact 16 kHz deadline rather than rounded up to a detector-frame boundary.
            Assert.Equal(16, recording.FrameCount % SileroVoiceActivityDetector.FrameSampleCount);
            Assert.Equal(0, Assert.Single(completions).SegmentIndex);
            Assert.Equal(0, legacyCompletions);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies zero configured pre-roll retains every qualified-onset frame through the real 16 kHz capture and
    /// resample path, including the exact endpoint frame which closes the first segment.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_ZeroPreRoll_RetainsCandidateFramesThroughQualificationWithoutDropOrDuplication()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        CapturingSequenceVoiceActivityDetector detector = new([true, true, true, false]);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = detector,
            InputMode = VoiceInputMode.AutomaticOnly,
            AutomaticPreRollMilliseconds = 0,
            AutomaticMinimumVoicedMilliseconds = 96,
            AutomaticEndpointSilenceMilliseconds = 32,
            AutomaticContinuationGapMilliseconds = 100,
            AutomaticMaximumDuration = 1f,
            AutomaticFinalResult = "qualified capture",
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        int sourceFramesPerRegion = checked((int)Math.Ceiling(
            SileroVoiceActivityDetector.FrameSampleCount * AudioServer.GetMixRate() / StreamingMonoResampler.TargetSampleRate) + 33);
        Vector2[] source =
        [
            .. CreateStereoRegion(0.2f, sourceFramesPerRegion),
            .. CreateStereoRegion(0.4f, sourceFramesPerRegion),
            .. CreateStereoRegion(0.7f, sourceFramesPerRegion),
            .. CreateStereoRegion(0f, sourceFramesPerRegion),
        ];

        try
        {
            capture.PublishFrames(source);
            await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 1, maxFrames: 60);
            await WaitUntilAsync(sceneTree, () => !transcriber.IsTranscribing, maxFrames: 90);

            RecordedAudioData recording = Assert.IsType<RecordedAudioData>(transcriber.AutomaticFinalRecording);
            Assert.Equal(StreamingMonoResampler.TargetSampleRate, recording.SampleRate);
            Assert.Equal(1, recording.ChannelCount);

            // The detector observes the actual resampled frames, providing an independent capture-path oracle for
            // candidate onset, qualification, and the endpoint frame rather than reconstructing Transcriber's buffer.
            Assert.True(detector.ProcessedFrames.Count >= 4);
            short[] expected = [.. detector.ProcessedFrames.Take(4).SelectMany(frame => frame.Select(ToPCM16))];
            short[] actual = GetPCM16Samples(recording);
            Assert.Equal(4 * SileroVoiceActivityDetector.FrameSampleCount, actual.Length);
            Assert.Equal(expected, actual);

            // The three candidate/qualification regions remain visibly distinct in the recorded 16 kHz payload.
            Assert.True(MeanAbsolute(expected.AsSpan(0, 512)) < MeanAbsolute(expected.AsSpan(512, 512)));
            Assert.True(MeanAbsolute(expected.AsSpan(512, 512)) < MeanAbsolute(expected.AsSpan(1024, 512)));
            Assert.Equal(expected.AsSpan(1536, 512).ToArray(), actual.AsSpan(1536, 512).ToArray());
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies detector initialisation failure in ButtonAndAutomatic warns exactly once, keeps push-to-talk fully
    /// functional, and never repeats the warning per frame.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_WhenDetectorCreationFails_ButtonAndAutomaticKeepsPushToTalkAndWarnsOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            CreateDetectorException = new FileNotFoundException("The committed Silero model was not found."),
            NextResultFactory = _ => Task.FromResult("manual survives"),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        transcriber.RecordButton = new StringName("speech_record");
        List<string> completions = [];
        int failures = 0;
        int recordingStarts = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(completions.Add));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(_ => failures++));

        try
        {
            // Sustained loud audio can never open an automatic window: monitoring is latched unavailable.
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048 * 4)]);
            await WaitForFramesAsync(sceneTree, 10);
            Assert.False(transcriber.IsRecording);
            Assert.Equal(0, recordingStarts);

            IReadOnlyList<string> notifications = GetNotificationTexts(fixture.NotificationWidget);
            int warningCount = notifications.Count(text => text == "Automatic voice detection is unavailable. Use push-to-talk.");
            Assert.Equal(1, warningCount);
            Assert.Equal(0, failures);

            // Push-to-talk stays fully functional through the same transcriber.
            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitForNextFrameAsync(sceneTree);
            Assert.True(transcriber.IsRecording);
            capture.PublishFrames([new Vector2(0.5f, 0.5f)]);
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => completions.Count == 1 && !transcriber.IsTranscribing,
                maxFrames: 90);
            Assert.Equal(["manual survives"], completions);
            Assert.Equal(1, recordingStarts);

            // The warning neither duplicates toasts nor repeats per frame while capture keeps flowing.
            await WaitForFramesAsync(sceneTree, 10);
            int repeatedWarningCount = GetNotificationTexts(fixture.NotificationWidget)
                .Count(text => text == "Automatic voice detection is unavailable. Use push-to-talk.");
            Assert.Equal(1, repeatedWarningCount);
            Assert.Equal(0, failures);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies detector initialisation failure in AutomaticOnly warns once, emits exactly one failure signal, and
    /// latches unavailable instead of silently idling.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_WhenDetectorCreationFails_AutomaticOnlyWarnsOnceFailsOnceAndLatches()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            InputMode = VoiceInputMode.AutomaticOnly,
            Enabled = false,
            CreateDetectorException = new FileNotFoundException("The committed Silero model was not found."),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<string> failures = [];
        int recordingStarts = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(failures.Add));

        try
        {
            // Initialisation runs only once the node is enabled, after the failure handler is connected.
            transcriber.Enabled = true;
            transcriber._Process(0);
            await WaitUntilAsync(
                sceneTree,
                () => failures.Count == 1 && !transcriber.IsRecording && !transcriber.IsTranscribing,
                maxFrames: 30);
            Assert.Equal(["Automatic voice detection is unavailable."], failures);
            int automaticOnlyWarningCount = GetNotificationTexts(fixture.NotificationWidget)
                .Count(text => text == "Automatic voice detection is unavailable.");
            Assert.Equal(1, automaticOnlyWarningCount);

            // Sustained loud audio never opens a window and never repeats the warning or failure.
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048 * 4)]);
            await WaitForFramesAsync(sceneTree, 10);
            Assert.False(transcriber.IsRecording);
            Assert.Equal(0, recordingStarts);
            _ = Assert.Single(failures);
            int repeatedAutomaticOnlyWarningCount = GetNotificationTexts(fixture.NotificationWidget)
                .Count(text => text == "Automatic voice detection is unavailable.");
            Assert.Equal(1, repeatedAutomaticOnlyWarningCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a mid-session detector failure in ButtonAndAutomatic abandons the open automatic utterance through
    /// exactly one failure signal — so the player speaking window its RecordingStarted opened closes — still warns
    /// exactly once without a duplicate toast, and keeps manual push-to-talk fully functional.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_WhenDetectorFailsMidSessionWithOpenUtterance_ClosesSpeakingWindowOnceAndKeepsPushToTalk()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        AmplitudeFakeVoiceActivityDetector detector = new(0.5f);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = detector,
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 1,
            AutomaticContinuationGapMilliseconds = 2,
            AutomaticMaximumDuration = 1f,
            NextResultFactory = _ => Task.FromResult("manual survives mid-session failure"),
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        VoiceSpeakingWindowIntegrationTests.WindowTestPlayerVoice voice = new()
        {
            Name = "PlayerVoice",
            Transcriber = transcriber,
        };
        fixture.Global.AddChild(voice);
        transcriber.RecordButton = new StringName("speech_record");
        List<string> completions = [];
        List<string> failures = [];
        int recordingStarts = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionCompleted,
            Callable.From<string>(completions.Add));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(failures.Add));

        try
        {
            // A qualified automatic onset opens the player's public speaking window through RecordingStarted.
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(
                sceneTree,
                () => recordingStarts == 1 && transcriber.IsRecording && voice.IsSpeaking,
                maxFrames: 60);

            // A mid-session inference failure latches automatic input unavailable and abandons the open utterance.
            detector.FailOnNextProcess(new InvalidOperationException("Silero inference failed mid-session"));
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
            await WaitUntilAsync(
                sceneTree,
                () => failures.Count == 1
                    && !voice.IsSpeaking
                    && !transcriber.IsRecording
                    && !transcriber.IsTranscribing
                    && !transcriber.IsFinalising,
                maxFrames: 60);

            Assert.Equal(["Automatic voice detection is unavailable. Use push-to-talk."], failures);
            Assert.Equal(0, transcriber.AutomaticFinalisationCallCount);

            // The structured warning fired exactly once and the failure signal added no duplicate toast.
            Assert.Equal(
                1,
                GetNotificationTexts(fixture.NotificationWidget)
                    .Count(text => text == "Automatic voice detection is unavailable. Use push-to-talk."));

            // Sustained loud audio cannot reopen a window while monitoring stays latched unavailable.
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048 * 4)]);
            await WaitForFramesAsync(sceneTree, 10);
            Assert.False(voice.IsSpeaking);
            Assert.Equal(1, recordingStarts);
            _ = Assert.Single(failures);
            Assert.Equal(
                1,
                GetNotificationTexts(fixture.NotificationWidget)
                    .Count(text => text == "Automatic voice detection is unavailable. Use push-to-talk."));

            // Manual push-to-talk keeps working through the same transcriber after the latch.
            fixture.LeftController.TriggerActionButtonPressed("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => recordingStarts == 2 && transcriber.IsRecording && voice.IsSpeaking,
                maxFrames: 30);
            capture.PublishFrames([new Vector2(0.5f, 0.5f)]);
            fixture.LeftController.TriggerActionButtonReleased("speech_record");
            await WaitUntilAsync(
                sceneTree,
                () => completions.Count == 1 && !transcriber.IsTranscribing && !voice.IsSpeaking,
                maxFrames: 90);

            Assert.Equal(["manual survives mid-session failure"], completions);
            Assert.Equal(2, recordingStarts);
            _ = Assert.Single(failures);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a detector failing persistently — every complete frame inside one drain throws — latches automatic
    /// input unavailable in AutomaticOnly through exactly one warning toast and exactly one failure signal.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_WhenDetectorFailsPersistentlyWithinOneDrain_AutomaticOnlyWarnsAndFailsExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        AmplitudeFakeVoiceActivityDetector detector = new(0.5f);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = detector,
            InputMode = VoiceInputMode.AutomaticOnly,
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<string> failures = [];
        int recordingStarts = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(failures.Add));

        try
        {
            // Silent priming accumulates remainder in the 512-sample frame accumulator across drains so the failing
            // drain below completes at least two detector frames (standard 44.1/48 kHz mix rates resample one
            // 2048-frame drain to roughly 680-740 samples).
            await PrimeSilentAutomaticAudioAsync(sceneTree, capture);
            detector.FailEveryProcess(new InvalidOperationException("Silero session faulted persistently"));
            capture.PublishFrames([.. Enumerable.Repeat(Vector2.Zero, 2048)]);
            await WaitUntilAsync(
                sceneTree,
                () => failures.Count == 1 && !transcriber.IsRecording && !transcriber.IsTranscribing,
                maxFrames: 30);

            // The re-entry path ran: a second complete frame reached the failing detector inside the same drain.
            Assert.True(
                detector.FailureCount >= 2,
                $"Expected the failing drain to process at least two frames, but it failed {detector.FailureCount} time(s).");
            Assert.Equal(["Automatic voice detection is unavailable."], failures);
            Assert.Equal(0, recordingStarts);
            Assert.Equal(
                1,
                GetNotificationTexts(fixture.NotificationWidget)
                    .Count(text => text == "Automatic voice detection is unavailable."));

            // Further capture flow cannot repeat the warning toast or the failure signal while the latch holds.
            capture.PublishFrames([.. Enumerable.Repeat(Vector2.Zero, 2048 * 2)]);
            await WaitForFramesAsync(sceneTree, 10);
            _ = Assert.Single(failures);
            Assert.Equal(
                1,
                GetNotificationTexts(fixture.NotificationWidget)
                    .Count(text => text == "Automatic voice detection is unavailable."));
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies a detector failing persistently across frames inside one drain in ButtonAndAutomatic — with no open
    /// utterance — warns exactly once through the notification host and never emits a failure signal.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_WhenDetectorFailsPersistentlyWithinOneDrain_ButtonAndAutomaticWithoutUtteranceWarnsOnceWithoutFailureSignal()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        AmplitudeFakeVoiceActivityDetector detector = new(0.5f);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = detector,
            InputMode = VoiceInputMode.ButtonAndAutomatic,
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        List<string> failures = [];
        int recordingStarts = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.TranscriptionFailed,
            Callable.From<string>(failures.Add));

        try
        {
            await PrimeSilentAutomaticAudioAsync(sceneTree, capture);
            detector.FailEveryProcess(new InvalidOperationException("Silero session faulted persistently"));
            capture.PublishFrames([.. Enumerable.Repeat(Vector2.Zero, 2048)]);
            await WaitUntilAsync(
                sceneTree,
                () => detector.FailureCount >= 1 && !transcriber.IsRecording,
                maxFrames: 30);

            // The re-entry path ran: a second complete frame reached the failing detector inside the same drain.
            Assert.True(
                detector.FailureCount >= 2,
                $"Expected the failing drain to process at least two frames, but it failed {detector.FailureCount} time(s).");
            Assert.Empty(failures);
            Assert.Equal(0, recordingStarts);
            Assert.Equal(
                1,
                GetNotificationTexts(fixture.NotificationWidget)
                    .Count(text => text == "Automatic voice detection is unavailable. Use push-to-talk."));

            // Further capture flow cannot repeat the warning toast or surface a failure signal.
            capture.PublishFrames([.. Enumerable.Repeat(Vector2.Zero, 2048 * 2)]);
            await WaitForFramesAsync(sceneTree, 10);
            Assert.Empty(failures);
            Assert.False(transcriber.IsRecording);
            Assert.Equal(
                1,
                GetNotificationTexts(fixture.NotificationWidget)
                    .Count(text => text == "Automatic voice detection is unavailable. Use push-to-talk."));
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Publishes silent single-drain capture batches until each is fully drained, accumulating frame-buffer remainder
    /// so a later failing drain completes at least two 512-sample detector frames.
    /// </summary>
    private static async Task PrimeSilentAutomaticAudioAsync(SceneTree sceneTree, FakeAudioFrameCapture capture)
    {
        // Each 2048-frame batch is fully drained before the next publish so the monitoring clear between drains never
        // discards priming audio.
        for (int publish = 0; publish < 6; publish++)
        {
            capture.PublishFrames([.. Enumerable.Repeat(Vector2.Zero, 2048)]);
            await WaitUntilAsync(sceneTree, () => capture.FramesAvailable == 0, maxFrames: 10);
        }
    }

    /// <summary>
    /// Verifies the base automatic path runs the committed Silero model end to end and that the exported speech
    /// probability threshold propagates: a zero threshold qualifies any frame's onset.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_WithRealSileroModelAndZeroThreshold_QualifiesOnsetAndForceCloses()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticSpeechProbabilityThreshold = 0f,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 700,
            AutomaticContinuationGapMilliseconds = 2000,
            AutomaticMaximumDuration = 1f,
            AutomaticFinalResult = "silero final",
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        int recordingStarts = 0;
        List<AutomaticSegmentResult> completions = [];
        int legacyCompletions = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));
        _ = transcriber.Connect(
            Transcriber.SignalName.AutomaticSegmentCompleted,
            Callable.From<string, string, int, bool>((text, groupID, segmentIndex, continued) =>
                completions.Add(new AutomaticSegmentResult(text, groupID, segmentIndex, continued))));
        _ = transcriber.Connect(Transcriber.SignalName.TranscriptionCompleted, Callable.From<string>(_ => legacyCompletions++));

        try
        {
            // A zero threshold makes every frame voiced, so the first completed 512-sample frame qualifies the
            // onset and the one-second maximum duration later force-closes the utterance.
            for (int batch = 0; batch < 24; batch++)
            {
                capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.6f, 0.6f), 2048)]);
                await WaitForNextFrameAsync(sceneTree);
                if (recordingStarts == 1)
                {
                    break;
                }
            }

            Assert.Equal(1, recordingStarts);
            Assert.True(transcriber.IsRecording);

            // The sample clock only advances while capture keeps flowing, so feed frames until the maximum
            // duration force-closes the utterance and the single REST final settles.
            for (int batch = 0; batch < 90 && completions.Count == 0; batch++)
            {
                capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.6f, 0.6f), 2048)]);
                await WaitForNextFrameAsync(sceneTree);
            }

            await WaitUntilAsync(sceneTree, () => completions.Count == 1 && !transcriber.IsTranscribing, maxFrames: 120);
            AutomaticSegmentResult completion = Assert.Single(completions);
            Assert.Equal("silero final", completion.Text);
            Assert.Equal(0, completion.SegmentIndex);
            Assert.False(completion.Continued);
            Assert.Equal(0, legacyCompletions);

            RecordedAudioData recording = Assert.IsType<RecordedAudioData>(transcriber.AutomaticFinalRecording);
            Assert.Equal(16000, recording.SampleRate);
            Assert.Equal(1, recording.ChannelCount);
        }
        finally
        {
            await DestroyRuntimeSpeechFixtureAsync(sceneTree, fixture);
            await existingGlobalScope.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies the base automatic path with a maximum threshold stays silent on constant non-speech audio: the
    /// committed Silero model never reaches a probability of exactly one.
    /// </summary>
    [Fact]
    public async Task AutomaticInput_WithRealSileroModelAndUnitThreshold_DetectsNoOnsetInConstantAudio()
    {
        SceneTree sceneTree = GetSceneTree();
        ExistingGlobalScope existingGlobalScope = await ExistingGlobalScope.CreateAsync(sceneTree);
        FakeAudioFrameCapture capture = new(framesAvailableAfterClear: 0);
        FakeOpenAITranscriber transcriber = new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticSpeechProbabilityThreshold = 1f,
            AutomaticMinimumVoicedMilliseconds = 0,
        };
        RuntimeSpeechFixture fixture = await CreateRuntimeSpeechFixtureAsync(sceneTree, transcriber);
        int recordingStarts = 0;
        _ = transcriber.Connect(
            Transcriber.SignalName.RecordingStarted,
            Callable.From(() => recordingStarts++));

        try
        {
            capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048 * 6)]);
            await WaitForFramesAsync(sceneTree, 30);

            Assert.False(transcriber.IsRecording);
            Assert.Equal(0, recordingStarts);
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

    private static FakeOpenAITranscriber CreateAutomaticTranscriber(
        FakeAudioFrameCapture capture,
        IVoiceActivityDetector detector)
        => new()
        {
            Name = "Transcriber",
            AudioCaptureForTesting = capture,
            VoiceActivityDetectorForTesting = detector,
            InputMode = VoiceInputMode.ButtonAndAutomatic,
            AutomaticMinimumVoicedMilliseconds = 0,
            AutomaticPreRollMilliseconds = 0,
            AutomaticEndpointSilenceMilliseconds = 1,
            AutomaticContinuationGapMilliseconds = 200,
            AutomaticMaximumDuration = 1f,
        };

    private static async Task CaptureTwoSegmentAutomaticGroupAsync(
        SceneTree sceneTree,
        FakeAudioFrameCapture capture,
        FakeOpenAITranscriber transcriber)
    {
        capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.8f, 0.8f), 2048)]);
        await WaitUntilAsync(sceneTree, () => transcriber.IsRecording, maxFrames: 60);
        capture.PublishFrames(new Vector2[2048]);
        await WaitForNextFrameAsync(sceneTree);
        capture.PublishFrames(new Vector2[2048]);
        await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 1, maxFrames: 30);

        capture.PublishFrames([.. Enumerable.Repeat(new Vector2(0.6f, 0.6f), 2048)]);
        await WaitForNextFrameAsync(sceneTree);
        capture.PublishFrames(new Vector2[2048]);
        await WaitForNextFrameAsync(sceneTree);
        capture.PublishFrames(new Vector2[2048]);
        await WaitUntilAsync(sceneTree, () => transcriber.AutomaticFinalisationCallCount == 2, maxFrames: 30);
        await CloseAutomaticGroupAsync(sceneTree, capture, transcriber);
    }

    private static async Task CloseAutomaticGroupAsync(
        SceneTree sceneTree,
        FakeAudioFrameCapture capture,
        FakeOpenAITranscriber transcriber)
    {
        for (int batch = 0; batch < 12 && transcriber.IsRecording; batch++)
        {
            capture.PublishFrames(new Vector2[2048]);
            await WaitForNextFrameAsync(sceneTree);
        }

        await WaitUntilAsync(sceneTree, () => !transcriber.IsRecording, maxFrames: 30);
    }

    private static short[] GetPCM16Samples(RecordedAudioData recording)
    {
        ReadOnlySpan<byte> bytes = recording.PCMData.Span;
        short[] samples = new short[bytes.Length / sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * sizeof(short), sizeof(short)));
        }

        return samples;
    }

    private static IEnumerable<Vector2> CreateStereoRegion(float value, int count)
        => Enumerable.Repeat(new Vector2(value, value), count);

    private static short ToPCM16(float sample)
        => (short)MathF.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);

    private static double MeanAbsolute(ReadOnlySpan<short> samples)
    {
        double total = 0;
        foreach (short sample in samples)
        {
            total += Math.Abs(sample);
        }

        return total / samples.Length;
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
        } = _ => Task.FromResult("manual result");

        public string? AutomaticFinalResult
        {
            get;
            set;
        }

        public Exception? AutomaticFinalException
        {
            get;
            set;
        }

        public Func<RecordedAudioData, int, CancellationToken, Task<string>>? AutomaticResultFactory
        {
            get;
            set;
        }

        public List<RecordedAudioData> AutomaticFinalRecordings { get; } = [];

        public RecordedAudioData? AutomaticFinalRecording
        {
            get;
            private set;
        }

        public Exception? CreateDetectorException
        {
            get;
            set;
        }

        /// <summary>
        /// When set, both overrides route through the real multipart REST boundary over a stub transport — emitting
        /// the STT dispatch marker exactly as production does — instead of returning the synthetic result directly.
        /// </summary>
        public bool RouteThroughRestBoundary
        {
            get;
            set;
        }

        public int TranscribeCallCount
        {
            get;
            private set;
        }

        public int AutomaticFinalisationCallCount
        {
            get;
            private set;
        }

        public override Task<string> Transcribe(RecordedAudioData recording)
        {
            TranscribeCallCount++;
            return RouteThroughRestBoundary
                ? TranscribeThroughRestBoundaryAsync(
                    recording,
                    ManualDispatchSource,
                    NextResultFactory(recording),
                    CancellationToken.None)
                : NextResultFactory(recording);
        }

        protected override IVoiceActivityDetector? CreateVoiceActivityDetector(int sampleRate, AutomaticVoiceInputOptions options)
            => CreateDetectorException is { } exception
                ? throw exception
                : base.CreateVoiceActivityDetector(sampleRate, options);

        protected override Task<string> FinaliseAutomaticUtteranceAsync(
            RecordedAudioData recording,
            CancellationToken cancellationToken)
        {
            AutomaticFinalRecording = recording;
            AutomaticFinalRecordings.Add(recording);
            AutomaticFinalisationCallCount++;
            return AutomaticResultFactory is { } automaticResultFactory
                ? automaticResultFactory(recording, AutomaticFinalisationCallCount - 1, cancellationToken)
                : RouteThroughRestBoundary
                ? TranscribeThroughRestBoundaryAsync(
                    recording,
                    AutomaticDispatchSource,
                    Task.FromResult(AutomaticFinalResult ?? "automatic final"),
                    cancellationToken)
                : AutomaticFinalException is { } exception
                    ? Task.FromException<string>(exception)
                    : Task.FromResult(AutomaticFinalResult ?? "automatic final");
        }

        private static async Task<string> TranscribeThroughRestBoundaryAsync(
            RecordedAudioData recording,
            string dispatchSourceMode,
            Task<string> pendingResult,
            CancellationToken cancellationToken)
        {
            using ResultFactoryTranscriptionHandler handler = new(pendingResult);
            using System.Net.Http.HttpClient httpClient = new(handler);
            OpenAIClientOptions clientOptions = new()
            {
                Endpoint = new Uri("https://rest-boundary.test/v1"),
                Transport = new HttpClientPipelineTransport(httpClient),
            };
            AudioClient audioClient = new(
                "whisper-1",
                new ApiKeyCredential("rest-boundary-test-key"),
                clientOptions);
            OpenAITranscriberSettings settings = new(
                Host: "https://rest-boundary.test/v1",
                ApiKey: null,
                Model: "whisper-1",
                Language: null,
                Prompt: null,
                Hotwords: null,
                TimeoutSeconds: null);
            return await TranscribeViaMultipartAsync(
                audioClient,
                recording,
                settings,
                cancellationToken,
                dispatchSourceMode);
        }

        private sealed class ResultFactoryTranscriptionHandler(Task<string> pendingResult) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _ = request;
                _ = cancellationToken;
                string text = await pendingResult;
                byte[] responseBody = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    text
                });
                ByteArrayContent content = new(responseBody);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                };
            }
        }
    }

    /// <summary>A deterministic detector stand-in that voices frames by peak amplitude and can fail on demand.</summary>
    private sealed class AmplitudeFakeVoiceActivityDetector(float amplitudeThreshold) : IVoiceActivityDetector
    {
        private Exception? _failOnNextProcess;
        private Exception? _failEveryProcess;

        /// <summary>Arms a mid-session inference failure consumed by the next processed frame.</summary>
        public void FailOnNextProcess(Exception exception) => _failOnNextProcess = exception;

        /// <summary>Arms a persistent inference failure consumed by every subsequent processed frame.</summary>
        public void FailEveryProcess(Exception exception) => _failEveryProcess = exception;

        /// <summary>Gets the number of processed frames that threw an armed inference failure.</summary>
        public int FailureCount
        {
            get;
            private set;
        }

        public VoiceActivityDetection Process(ReadOnlySpan<float> samples)
        {
            if (_failEveryProcess is { } persistent)
            {
                FailureCount++;
                throw persistent;
            }

            if (_failOnNextProcess is { } exception)
            {
                _failOnNextProcess = null;
                FailureCount++;
                throw exception;
            }

            float peak = 0f;
            foreach (float sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            bool isVoiced = peak >= amplitudeThreshold;
            return new VoiceActivityDetection(isVoiced, isVoiced ? 0.99f : 0.01f);
        }

        public void Reset()
        {
        }
    }

    /// <summary>Returns a scripted sequence while retaining the real 16 kHz frames supplied by Transcriber.</summary>
    private sealed class CapturingSequenceVoiceActivityDetector(IEnumerable<bool> detections) : IVoiceActivityDetector
    {
        private readonly Queue<bool> _detections = new(detections);

        public List<float[]> ProcessedFrames { get; } = [];

        public VoiceActivityDetection Process(ReadOnlySpan<float> samples)
        {
            ProcessedFrames.Add(samples.ToArray());
            bool isVoiced = _detections.Count == 0 || _detections.Dequeue();
            return new VoiceActivityDetection(isVoiced, isVoiced ? 0.99f : 0.01f);
        }

        public void Reset()
        {
        }
    }

    private sealed class FakeAudioFrameCapture(int framesAvailableAfterClear) : IAudioFrameCapture
    {
        private readonly Queue<Vector2> _publishedFrames = [];
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
            for (int index = 0; index < frames.Length; index++)
            {
                frames[index] = _publishedFrames.Count > 0 ? _publishedFrames.Dequeue() : FrameValue;
            }

            FramesAvailable -= count;
            return frames;
        }

        public void PublishFrames(int frameCount) => FramesAvailable += frameCount;

        public void PublishFrames(IEnumerable<Vector2> frames)
        {
            foreach (Vector2 frame in frames)
            {
                _publishedFrames.Enqueue(frame);
                FramesAvailable++;
            }
        }

        public void Clear()
        {
            ClearCallCount++;
            _publishedFrames.Clear();
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

    private sealed partial class SpeechPublicationObserver : Node, IHearing
    {
        private readonly List<SpeechPublication> _events = [];

        public IReadOnlyList<SpeechPublication> Events => _events;

        public bool IsSpeakingAtPublication
        {
            get;
            private set;
        }

        public IReadOnlyList<Type> PerceptTypes { get; } = [typeof(SpeechPercept)];

        public override void _Ready() => AddToGroup(IHearing.GroupName);

        public override void _ExitTree() => RemoveFromGroup(IHearing.GroupName);

#pragma warning disable CS0067
        public event Action<IPercept>? Perceived;
#pragma warning restore CS0067

        public void ReceiveVoice(string speech, IVoice source)
            => ReceiveVoice(speech, source, null);

        public void ReceiveVoice(string speech, IVoice source, SpeechSegmentMetadata? metadata)
        {
            IsSpeakingAtPublication = source.IsSpeaking;
            _events.Add(new SpeechPublication(speech, metadata));
        }
    }

    private sealed record RuntimeSpeechFixture(
        Node Global,
        XRManager XRManager,
        Transcriber Transcriber,
        FakeXRHandController LeftController,
        NotificationWidget NotificationWidget);

    private sealed record AutomaticSegmentResult(string Text, string SpeechGroupID, int SegmentIndex, bool Continued);

    private sealed record AutomaticSegmentFailure(string Error, string SpeechGroupID, int SegmentIndex, bool Continued);

    private sealed record SpeechPublication(string Speech, SpeechSegmentMetadata? Metadata);

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
        private readonly XRControllerHandTracking _handTracking;

        public FakeXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            LeftControllerNode = new FakeXRHandController();
            RightControllerNode = new FakeXRHandController();
            _handTracking = new XRControllerHandTracking(RightControllerNode, LeftControllerNode);
        }

        public IXROrigin Origin => new FakeXROrigin(OriginNode);

        public IXRCamera Camera => new FakeXRCamera(CameraNode);

        public IXRHandController RightHandController => RightControllerNode;

        public IXRHandController LeftHandController => LeftControllerNode;

        public XRHandTrackingMode HandTrackingMode => XRHandTrackingMode.Controller;

        public IXRHandJointProvider OpticalHandJoints => XREmptyHandJointProvider.Instance;

        public IXRHandPoseSource GetHandPoseSource(LimbSide side) => _handTracking.GetHandPoseSource(side);

#pragma warning disable CS0067
        public event Action? PoseRecentered;

        public event Action? HandTrackingModeChanged;
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
