using System.Diagnostics;
using AlleyCat.Core.Logging;
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
/// <remarks>
/// While the <see cref="SceneTree" /> is paused (for example while the main menu is open), record-button presses
/// are ignored through the built-in <see cref="Node.CanProcess()" /> guard, and an active recording is stopped and
/// finalised through the normal stop path. A trigger still held across the unpause boundary does not start a
/// recording; only a fresh release-then-press edge does.
/// </remarks>
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
    private IVoiceActivityDetector? _voiceActivityDetector;
    private AutomaticUtteranceCoordinator? _automaticUtteranceCoordinator;
    private MonoFloatPreRollBuffer? _automaticPreRollBuffer;
    private StreamingMonoResampler? _automaticResampler;
    private float[]? _automaticMonoScratch;
    private float[]? _automaticResampledScratch;
    private float[]? _automaticFrameBuffer;
    private int _automaticFrameBufferFill;
    private float[]? _automaticPreRollDrainScratch;
    private AutomaticUtterance? _activeAutomaticUtterance;
    private readonly Dictionary<Guid, CancellationTokenSource> _automaticFinalisationCancellations = [];
    private readonly Dictionary<Guid, AutomaticSpeechGroup> _automaticGroups = [];
    private int _pendingAutomaticFinalisations;
    private bool _manualSessionActive;
    private bool _automaticInputUnavailable;
    private long _automaticCaptureSampleIndex;
    private int _automaticSourceSampleRate;

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

    /// <summary>Overrides the automatic detector for deterministic runtime tests.</summary>
    internal IVoiceActivityDetector? VoiceActivityDetectorForTesting
    {
        get;
        set;
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
    /// Emitted when an in-progress manual recording stops without any public outcome, such as node teardown or an
    /// aborted finalisation, so listeners can settle their manual-session state exactly once.
    /// </summary>
    [Signal]
    public delegate void RecordingAbandonedEventHandler();

    /// <summary>Emitted for an ordered successful automatic segment outcome, including a blank result.</summary>
    [Signal]
    public delegate void AutomaticSegmentCompletedEventHandler(string text, string speechGroupID, int segmentIndex, bool continued);

    /// <summary>Emitted for an ordered failed automatic segment outcome.</summary>
    [Signal]
    public delegate void AutomaticSegmentFailedEventHandler(string error, string speechGroupID, int segmentIndex, bool continued);

    /// <summary>Emitted once a qualified automatic speech group opens.</summary>
    [Signal]
    public delegate void AutomaticGroupOpenedEventHandler(string speechGroupID);

    /// <summary>
    /// Emitted after a closed automatic group has settled every segment outcome.
    /// </summary>
    /// <param name="speechGroupID">The closed speech group identifier.</param>
    /// <param name="outcomesWerePublished">
    /// Whether every segment outcome was published before the group reached its continuation-gap closure. When false,
    /// the final segment outcome immediately follows this signal.
    /// </param>
    [Signal]
    public delegate void AutomaticGroupClosedEventHandler(string speechGroupID, bool outcomesWerePublished);

    /// <summary>Emitted when an automatic group is abandoned without public segment outcomes.</summary>
    [Signal]
    public delegate void AutomaticGroupAbandonedEventHandler(string speechGroupID);

    /// <summary>Textless lifecycle notification emitted synchronously when speech resumes within a group.</summary>
    [Signal]
    public delegate void AutomaticSpeechResumedEventHandler(string speechGroupID, int segmentIndex, bool continued);

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

    /// <summary>Selects the admitted manual and automatic microphone input mechanisms.</summary>
    [Export]
    public VoiceInputMode InputMode
    {
        get;
        set;
    } = VoiceInputMode.ButtonOnly;

    /// <summary>Silero speech-probability threshold a 16 kHz frame must meet to count as voiced.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float AutomaticSpeechProbabilityThreshold
    {
        get;
        set;
    } = 0.5f;

    /// <summary>Automatic onset pre-roll duration in milliseconds.</summary>
    [Export(PropertyHint.Range, "0,10000,10")]
    public int AutomaticPreRollMilliseconds
    {
        get;
        set;
    } = 250;

    /// <summary>Continuous voiced duration required to qualify automatic onset in milliseconds.</summary>
    [Export(PropertyHint.Range, "0,5000,10")]
    public int AutomaticMinimumVoicedMilliseconds
    {
        get;
        set;
    } = 120;

    /// <summary>Automatic endpoint-silence duration in milliseconds.</summary>
    [Export(PropertyHint.Range, "1,30000,10")]
    public int AutomaticEndpointSilenceMilliseconds
    {
        get;
        set;
    } = 700;

    /// <summary>Maximum automatic continuation gap in milliseconds.</summary>
    [Export(PropertyHint.Range, "1,60000,10")]
    public int AutomaticContinuationGapMilliseconds
    {
        get;
        set;
    } = 2000;

    /// <summary>Maximum duration of one automatic utterance in seconds.</summary>
    [Export(PropertyHint.Range, "1,120,0.1")]
    public float AutomaticMaximumDuration
    {
        get;
        set;
    } = 30f;

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
    /// Creates the local Silero detector used by automatic monitoring, loading the committed model from its fixed
    /// path. Derived backends may override it, returning null to disable the automatic path while manual
    /// push-to-talk keeps working.
    /// </summary>
    protected virtual IVoiceActivityDetector? CreateVoiceActivityDetector(int sampleRate, AutomaticVoiceInputOptions options)
        => new SileroVoiceActivityDetector(options.SpeechProbabilityThreshold);

    /// <summary>
    /// Runs one authoritative automatic finalisation request off the Godot thread.
    /// </summary>
    protected virtual Task<string> FinaliseAutomaticUtteranceAsync(RecordedAudioData recording, CancellationToken cancellationToken)
        => Task.FromException<string>(new InvalidOperationException("This transcriber does not provide automatic finalisation."));

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

        UpdateAutomaticMonitoring();
        UpdateProcessing();
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
        AbandonAutomaticUtterance(silent: true);
        CancelAutomaticFinalisations();
        TeardownAutomaticPipeline();
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

        if (IsRecording && _manualSessionActive)
        {
            DrainCapture(CaptureBatchFrames);
        }

        else if (IsFinalising)
        {
            ProcessRecordingFinalisation();
        }

        else if (ShouldMonitorAutomatically())
        {
            DrainAutomaticCapture(CaptureBatchFrames);
        }

        UpdateAutomaticMonitoring();

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

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what != NotificationPaused)
        {
            return;
        }

        if (IsRecording)
        {
            // The microphone must not keep recording into a paused world; the normal stop path parks the
            // capture in its finalising state, and the drained transcription dispatch resumes after the
            // tree unpauses.
            ResolveLogger()?.LogInformation(
                "Scene tree paused while recording; stopping and finalising the active recording.");
            StopRecording();
        }
    }

    private void OnControllerButtonPressed(string actionName)
    {
        // XR controller relays keep emitting while the tree is paused, so the record handler must
        // ignore presses it cannot act on.
        if (!CanProcess())
        {
            return;
        }

        if (!Enabled)
        {
            return;
        }

        if (InputMode != VoiceInputMode.AutomaticOnly && string.Equals(actionName, RecordButton.ToString(), StringComparison.Ordinal))
        {
            StartRecording();
        }
    }

    private void OnControllerButtonReleased(string actionName)
    {
        if (!CanProcess())
        {
            return;
        }

        if (InputMode != VoiceInputMode.AutomaticOnly && string.Equals(actionName, RecordButton.ToString(), StringComparison.Ordinal))
        {
            StopRecording();
        }
    }

    /// <summary>
    /// Begins microphone capture when the transcriber is idle.
    /// </summary>
    public void StartRecording()
    {
        if (!Enabled
            || InputMode == VoiceInputMode.AutomaticOnly
            || (IsRecording && _manualSessionActive)
            || IsFinalising
            || (_manualSessionActive && IsTranscribing))
        {
            return;
        }

        AbandonAutomaticUtterance(silent: true);
        CancelAutomaticFinalisations();

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
        _manualSessionActive = true;
        SetLifecycleState(TranscriberLifecycleState.Recording, true);
        UpdateProcessing();
        _recordingStopwatch = PipelineDebugLog.StartTimer();
        PipelineDebugLog.Stage("STT recording started");
        _ = EmitSignal(SignalName.RecordingStarted);
        OnRecordingStarted();
    }

    /// <summary>
    /// Stops microphone capture and dispatches transcription when a recording is active.
    /// </summary>
    public void StopRecording() => RequestRecordingStop();

    private void StopRecordingInternal()
    {
        bool wasRecording = IsRecording && _manualSessionActive;
        Stopwatch? recordingStopwatch = _recordingStopwatch;
        _maxDurationTimer?.Stop();

        StopMicrophonePlayer(wasRecording);
        CompleteRecordingLifecycle(wasRecording, recordingStopwatch);
        if (wasRecording)
        {
            // The silent stop paths (node teardown, aborted finalisation) never dispatch a public completion or
            // failure for the manual session, so the explicit abandonment signal lets listeners settle exactly once.
            _ = EmitSignal(SignalName.RecordingAbandoned);
        }

        SetLifecycleState(TranscriberLifecycleState.Finalising, false);
        _finalisationProcessFrames = 0;
        _audioAccumulator = null;
        _audioCapture?.Clear();
        _manualSessionActive = false;
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
                PipelineDebugLog.LogOnlyLatency("STT recording stopped after", recordingStopwatch);
            }

            OnRecordingStopped();
        }
    }

    private void RequestRecordingStop()
    {
        if (!IsRecording || !_manualSessionActive)
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
            _audioAccumulator = null;
            audioCapture.Clear();
            // Clear the manual flag and recompute processing before failure settlement, mirroring the ordinary
            // success/failure dispatch paths, so automatic monitoring can resume immediately.
            _manualSessionActive = false;
            UpdateProcessing();
            HandleTranscriptionFailure(new InvalidOperationException("Microphone recording contained no audio frames."));
            UpdateAutomaticMonitoring();
            return;
        }

        int sampleRate = Math.Max(1, (int)MathF.Round(AudioServer.GetMixRate()));
        RecordedAudioData recording = audioAccumulator.Complete(sampleRate);
        _audioAccumulator = null;
        audioCapture.Clear();
        if (PipelineDebugLog.IsEnabled)
        {
            PipelineDebugLog.Stage("STT managed recording completed", $"{recording.PCMData.Length} bytes");
        }

        await InvokeTranscriptionAsync(recording);
    }

    private async Task InvokeTranscriptionAsync(RecordedAudioData recording)
    {
        bool lifetimeActive = TryGetActiveLifetimeGeneration(out long lifetimeGeneration);
        if (!lifetimeActive || !Enabled)
        {
            // Clear the manual flag and recompute processing before bailing out so idle _Process is not left spinning.
            bool ownsManualAbandonment = lifetimeActive && _manualSessionActive;
            _manualSessionActive = false;
            UpdateProcessing();
            if (ownsManualAbandonment)
            {
                // A manual capture dropped before worker dispatch — here because the node was disabled while its
                // finalisation was still pending — ends its session through the terminal abandonment signal exactly
                // once (SPCH-003 TR-28), so downstream holders of the synthetic manual token settle textlessly.
                // The node-lifetime-inactive branch never emits: teardown owns that abandonment through
                // StopRecordingInternal, and no lifecycle work may follow it.
                _ = EmitSignal(SignalName.RecordingAbandoned);
            }

            UpdateAutomaticMonitoring();
            return;
        }

        Stopwatch stopwatch = PipelineDebugLog.StartTimer();

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
                if (PipelineDebugLog.IsEnabled)
                {
                    PipelineDebugLog.LogOnlyLatency("STT completed in", stopwatch, $"{text.Length} chars");
                }

                SetLifecycleState(TranscriberLifecycleState.Transcribing, false);
                // The manual flag must clear before processing is recomputed so automatic monitoring can resume;
                // otherwise every lifecycle flag is false and node processing stops for the rest of the session.
                _manualSessionActive = false;
                UpdateProcessing();
                HandleTranscriptionSuccess(text);
                UpdateAutomaticMonitoring();
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
                PipelineDebugLog.LogOnlyLatency("STT failed after", stopwatch);
                SetLifecycleState(TranscriberLifecycleState.Transcribing, false);
                // Clear the manual flag before recomputing processing so automatic monitoring resumes after failure.
                _manualSessionActive = false;
                UpdateProcessing();
                HandleTranscriptionFailure(ex);
                UpdateAutomaticMonitoring();
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

    private void DrainAutomaticCapture(int maximumFrames)
    {
        IAudioFrameCapture? capture = _audioCapture;
        if (capture is null || maximumFrames <= 0 || !EnsureAutomaticPipeline())
        {
            return;
        }

        int framesToRead = (int)Math.Min(maximumFrames, capture.FramesAvailable);
        if (framesToRead <= 0)
        {
            return;
        }

        Vector2[] frames = capture.ReadFrames(framesToRead);
        float[] mono = _automaticMonoScratch ?? throw new InvalidOperationException("Automatic scratch audio was not initialised.");
        int count = Math.Min(frames.Length, mono.Length);
        for (int index = 0; index < count; index++)
        {
            float sample = (frames[index].X + frames[index].Y) * 0.5f;
            mono[index] = float.IsNaN(sample) ? 0f : Math.Clamp(sample, -1f, 1f);
        }

        float[] resampled = _automaticResampledScratch ?? throw new InvalidOperationException("Automatic resampled scratch audio was not initialised.");
        StreamingMonoResampler resampler = _automaticResampler ?? throw new InvalidOperationException("Automatic resampler was not initialised.");

        // The resampler runs continuously across utterances: it is never flushed or reset at utterance boundaries,
        // so the 16 kHz sample clock — and every deadline measured on it — advances without gaps.
        int resampledCount = resampler.Process(mono.AsSpan(0, count), resampled);
        ProcessAutomaticSamples(resampled.AsSpan(0, resampledCount));
    }

    private bool EnsureAutomaticPipeline()
    {
        if (_automaticInputUnavailable)
        {
            return false;
        }

        int sourceSampleRate = Math.Max(1, (int)MathF.Round(AudioServer.GetMixRate()));
        if (_voiceActivityDetector is not null)
        {
            if (_automaticSourceSampleRate == sourceSampleRate)
            {
                return true;
            }

            TeardownAutomaticPipeline();
        }

        try
        {
            AutomaticVoiceInputOptions options = CreateAutomaticOptions();

            IVoiceActivityDetector? detector = VoiceActivityDetectorForTesting ?? CreateVoiceActivityDetector(sourceSampleRate, options);
            if (detector is null)
            {
                return false;
            }

            _automaticSourceSampleRate = sourceSampleRate;
            _voiceActivityDetector = detector;
            _automaticUtteranceCoordinator = new AutomaticUtteranceCoordinator(StreamingMonoResampler.TargetSampleRate, options);
            _automaticResampler = new StreamingMonoResampler(sourceSampleRate);
            _automaticPreRollBuffer = new MonoFloatPreRollBuffer(GetAutomaticPreRollCapacity(options));
            _automaticMonoScratch = new float[CaptureBatchFrames];
            _automaticResampledScratch = new float[
                Math.Max(CaptureBatchFrames, checked((int)Math.Ceiling((double)CaptureBatchFrames * StreamingMonoResampler.TargetSampleRate / sourceSampleRate))) + 64];
            _automaticFrameBuffer = new float[SileroVoiceActivityDetector.FrameSampleCount];
            _automaticPreRollDrainScratch = new float[CaptureBatchFrames];
            _automaticFrameBufferFill = 0;
            _automaticCaptureSampleIndex = 0;
            return true;
        }
        catch (Exception exception)
        {
            // Invalid tuning, a missing model, an incompatible ONNX graph, or session failures all latch automatic
            // input unavailable; partially created detector and session state is disposed before the single warning.
            TeardownAutomaticPipeline();
            return HandleAutomaticInputUnavailable(exception);
        }
    }

    private void TeardownAutomaticPipeline()
    {
        if (_voiceActivityDetector is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _voiceActivityDetector = null;
        _automaticUtteranceCoordinator = null;
        _automaticResampler = null;
        _automaticPreRollBuffer = null;
        _automaticMonoScratch = null;
        _automaticResampledScratch = null;
        _automaticFrameBuffer = null;
        _automaticPreRollDrainScratch = null;
        _automaticFrameBufferFill = 0;
        _automaticCaptureSampleIndex = 0;
        _automaticSourceSampleRate = 0;
    }
    private bool HandleAutomaticInputUnavailable(Exception exception)
    {
        // A persistently failing detector throws for every complete frame inside one drain; the latch below already
        // fired the single warning and failure signal for that drain, so re-entry must stay idempotent.
        if (_automaticInputUnavailable)
        {
            return false;
        }

        // The latch runs first so no later frame retries initialisation or repeats the warning.
        _automaticInputUnavailable = true;
        // An open automatic utterance already opened the public speaking window through RecordingStarted, so it
        // must receive exactly one terminal failure in every input mode.
        bool hasOpenAutomaticUtterance = _automaticGroups.Count > 0;
        AbandonAutomaticUtterance(silent: true);

        bool manualInputAvailable = InputMode != VoiceInputMode.AutomaticOnly;
        var entry = new AutomaticVoiceInputUnavailableEntry(
            InputMode,
            SileroVoiceActivityDetector.ModelPath,
            exception,
            manualInputAvailable);

        ResolveLogger()?.Log(
            LogLevel.Warning,
            default,
            entry,
            exception,
            static (state, _) => state.ToDetailedLogText());

        if (AutomaticVoiceInputUnavailableEntry.RequiresFailureSignal(manualInputAvailable, hasOpenAutomaticUtterance))
        {
            // AutomaticOnly has no alternative input path, and an abandoned utterance already opened the player's
            // speaking window: surface the failure once instead of silently idling or jamming the window open.
            _ = EmitSignal(SignalName.TranscriptionFailed, entry.ToNotificationText());
        }

        UpdateProcessing();
        return false;
    }

    private void ProcessAutomaticSamples(ReadOnlySpan<float> samples)
    {
        float[] frameBuffer = _automaticFrameBuffer ?? throw new InvalidOperationException("Automatic frame buffer was not initialised.");
        int frameLength = SileroVoiceActivityDetector.FrameSampleCount;

        while (!samples.IsEmpty)
        {
            int samplesToCopy = Math.Min(samples.Length, frameLength - _automaticFrameBufferFill);
            samples[..samplesToCopy].CopyTo(frameBuffer.AsSpan(_automaticFrameBufferFill));
            _automaticFrameBufferFill += samplesToCopy;
            samples = samples[samplesToCopy..];
            if (_automaticFrameBufferFill < frameLength)
            {
                return;
            }

            ProcessAutomaticFrame(frameBuffer.AsSpan(0, frameLength));
            _automaticFrameBufferFill = 0;
        }
    }

    private void ProcessAutomaticFrame(ReadOnlySpan<float> frame)
    {
        IVoiceActivityDetector? detector = _voiceActivityDetector;
        AutomaticUtteranceCoordinator? coordinator = _automaticUtteranceCoordinator;
        MonoFloatPreRollBuffer? preRoll = _automaticPreRollBuffer;
        if (detector is null || coordinator is null || preRoll is null)
        {
            return;
        }

        long frameStart = _automaticCaptureSampleIndex;
        _automaticCaptureSampleIndex = checked(_automaticCaptureSampleIndex + frame.Length);

        VoiceActivityDetection detection;
        try
        {
            detection = detector.Process(frame);
        }
        catch (Exception exception)
        {
            // A session or inference failure after successful initialisation latches through the same single
            // structured warning instead of throwing inside the frame loop.
            _ = HandleAutomaticInputUnavailable(exception);
            return;
        }

        AutomaticUtteranceTransitions transitions = coordinator.ProcessFrame(frameStart, frame.Length, detection);
        long cursor = frameStart;
        List<AutomaticPendingFinalisation>? pendingFinalisations = null;
        foreach (AutomaticUtteranceTransition transition in transitions)
        {
            switch (transition.Action)
            {
                case AutomaticUtteranceAction.Started:
                    StartAutomaticSegment(transition);
                    AppendAutomaticAudio(frame, cursor, frameStart, frameStart + frame.Length);
                    cursor = frameStart + frame.Length;
                    break;
                case AutomaticUtteranceAction.Endpointed:
                case AutomaticUtteranceAction.ForceClosed:
                    long deadline = transition.SegmentEndSample ?? cursor;
                    AppendAutomaticAudio(frame, cursor, frameStart, deadline);
                    cursor = Math.Clamp(deadline, frameStart, frameStart + frame.Length);
                    pendingFinalisations ??= [];
                    CloseAutomaticSegment(pendingFinalisations);
                    break;
                case AutomaticUtteranceAction.Continued:
                    StartAutomaticSegment(transition);
                    // Resume is intentionally synchronous and precedes any worker dispatch caused by this frame.
                    _ = EmitSignal(SignalName.AutomaticSpeechResumed, transition.SpeechGroupID!.Value.ToString(), transition.SegmentIndex!.Value, transition.Continued);
                    AppendAutomaticAudio(frame, cursor, frameStart, frameStart + frame.Length);
                    cursor = frameStart + frame.Length;
                    break;
                case AutomaticUtteranceAction.Closed:
                    MarkAutomaticGroupClosed(transition.SpeechGroupID!.Value);
                    break;
                case AutomaticUtteranceAction.None:
                    break;
                case AutomaticUtteranceAction.Rearmed:
                    break;
                default:
                    break;
            }
        }

        if (_activeAutomaticUtterance is not null && cursor < frameStart + frame.Length)
        {
            AppendAutomaticAudio(frame, cursor, frameStart, frameStart + frame.Length);
        }
        else if (_activeAutomaticUtterance is null
            && coordinator.State is AutomaticUtteranceState.Monitoring or AutomaticUtteranceState.Candidate)
        {
            preRoll.Append(frame);
        }

        if (pendingFinalisations is not null)
        {
            foreach (AutomaticPendingFinalisation pending in pendingFinalisations)
            {
                StartAutomaticFinalisation(pending.Segment, pending.Recording);
            }
        }
    }

    private void StartAutomaticSegment(AutomaticUtteranceTransition transition)
    {
        AutomaticVoiceInputOptions options = CreateAutomaticOptions();
        MonoFloatPreRollBuffer preRoll = _automaticPreRollBuffer ?? throw new InvalidOperationException("Automatic pre-roll was not initialised.");
        int retentionFrames = checked((int)Math.Ceiling((options.MaximumUtteranceDuration + options.PreRoll + TimeSpan.FromSeconds(1)).TotalSeconds * StreamingMonoResampler.TargetSampleRate));
        Guid groupID = transition.SpeechGroupID ?? throw new InvalidOperationException("Automatic segment did not include a speech group ID.");
        int segmentIndex = transition.SegmentIndex ?? throw new InvalidOperationException("Automatic segment did not include an index.");
        _activeAutomaticUtterance = new AutomaticUtterance(Guid.NewGuid(), groupID, segmentIndex, new PCMAudioAccumulator(Math.Max(1, retentionFrames)));
        if (!_automaticGroups.ContainsKey(groupID))
        {
            _automaticGroups.Add(groupID, new AutomaticSpeechGroup(groupID));
            SetLifecycleState(TranscriberLifecycleState.Recording, true);
            _ = EmitSignal(SignalName.AutomaticGroupOpened, groupID.ToString());
            _ = EmitSignal(SignalName.RecordingStarted);

            while (preRoll.Count > 0)
            {
                float[] scratch = _automaticPreRollDrainScratch ?? throw new InvalidOperationException("Automatic pre-roll scratch audio was not initialised.");
                int drained = preRoll.DrainTo(scratch);
                AppendAutomaticAudio(scratch, 0, 0, drained);
            }
        }
    }

    private void AppendAutomaticAudio(ReadOnlySpan<float> mono, long fromSample, long frameStart, long toSample)
    {
        AutomaticUtterance? segment = _activeAutomaticUtterance;
        if (segment is null)
        {
            return;
        }

        int start = (int)Math.Clamp(fromSample - frameStart, 0, mono.Length);
        int end = (int)Math.Clamp(toSample - frameStart, start, mono.Length);
        foreach (float sample in mono[start..end])
        {
            if (!segment.Accumulator.AppendMonoFrame(sample))
            {
                FailAutomaticUtterance(new InvalidOperationException("Automatic utterance retention exceeded its bounded capacity."));
                return;
            }
        }
    }

    private void CloseAutomaticSegment(List<AutomaticPendingFinalisation> pendingFinalisations)
    {
        AutomaticUtterance? utterance = _activeAutomaticUtterance;
        if (utterance is null)
        {
            return;
        }

        _activeAutomaticUtterance = null;
        RecordedAudioData recording = utterance.Accumulator.Complete(StreamingMonoResampler.TargetSampleRate);
        pendingFinalisations.Add(new AutomaticPendingFinalisation(utterance, recording));
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

    private void StartAutomaticFinalisation(AutomaticUtterance utterance, RecordedAudioData recording)
    {
        if (!TryGetActiveLifetimeGeneration(out long lifetimeGeneration))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _automaticFinalisationCancellations.Add(utterance.ID, cancellation);
        _automaticGroups[utterance.SpeechGroupID].SettlementGate.RegisterDispatch();
        _pendingAutomaticFinalisations++;
        SetLifecycleState(TranscriberLifecycleState.Transcribing, true);
        UpdateProcessing();
        _ = FinaliseAutomaticUtteranceOnWorkerAsync(utterance, recording, lifetimeGeneration, cancellation);
    }

    private async Task FinaliseAutomaticUtteranceOnWorkerAsync(
        AutomaticUtterance utterance,
        RecordedAudioData recording,
        long lifetimeGeneration,
        CancellationTokenSource cancellation)
    {
        try
        {
            string text = await Task.Run(
                () => FinaliseAutomaticUtteranceAsync(recording, cancellation.Token),
                cancellation.Token).ConfigureAwait(false);
            await DispatchGodotActionAsync(() => CompleteAutomaticFinalisation(utterance, cancellation, text, null), lifetimeGeneration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || !IsLifetimeActive(lifetimeGeneration))
        {
            // Manual precedence and teardown abandon automatic utterances without a public result.
        }
        catch (Exception exception)
        {
            if (IsLifetimeActive(lifetimeGeneration))
            {
                await DispatchGodotActionAsync(() => CompleteAutomaticFinalisation(utterance, cancellation, null, exception), lifetimeGeneration).ConfigureAwait(false);
            }
        }
    }

    private void CompleteAutomaticFinalisation(AutomaticUtterance utterance, CancellationTokenSource cancellation, string? text, Exception? exception)
    {
        if (!_automaticFinalisationCancellations.Remove(utterance.ID, out CancellationTokenSource? registered)
            || !ReferenceEquals(registered, cancellation)
            || !_automaticGroups.TryGetValue(utterance.SpeechGroupID, out AutomaticSpeechGroup? group)
            || group.SettlementGate.Abandoned)
        {
            cancellation.Dispose();
            return;
        }

        registered.Dispose();
        _pendingAutomaticFinalisations = Math.Max(0, _pendingAutomaticFinalisations - 1);
        if (_pendingAutomaticFinalisations == 0 && !_manualSessionActive)
        {
            SetLifecycleState(TranscriberLifecycleState.Transcribing, false);
            UpdateProcessing();
        }

        DrainAutomaticGroup(group, group.SettlementGate.Settle(utterance.SegmentIndex, new AutomaticSegmentOutcome(text, exception)));
        UpdateAutomaticMonitoring();
    }

    private void FailAutomaticUtterance(Exception exception)
    {
        if (_activeAutomaticUtterance is null)
        {
            return;
        }

        _activeAutomaticUtterance = null;
        _automaticPreRollBuffer?.Clear();
        _voiceActivityDetector?.Reset();
        _automaticUtteranceCoordinator?.Reset();
        AbandonAutomaticGroups();
        HandleTranscriptionFailure(exception);
    }

    private void AbandonAutomaticUtterance(bool silent)
    {
        _activeAutomaticUtterance = null;
        SetLifecycleState(TranscriberLifecycleState.Recording, false);
        _automaticPreRollBuffer?.Clear();
        _voiceActivityDetector?.Reset();
        _automaticUtteranceCoordinator?.Reset();
        AbandonAutomaticGroups();
        if (!silent)
        {
            HandleTranscriptionFailure(new OperationCanceledException("Automatic utterance was abandoned."));
        }
    }

    private void CancelAutomaticFinalisations()
    {
        foreach ((Guid _, CancellationTokenSource cancellation) in _automaticFinalisationCancellations)
        {
            cancellation.Cancel();
        }

        _automaticFinalisationCancellations.Clear();
        _pendingAutomaticFinalisations = 0;
        if (!_manualSessionActive)
        {
            SetLifecycleState(TranscriberLifecycleState.Transcribing, false);
        }
    }

    private void MarkAutomaticGroupClosed(Guid groupID)
    {
        if (!_automaticGroups.TryGetValue(groupID, out AutomaticSpeechGroup? group) || group.SettlementGate.Abandoned)
        {
            return;
        }

        group.SettlementGate.Close();
        if (_automaticGroups.Values.All(candidate => candidate.SettlementGate.Closed))
        {
            SetLifecycleState(TranscriberLifecycleState.Recording, false);
        }

        // A fast REST result can settle before the continuation gap closes. There are no new outcomes to drain in
        // that case, but the already-published group still needs its single public terminal and dictionary removal.
        // A same-frame endpoint/force-close queues its segment dispatch after the ordered transitions finish. Until at
        // least one segment is registered, that group's empty gate is not yet a settled public group.
        if (group.SettlementGate.DispatchedSegments > 0 && group.SettlementGate.IsFullySettled)
        {
            CompleteAutomaticGroupClosure(group, outcomesWerePublished: true);
            return;
        }

        DrainAutomaticGroup(group, []);
    }

    private void DrainAutomaticGroup(AutomaticSpeechGroup group, IReadOnlyList<AutomaticSegmentSettlement> settled)
    {
        foreach (AutomaticSegmentSettlement settlement in settled)
        {
            int segmentIndex = settlement.SegmentIndex;
            AutomaticSegmentOutcome outcome = settlement.Outcome;
            string groupID = group.ID.ToString();
            bool finalOutcome = group.SettlementGate.IsFullySettled
                && segmentIndex == group.SettlementGate.DispatchedSegments - 1;
            if (finalOutcome)
            {
                // Preserve the closure-before-final-publication ordering so PlayerVoice can close its speaking window
                // before a final nonblank segment reaches hearing listeners.
                CompleteAutomaticGroupClosure(group, outcomesWerePublished: false);
            }

            if (outcome.Exception is null)
            {
                _ = EmitSignal(SignalName.AutomaticSegmentCompleted, outcome.Text ?? string.Empty, groupID, segmentIndex, segmentIndex > 0);
            }
            else
            {
                ResolveLogger()?.LogError(outcome.Exception, "Automatic transcription segment {SegmentIndex} in group {SpeechGroupID} failed.", segmentIndex, groupID);
                _ = EmitSignal(SignalName.AutomaticSegmentFailed, outcome.Exception.Message, groupID, segmentIndex, segmentIndex > 0);
            }
        }
    }

    private void CompleteAutomaticGroupClosure(AutomaticSpeechGroup group, bool outcomesWerePublished)
    {
        if (_automaticGroups.Remove(group.ID))
        {
            _ = EmitSignal(SignalName.AutomaticGroupClosed, group.ID.ToString(), outcomesWerePublished);
        }
    }

    private void AbandonAutomaticGroups()
    {
        foreach (AutomaticSpeechGroup group in _automaticGroups.Values)
        {
            group.SettlementGate.Abandon();
            _ = EmitSignal(SignalName.AutomaticGroupAbandoned, group.ID.ToString());
        }

        _automaticGroups.Clear();
        CancelAutomaticFinalisations();
    }

    private AutomaticVoiceInputOptions CreateAutomaticOptions()
        => new(
            Math.Clamp(AutomaticSpeechProbabilityThreshold, 0f, 1f),
            TimeSpan.FromMilliseconds(Math.Max(0, AutomaticPreRollMilliseconds)),
            TimeSpan.FromMilliseconds(Math.Max(0, AutomaticMinimumVoicedMilliseconds)),
            TimeSpan.FromMilliseconds(Math.Max(1, AutomaticEndpointSilenceMilliseconds)),
            TimeSpan.FromMilliseconds(Math.Max(1, AutomaticContinuationGapMilliseconds)),
            TimeSpan.FromSeconds(Math.Clamp(AutomaticMaximumDuration, 1f, 120f)),
            null);

    private static int GetAutomaticPreRollCapacity(AutomaticVoiceInputOptions options)
    {
        // Qualification occurs after whole 512-sample VAD frames. Retain configured pre-roll plus the entire candidate
        // interval, with one additional frame for the threshold-crossing allowance, so onset audio drains once when
        // StartAutomaticSegment runs even when configured pre-roll is zero.
        int qualificationRetention = checked((int)Math.Ceiling(
            (options.PreRoll + options.MinimumVoicedDuration).TotalSeconds * StreamingMonoResampler.TargetSampleRate));
        return Math.Max(1, checked(qualificationRetention + SileroVoiceActivityDetector.FrameSampleCount));
    }

    private bool ShouldMonitorAutomatically()
        => Enabled
            && !_automaticInputUnavailable
            && InputMode is VoiceInputMode.AutomaticOnly or VoiceInputMode.ButtonAndAutomatic
            && !_manualSessionActive
            && !IsFinalising;

    private void UpdateAutomaticMonitoring()
    {
        AudioStreamPlayer? microphonePlayer = _microphonePlayer;
        if (microphonePlayer is null)
        {
            return;
        }

        if (ShouldMonitorAutomatically() && EnsureAutomaticPipeline())
        {
            if (!microphonePlayer.Playing)
            {
                _audioCapture?.Clear();
                microphonePlayer.Play();
            }
        }
        else if (!_manualSessionActive && microphonePlayer.Playing)
        {
            microphonePlayer.Stop();
            _audioCapture?.Clear();
        }
    }

    private void UpdateProcessing()
        => SetProcess((!_isBound && _xrInitialised) || IsRecording || IsFinalising || IsTranscribing || ShouldMonitorAutomatically());

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
        PipelineDebugLog.LogOnlyLatency(diagnosticName, stopwatch);
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
        if (TranscriptNotificationEnabled && !string.IsNullOrWhiteSpace(text))
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

    private sealed record AutomaticUtterance(Guid ID, Guid SpeechGroupID, int SegmentIndex, PCMAudioAccumulator Accumulator);

    private sealed record AutomaticPendingFinalisation(AutomaticUtterance Segment, RecordedAudioData Recording);

    private sealed class AutomaticSpeechGroup(Guid id)
    {
        public Guid ID { get; } = id;

        public AutomaticSegmentSettlementGate SettlementGate { get; } = new();
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
