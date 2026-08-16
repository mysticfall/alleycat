using System.Diagnostics;
using AlleyCat.Core.Logging;
using AlleyCat.Diagnostics;
using AlleyCat.Rigging;
using AlleyCat.UI;
using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Base XR speech-transcription component that records microphone input and dispatches transcription requests.
/// </summary>
public abstract partial class Transcriber : Node
{
    private const string DefaultRecordingBusName = "SpeechRecord";
    private const float CaptureBufferLengthSeconds = 0.5f;
    private const int CaptureBatchFrames = 2048;
    private const int FinalisationFallbackProcessFrames = 4;
    private XRManager? _xrManager;
    private IXRHandController? _recordController;
    private IAudioFrameCapture? _audioCapture;
    private PCMAudioAccumulator? _audioAccumulator;
    private AudioStreamPlayer? _microphonePlayer;
    private Godot.Timer? _maxDurationTimer;
    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private bool _xrInitialised;
    private bool _isBound;
    private bool _acceptsDeferredGodotActions;
    private volatile bool _isExitingTree;
    private long _lifetimeGeneration;
    private Stopwatch? _recordingStopwatch;
    private ILogger<Transcriber>? _logger;
    private IAudioMixClock _audioMixClock = new GodotAudioMixClock();
    private double _finalisationMixTime;
    private int _finalisationProcessFrames;

    internal IAudioFrameCapture? AudioCaptureForTesting
    {
        get;
        set;
    }

    internal Action<TranscriberPipelineStage, TimeSpan>? PipelineStageMeasuredForTesting
    {
        get;
        set;
    }

    internal Action? DeferredGodotActionQueuedForTesting
    {
        get;
        set;
    }

    internal Action? DeferredGodotActionExecutingForTesting
    {
        get;
        set;
    }

    internal Action<TranscriberLifecycleState, bool, int>? LifecycleStateChangedForTesting
    {
        get;
        set;
    }

    internal bool PauseDeferredGodotActionFlushForTesting
    {
        get;
        set;
    }

    internal IAudioMixClock AudioMixClockForTesting
    {
        set => _audioMixClock = value;
    }

    /// <summary>
    /// Emitted when a transcription request completes successfully.
    /// </summary>
    [Signal]
    public delegate void TranscriptionCompletedEventHandler(string text);

    /// <summary>
    /// Emitted when a transcription request fails.
    /// </summary>
    [Signal]
    public delegate void TranscriptionFailedEventHandler(string error);

    /// <summary>
    /// Emitted when microphone recording begins.
    /// </summary>
    [Signal]
    public delegate void RecordingStartedEventHandler();

    /// <summary>
    /// XR controller hand used for microphone recording.
    /// </summary>
    [Export]
    public LimbSide RecordHand
    {
        get;
        set;
    } = LimbSide.Left;

    /// <summary>
    /// XR action button used to begin and end microphone capture.
    /// </summary>
    [Export]
    public StringName RecordButton
    {
        get;
        set;
    } = new("trigger_click");

    /// <summary>
    /// Maximum recording duration before capture auto-stops and transcribes.
    /// </summary>
    [Export(PropertyHint.Range, "0.5,120,0.1")]
    public float MaxRecordingDuration
    {
        get;
        set;
    } = 15f;

    /// <summary>
    /// Audio bus used to route microphone capture into a record effect.
    /// </summary>
    [Export]
    public string RecordingBusName
    {
        get;
        set;
    } = DefaultRecordingBusName;

    /// <summary>
    /// Enables XR recording input and transcription request dispatch.
    /// </summary>
    [Export]
    public bool Enabled
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Enables posting successful transcript text to the notification UI for debugging and opt-in diagnostics.
    /// </summary>
    [Export]
    public bool TranscriptNotificationEnabled
    {
        get;
        set;
    }

    /// <summary>
    /// Indicates whether microphone capture is active.
    /// </summary>
    public bool IsRecording
    {
        get;
        private set;
    }

