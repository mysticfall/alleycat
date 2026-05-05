using AlleyCat.UI;
using AlleyCat.XR;
using Godot;

namespace AlleyCat.Speech;

/// <summary>
/// Base XR speech-transcription component that records microphone input and dispatches transcription requests.
/// </summary>
public abstract partial class Transcriber : Node
{
    private const string DefaultRecordingBusName = "SpeechRecord";
    private const string DefaultFriendlyErrorMessage = "Voice transcription failed. Please try again.";

    private XRManager? _xrManager;
    private IXRHandController? _recordController;
    private AudioEffectRecord? _recordEffect;
    private AudioStreamPlayer? _microphonePlayer;
    private Godot.Timer? _maxDurationTimer;
    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private bool _xrInitialised;
    private bool _isBound;
    private bool _deferredGodotActionFlushQueued;

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
    /// Selects which XR hand-controller triggers microphone recording.
    /// </summary>
    public enum RecordingHand
    {
        /// <summary>
        /// Left-hand XR controller.
        /// </summary>
        Left,

        /// <summary>
        /// Right-hand XR controller.
        /// </summary>
        Right
    }

    /// <summary>
    /// XR controller hand used for microphone recording.
    /// </summary>
    [Export]
    public RecordingHand RecordHand
    {
        get;
        set;
    } = RecordingHand.Left;

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
    /// Indicates whether microphone capture is active.
    /// </summary>
    public bool IsRecording
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
    /// Converts recorded audio into transcribed text.
    /// </summary>
    /// <param name="audioStream">Recorded WAV audio.</param>
    /// <returns>Transcribed text.</returns>
    public abstract Task<string> Transcribe(AudioStreamWav audioStream);

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
        => DispatchGodotActionAsync(action);

    /// <inheritdoc />
    public override void _Ready()
    {
        _xrManager = ResolveXRManager();
        _recordEffect = EnsureRecordEffect();
        _microphonePlayer = CreateMicrophonePlayer();
        _maxDurationTimer = CreateMaxDurationTimer();

        if (_xrManager is null)
        {
            GD.PushWarning($"{nameof(Transcriber)} could not find an {nameof(XRManager)} in the current scene tree.");
            SetProcess(false);
            return;
        }

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
        if (_xrManager is not null)
        {
            _xrManager.Initialised -= OnXRInitialised;
        }

        StopRecordingInternal();
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
    }

    private XRManager? ResolveXRManager()
    {
        foreach (Node node in GetTree().Root.FindChildren(pattern: "*", type: string.Empty, recursive: true, owned: false))
        {
            if (node is XRManager manager)
            {
                return manager;
            }
        }

        return null;
    }

    private AudioEffectRecord EnsureRecordEffect()
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
            if (AudioServer.GetBusEffect(busIndex, effectIndex) is AudioEffectRecord existingEffect)
            {
                existingEffect.SetRecordingActive(false);
                return existingEffect;
            }
        }

        AudioEffectRecord recordEffect = new();
        AudioServer.AddBusEffect(busIndex, recordEffect);
        recordEffect.SetRecordingActive(false);
        return recordEffect;
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

        _recordController = RecordHand == RecordingHand.Left
            ? xrManager.Runtime.LeftHandController
            : xrManager.Runtime.RightHandController;

        _recordController.ActionButtonPressed += OnControllerButtonPressed;
        _recordController.ActionButtonReleased += OnControllerButtonReleased;
        _isBound = true;
        SetProcess(false);
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
        SetProcess(_xrInitialised);
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
        if (!Enabled)
        {
            return;
        }

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
        if (!Enabled || IsRecording || IsTranscribing)
        {
            return;
        }

        AudioEffectRecord? recordEffect = _recordEffect;
        AudioStreamPlayer? microphonePlayer = _microphonePlayer;
        Godot.Timer? maxDurationTimer = _maxDurationTimer;
        if (recordEffect is null || microphonePlayer is null || maxDurationTimer is null)
        {
            return;
        }

        maxDurationTimer.Stop();
        maxDurationTimer.WaitTime = Mathf.Max(MaxRecordingDuration, 0.1f);
        recordEffect.SetRecordingActive(true);
        microphonePlayer.Play();
        maxDurationTimer.Start();
        IsRecording = true;
        OnRecordingStarted();
    }

    /// <summary>
    /// Stops microphone capture and dispatches transcription when a recording is active.
    /// </summary>
    public void StopRecording() => _ = StopRecordingAndTranscribeAsync();

    private void StopRecordingInternal()
    {
        bool wasRecording = IsRecording;
        _maxDurationTimer?.Stop();
        _microphonePlayer?.Stop();
        _recordEffect?.SetRecordingActive(false);
        IsRecording = false;

        if (wasRecording)
        {
            OnRecordingStopped();
        }
    }

    private async Task StopRecordingAndTranscribeAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        AudioEffectRecord? recordEffect = _recordEffect;
        if (recordEffect is null)
        {
            StopRecordingInternal();
            return;
        }

        StopRecordingInternal();
        AudioStreamWav recording = recordEffect.GetRecording();
        await InvokeTranscriptionAsync(recording);
    }

    private async Task InvokeTranscriptionAsync(AudioStreamWav recording)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            IsTranscribing = true;
            string text = await Transcribe(recording);
            await DispatchGodotActionAsync(() => HandleTranscriptionSuccess(text));
        }
        catch (Exception ex)
        {
            await DispatchGodotActionAsync(() => HandleTranscriptionFailure(ex));
        }
        finally
        {
            IsTranscribing = false;
        }
    }

    private void OnMaxDurationTimeout() => _ = StopRecordingAndTranscribeAsync();

    private Task DispatchGodotActionAsync(Action action)
    {
        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_deferredGodotActionsLock)
        {
            _deferredGodotActions.Enqueue(new DeferredGodotAction(action, completionSource));

            if (!_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }

        return completionSource.Task;
    }

    private void FlushDeferredGodotActions()
    {
        DeferredGodotAction[] actions;

        lock (_deferredGodotActionsLock)
        {
            actions = [.. _deferredGodotActions];
            _deferredGodotActions.Clear();
            _deferredGodotActionFlushQueued = false;
        }

        foreach (DeferredGodotAction action in actions)
        {
            try
            {
                action.Action();
                _ = action.CompletionSource.TrySetResult();
            }
            catch (Exception ex)
            {
                _ = action.CompletionSource.TrySetException(ex);
            }
        }

        lock (_deferredGodotActionsLock)
        {
            if (_deferredGodotActions.Count > 0 && !_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }
    }

    private void HandleTranscriptionSuccess(string text)
    {
        _ = EmitSignal(SignalName.TranscriptionCompleted, text);
        _ = this.PostNotification(text);
        OnTranscriptionCompleted(text);
    }

    private void HandleTranscriptionFailure(Exception ex)
    {
        GD.PushError(ex.ToString());
        _ = EmitSignal(SignalName.TranscriptionFailed, ex.Message);
        _ = this.PostNotification(DefaultFriendlyErrorMessage);
    }

    private sealed class DeferredGodotAction(Action action, TaskCompletionSource completionSource)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;
    }
}