    /// <summary>
    /// Indicates whether capture has stopped and the final audio mix is awaiting its bounded drain.
    /// </summary>
    public bool IsFinalising
    {
        get;
        private set;
    }

    /// <summary>
    /// Indicates whether a transcription request is currently in flight.
    /// </summary>
    public bool IsTranscribing
    {
        get;
        private set;
    }

    /// <summary>
    /// Converts recorded managed PCM audio into transcribed text.
    /// </summary>
    /// <param name="recording">Recorded PCM16 audio.</param>
    /// <returns>Transcribed text.</returns>
    public abstract Task<string> Transcribe(RecordedAudioData recording);

    /// <summary>
    /// Hook invoked after microphone capture begins.
    /// </summary>
    protected virtual void OnRecordingStarted()
    {
    }

    /// <summary>
    /// Hook invoked after microphone capture stops.
    /// </summary>
    protected virtual void OnRecordingStopped()
    {
    }

    /// <summary>
    /// Hook invoked after a transcription succeeds on the main thread.
    /// </summary>
    /// <param name="text">Transcribed text.</param>
    protected virtual void OnTranscriptionCompleted(string text)
    {
    }

    /// <summary>
    /// Dispatches a Godot action through the deferred main-thread queue.
    /// </summary>
    /// <param name="action">Action to execute on the Godot thread.</param>
    /// <returns>Completion task for the queued action.</returns>
    protected Task DispatchDeferredGodotActionAsync(Action action)
    {
        long generation;

        lock (_deferredGodotActionsLock)
        {
            generation = _lifetimeGeneration;
        }

        return DispatchGodotActionAsync(action, generation);
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _isExitingTree = false;
        DeferredGodotAction[] staleActions;
        lock (_deferredGodotActionsLock)
        {
            _lifetimeGeneration++;
            _acceptsDeferredGodotActions = true;
            staleActions = DrainDeferredGodotActionsLocked();
        }

        CancelDeferredGodotActions(staleActions);
        _xrManager = ResolveXRManager();
        _audioCapture = AudioCaptureForTesting ?? EnsureAudioCapture();
        _microphonePlayer = CreateMicrophonePlayer();
        _maxDurationTimer = CreateMaxDurationTimer();

        _xrManager.Initialised += OnXRInitialised;

        if (_xrManager.InitialisationAttempted)
        {
            _xrInitialised = _xrManager.InitialisationSucceeded;

            if (!_xrInitialised)
            {
                GD.PushWarning($"{nameof(Transcriber)} skipped XR controller binding because XR initialisation failed.");
                SetProcess(false);
                return;
            }
        }

        if (_xrInitialised)
        {
            _isBound = TryBindController();
        }

        SetProcess(!_isBound);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        DeferredGodotAction[] pendingActions;
        lock (_deferredGodotActionsLock)
        {
            _isExitingTree = true;
            _acceptsDeferredGodotActions = false;
            _lifetimeGeneration++;
            pendingActions = DrainDeferredGodotActionsLocked();
        }

        CancelDeferredGodotActions(pendingActions);

        if (_xrManager is XRManager xrManager)
        {
            xrManager.Initialised -= OnXRInitialised;
        }

        StopRecordingInternal();
        SetLifecycleState(TranscriberLifecycleState.Transcribing, false);
        DisconnectController();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _ = delta;

        if (!_isBound && _xrInitialised)
        {
            _isBound = TryBindController();
        }

        if (IsRecording)
        {
            DrainCapture(CaptureBatchFrames);
        }

        else if (IsFinalising)
        {
            ProcessRecordingFinalisation();
        }

        FlushDeferredGodotActions();
    }

    private static XRManager ResolveXRManager()
        => Game.Instance.GetRequiredService<XRManager>();

    private IAudioFrameCapture EnsureAudioCapture()
    {
        string busName = string.IsNullOrWhiteSpace(RecordingBusName) ? DefaultRecordingBusName : RecordingBusName;
        int busIndex = AudioServer.GetBusIndex(busName);
        if (busIndex < 0)
        {
            busIndex = AudioServer.BusCount;
            AudioServer.AddBus(busIndex);
            AudioServer.SetBusName(busIndex, busName);
        }

        for (int effectIndex = 0; effectIndex < AudioServer.GetBusEffectCount(busIndex); effectIndex++)
        {
            if (AudioServer.GetBusEffect(busIndex, effectIndex) is AudioEffectCapture existingEffect)
            {
                existingEffect.BufferLength = CaptureBufferLengthSeconds;
                existingEffect.ClearBuffer();
                return new GodotAudioFrameCapture(existingEffect);
            }
        }

        AudioEffectCapture captureEffect = new()
        {
            BufferLength = CaptureBufferLengthSeconds,
        };
        AudioServer.AddBusEffect(busIndex, captureEffect);
        captureEffect.ClearBuffer();
        return new GodotAudioFrameCapture(captureEffect);
    }

    private AudioStreamPlayer CreateMicrophonePlayer()
    {
        AudioStreamPlayer microphonePlayer = new()
        {
            Name = nameof(Transcriber) + "MicrophonePlayer",
            Stream = new AudioStreamMicrophone(),
            Bus = string.IsNullOrWhiteSpace(RecordingBusName) ? DefaultRecordingBusName : RecordingBusName,
            Autoplay = false,
            ProcessMode = ProcessModeEnum.Always,
        };

        AddChild(microphonePlayer);
        return microphonePlayer;
    }

    private Godot.Timer CreateMaxDurationTimer()
    {
        Godot.Timer maxDurationTimer = new()
        {
            Name = nameof(Transcriber) + "MaxDurationTimer",
            OneShot = true,
            WaitTime = MaxRecordingDuration,
            ProcessCallback = Godot.Timer.TimerProcessCallback.Idle,
        };

        maxDurationTimer.Timeout += OnMaxDurationTimeout;
        AddChild(maxDurationTimer);
        return maxDurationTimer;
    }

    private bool TryBindController()
    {
        XRManager? xrManager = _xrManager;
        if (xrManager is null)
        {
            return false;
        }

        DisconnectController();

        _recordController = RecordHand == LimbSide.Left
            ? xrManager.Runtime.LeftHandController
            : xrManager.Runtime.RightHandController;

        _recordController.ActionButtonPressed += OnControllerButtonPressed;
        _recordController.ActionButtonReleased += OnControllerButtonReleased;
        _isBound = true;
        UpdateProcessing();
        return true;
    }

    private void DisconnectController()
    {
        if (_recordController is not null)
        {
            _recordController.ActionButtonPressed -= OnControllerButtonPressed;
            _recordController.ActionButtonReleased -= OnControllerButtonReleased;
            _recordController = null;
        }

        _isBound = false;
        UpdateProcessing();
    }

    private void OnXRInitialised(bool succeeded)
    {
        if (!succeeded)
        {
            GD.PushWarning($"{nameof(Transcriber)} skipped XR controller binding because XR initialisation failed.");
            SetProcess(false);
            return;
        }

        _xrInitialised = true;
        _isBound = TryBindController();

        if (!_isBound)
        {
            SetProcess(true);
        }
    }

    private void OnControllerButtonPressed(string actionName)
    {
        if (!Enabled)
        {
            return;
        }

        if (string.Equals(actionName, RecordButton.ToString(), StringComparison.Ordinal))
        {
            StartRecording();
        }
    }

    private void OnControllerButtonReleased(string actionName)
    {
        if (string.Equals(actionName, RecordButton.ToString(), StringComparison.Ordinal))
        {
            StopRecording();
        }
    }

    /// <summary>
    /// Begins microphone capture when the transcriber is idle.
    /// </summary>
    public void StartRecording()
    {
        if (!Enabled || IsRecording || IsFinalising || IsTranscribing)
        {
            return;
        }

        IAudioFrameCapture? audioCapture = _audioCapture;
        AudioStreamPlayer? microphonePlayer = _microphonePlayer;
        Godot.Timer? maxDurationTimer = _maxDurationTimer;
        if (audioCapture is null || microphonePlayer is null || maxDurationTimer is null)
        {
            return;
        }

        maxDurationTimer.Stop();
        float effectiveMaximumDuration = Math.Clamp(MaxRecordingDuration, 0.1f, 120f);
        maxDurationTimer.WaitTime = effectiveMaximumDuration;
        int sampleRate = Math.Max(1, (int)MathF.Round(AudioServer.GetMixRate()));
        int maximumFrames = checked((int)Math.Ceiling(sampleRate * effectiveMaximumDuration));
        _audioAccumulator = new PCMAudioAccumulator(maximumFrames);
        audioCapture.Clear();
        microphonePlayer.Play();
        maxDurationTimer.Start();
        SetLifecycleState(TranscriberLifecycleState.Recording, true);
        UpdateProcessing();
        _recordingStopwatch = AIPipelineDebugLog.StartTimer();
        AIPipelineDebugLog.Stage("STT recording started");
        _ = EmitSignal(SignalName.RecordingStarted);
        OnRecordingStarted();
    }

    /// <summary>
    /// Stops microphone capture and dispatches transcription when a recording is active.
    /// </summary>
    public void StopRecording() => RequestRecordingStop();

    private void StopRecordingInternal()
    {
        bool wasRecording = IsRecording;
        Stopwatch? recordingStopwatch = _recordingStopwatch;
        _maxDurationTimer?.Stop();

        StopMicrophonePlayer(wasRecording);
        CompleteRecordingLifecycle(wasRecording, recordingStopwatch);
        SetLifecycleState(TranscriberLifecycleState.Finalising, false);
        _finalisationProcessFrames = 0;
        _audioAccumulator = null;
        _audioCapture?.Clear();
        UpdateProcessing();
    }

    private void StopMicrophonePlayer(bool measureStage)
    {
        var stageStopwatch = Stopwatch.StartNew();
        _microphonePlayer?.Stop();
        if (measureStage)
        {
            RecordPipelineStage(
                TranscriberPipelineStage.MicrophonePlayerStop,
                "STT microphone player stopped in",
                stageStopwatch);
        }
    }

    private void CompleteRecordingLifecycle(bool wasRecording, Stopwatch? recordingStopwatch)
    {
        SetLifecycleState(TranscriberLifecycleState.Recording, false);
        UpdateProcessing();
        _recordingStopwatch = null;

        if (wasRecording)
        {
            if (recordingStopwatch is not null)
            {
                AIPipelineDebugLog.Latency("STT recording stopped after", recordingStopwatch);
            }

            OnRecordingStopped();
        }
    }

    private void RequestRecordingStop()
    {
        if (!IsRecording)
        {
            return;
        }

        IAudioFrameCapture? audioCapture = _audioCapture;
        PCMAudioAccumulator? audioAccumulator = _audioAccumulator;
        if (audioCapture is null || audioAccumulator is null)
        {
            StopRecordingInternal();
            return;
        }

        Stopwatch? recordingStopwatch = _recordingStopwatch;
        _maxDurationTimer?.Stop();
        StopMicrophonePlayer(measureStage: true);
        SetLifecycleState(TranscriberLifecycleState.Finalising, true);
        _finalisationMixTime = _audioMixClock.TimeSinceLastMix;
        _finalisationProcessFrames = 0;
        CompleteRecordingLifecycle(wasRecording: true, recordingStopwatch);
        UpdateProcessing();
    }

    private void ProcessRecordingFinalisation()
    {
        double currentMixTime = _audioMixClock.TimeSinceLastMix;
        _finalisationProcessFrames++;
        bool crossedMixBoundary = currentMixTime < _finalisationMixTime;
        bool reachedFallback = _finalisationProcessFrames >= FinalisationFallbackProcessFrames;
        _finalisationMixTime = currentMixTime;

        if (!crossedMixBoundary && !reachedFallback)
        {
            return;
        }

        SetLifecycleState(TranscriberLifecycleState.Finalising, false);
        UpdateProcessing();
        _ = FinaliseRecordingAndTranscribeAsync();
    }

    private async Task FinaliseRecordingAndTranscribeAsync()
    {
        IAudioFrameCapture? audioCapture = _audioCapture;
        PCMAudioAccumulator? audioAccumulator = _audioAccumulator;
        if (_isExitingTree || audioCapture is null || audioAccumulator is null)
        {
            return;
        }

        var finalDrainStopwatch = Stopwatch.StartNew();
        DrainFinalCapture();
        RecordPipelineStage(
            TranscriberPipelineStage.FinalCaptureDrain,
            "STT final capture batch drained in",
            finalDrainStopwatch);

        long discardedFrames = audioCapture.DiscardedFrames;
        if (discardedFrames > 0)
        {
            ResolveLogger()?.LogWarning(
                "Speech capture discarded {DiscardedFrames} frames because its ring buffer overflowed.",
                discardedFrames);
        }

        if (audioAccumulator.FrameCount == 0)
        {
            HandleTranscriptionFailure(new InvalidOperationException("Microphone recording contained no audio frames."));
            _audioAccumulator = null;
            audioCapture.Clear();
            return;
        }

        int sampleRate = Math.Max(1, (int)MathF.Round(AudioServer.GetMixRate()));
        RecordedAudioData recording = audioAccumulator.Complete(sampleRate);
        _audioAccumulator = null;
        audioCapture.Clear();
        if (AIPipelineDebugLog.IsEnabled)
        {
            AIPipelineDebugLog.Stage("STT managed recording completed", $"{recording.PCMData.Length} bytes");
        }

        await InvokeTranscriptionAsync(recording);
    }

    private async Task InvokeTranscriptionAsync(RecordedAudioData recording)
    {
        if (!Enabled || !TryGetActiveLifetimeGeneration(out long lifetimeGeneration))
        {
            return;
        }

        Stopwatch stopwatch = AIPipelineDebugLog.StartTimer();

        try
        {
            SetLifecycleState(TranscriberLifecycleState.Transcribing, true);
            UpdateProcessing();
            var dispatchStopwatch = Stopwatch.StartNew();
            Task<string> transcriptionTask = Task.Run(() => Transcribe(recording));
            RecordPipelineStage(
                TranscriberPipelineStage.WorkerDispatch,
                "STT worker dispatched in",
                dispatchStopwatch);

            string text = await transcriptionTask;
            var completionDispatchStopwatch = Stopwatch.StartNew();
            await DispatchGodotActionAsync(() =>
            {
                if (AIPipelineDebugLog.IsEnabled)
                {
                    AIPipelineDebugLog.Latency("STT completed in", stopwatch, $"{text.Length} chars");
                }

                SetLifecycleState(TranscriberLifecycleState.Transcribing, false);
                UpdateProcessing();
                HandleTranscriptionSuccess(text);
                RecordPipelineStage(
                    TranscriberPipelineStage.CompletionDispatch,
                    "STT completion dispatched in",
                    completionDispatchStopwatch);
            }, lifetimeGeneration);
        }
        catch (OperationCanceledException) when (!IsLifetimeActive(lifetimeGeneration))
        {
            // Teardown cancellation is an expected lifecycle outcome. _ExitTree owns state cleanup.
        }
        catch (Exception ex)
        {
            if (!IsLifetimeActive(lifetimeGeneration))
            {
                return;
            }

            await DispatchGodotActionAsync(() =>
            {
                AIPipelineDebugLog.Latency("STT failed after", stopwatch);
                SetLifecycleState(TranscriberLifecycleState.Transcribing, false);
                UpdateProcessing();
                HandleTranscriptionFailure(ex);
            }, lifetimeGeneration);
        }
    }

    private void DrainCapture(int maximumFrames)
    {
        IAudioFrameCapture? capture = _audioCapture;
        PCMAudioAccumulator? accumulator = _audioAccumulator;
        if (capture is null || accumulator is null || maximumFrames <= 0 || accumulator.RemainingFrames == 0)
        {
            return;
        }

        int framesToRead = (int)Math.Min(maximumFrames, Math.Min(capture.FramesAvailable, accumulator.RemainingFrames));
        if (framesToRead <= 0)
        {
            return;
        }

        Vector2[] frames = capture.ReadFrames(framesToRead);
        foreach (Vector2 frame in frames)
        {
            _ = accumulator.AppendStereoFrame(frame.X, frame.Y);
        }
    }

    private void DrainFinalCapture()
    {
        IAudioFrameCapture? capture = _audioCapture;
        PCMAudioAccumulator? accumulator = _audioAccumulator;
        if (capture is null || accumulator is null)
        {
            return;
        }

        int framesToRead = (int)Math.Min(
            CaptureBatchFrames,
            Math.Min(capture.FramesAvailable, accumulator.RemainingFrames));
        Vector2[] frames = capture.ReadFrames(framesToRead);
        foreach (Vector2 frame in frames)
        {
            _ = accumulator.AppendStereoFrame(frame.X, frame.Y);
        }
    }

    private void UpdateProcessing()
        => SetProcess((!_isBound && _xrInitialised) || IsRecording || IsFinalising || IsTranscribing);

    private void SetLifecycleState(TranscriberLifecycleState state, bool value)
    {
        bool currentValue = state switch
        {
            TranscriberLifecycleState.Recording => IsRecording,
            TranscriberLifecycleState.Finalising => IsFinalising,
            TranscriberLifecycleState.Transcribing => IsTranscribing,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };

        if (currentValue == value)
        {
            return;
        }

        switch (state)
        {
            case TranscriberLifecycleState.Recording:
                IsRecording = value;
                break;
            case TranscriberLifecycleState.Finalising:
                IsFinalising = value;
                break;
            case TranscriberLifecycleState.Transcribing:
                IsTranscribing = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        LifecycleStateChangedForTesting?.Invoke(state, value, System.Environment.CurrentManagedThreadId);
    }

    private ILogger<Transcriber>? ResolveLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<Transcriber>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    private void RecordPipelineStage(
        TranscriberPipelineStage stage,
        string diagnosticName,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        PipelineStageMeasuredForTesting?.Invoke(stage, stopwatch.Elapsed);
        AIPipelineDebugLog.Latency(diagnosticName, stopwatch);
    }

    private void OnMaxDurationTimeout() => RequestRecordingStop();

    private Task DispatchGodotActionAsync(Action action, long lifetimeGeneration)
    {
        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_deferredGodotActionsLock)
        {
            if (!_acceptsDeferredGodotActions || lifetimeGeneration != _lifetimeGeneration)
            {
                _ = completionSource.TrySetCanceled();
                return completionSource.Task;
            }

            _deferredGodotActions.Enqueue(new DeferredGodotAction(action, completionSource, lifetimeGeneration));
        }

        DeferredGodotActionQueuedForTesting?.Invoke();
        return completionSource.Task;
    }

    private void FlushDeferredGodotActions()
    {
        while (true)
        {
            DeferredGodotAction action;

            lock (_deferredGodotActionsLock)
            {
                if (PauseDeferredGodotActionFlushForTesting || _deferredGodotActions.Count == 0)
                {
                    return;
                }

                action = _deferredGodotActions.Dequeue();
                if (!_acceptsDeferredGodotActions || action.LifetimeGeneration != _lifetimeGeneration)
                {
                    _ = action.CompletionSource.TrySetCanceled();
                    continue;
                }
            }

            try
            {
                // The generation check is deliberately adjacent to this Godot-thread action boundary.
                DeferredGodotActionExecutingForTesting?.Invoke();
                action.Action();
                _ = action.CompletionSource.TrySetResult();
            }
            catch (Exception ex)
            {
                _ = action.CompletionSource.TrySetException(ex);
            }
        }
    }

    private bool TryGetActiveLifetimeGeneration(out long lifetimeGeneration)
    {
        lock (_deferredGodotActionsLock)
        {
            lifetimeGeneration = _lifetimeGeneration;
            return _acceptsDeferredGodotActions;
        }
    }

    private bool IsLifetimeActive(long lifetimeGeneration)
    {
        lock (_deferredGodotActionsLock)
        {
            return _acceptsDeferredGodotActions && lifetimeGeneration == _lifetimeGeneration;
        }
    }

    private DeferredGodotAction[] DrainDeferredGodotActionsLocked()
    {
        DeferredGodotAction[] actions = [.. _deferredGodotActions];
        _deferredGodotActions.Clear();
        return actions;
    }

    private static void CancelDeferredGodotActions(IEnumerable<DeferredGodotAction> actions)
    {
        foreach (DeferredGodotAction action in actions)
        {
            _ = action.CompletionSource.TrySetCanceled();
        }
    }

    private void HandleTranscriptionSuccess(string text)
    {
        if (TranscriptNotificationEnabled)
        {
            _ = this.PostNotification(text);
        }

        _ = EmitSignal(SignalName.TranscriptionCompleted, text);
        OnTranscriptionCompleted(text);
    }

    private void HandleTranscriptionFailure(Exception ex)
    {
        // Failure UX and signal emission must still run in isolated integration scenes without the Game provider;
        // diagnostics are explicitly optional only for this recovery path.
        if (ResolveLogger() is { } resolvedLogger)
        {
            resolvedLogger.LogError(
                ex,
                "Voice transcription failed while processing recorded microphone audio.");
        }

        _ = EmitSignal(SignalName.TranscriptionFailed, ex.Message);
    }

    private sealed class DeferredGodotAction(
        Action action,
        TaskCompletionSource completionSource,
        long lifetimeGeneration)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;

        public long LifetimeGeneration { get; } = lifetimeGeneration;
    }
}

internal enum TranscriberPipelineStage
{
    MicrophonePlayerStop,
    FinalCaptureDrain,
    WorkerDispatch,
    CompletionDispatch,
}

internal enum TranscriberLifecycleState
{
    Recording,
    Finalising,
    Transcribing,
}

internal interface IAudioFrameCapture
{
    long FramesAvailable
    {
        get;
    }

    long DiscardedFrames
    {
        get;
    }

    Vector2[] ReadFrames(int maximumFrames);

    void Clear();
}

internal interface IAudioMixClock
{
    double TimeSinceLastMix
    {
        get;
    }
}

internal sealed class GodotAudioMixClock : IAudioMixClock
{
    public double TimeSinceLastMix => AudioServer.GetTimeSinceLastMix();
}

internal sealed class GodotAudioFrameCapture(AudioEffectCapture effect) : IAudioFrameCapture
{
    public long FramesAvailable => effect.GetFramesAvailable();

    public long DiscardedFrames
    {
        get => Math.Max(0, effect.GetDiscardedFrames() - field);
        private set;
    }

    public Vector2[] ReadFrames(int maximumFrames)
        => maximumFrames > 0 ? effect.GetBuffer(maximumFrames) : [];

    public void Clear()
    {
        effect.ClearBuffer();
        DiscardedFrames = effect.GetDiscardedFrames();
    }
}
