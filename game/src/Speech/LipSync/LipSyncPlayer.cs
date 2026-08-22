using System.Text;
using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.LipSync;

/// <summary>
/// Loads an audio clip through a concrete backend and plays ARKit blendshape values onto character meshes.
/// </summary>
public abstract partial class LipSyncPlayer : Node
{
    /// <summary>
    /// Skeleton whose descendants contain the blendshape-capable meshes to drive.
    /// </summary>
    [Export]
    public Skeleton3D? Skeleton
    {
        get;
        set;
    }

    /// <summary>
    /// Optional audio player for synchronised audio/blendshape playback.
    /// </summary>
    [Export]
    public AudioStreamPlayer3D AudioPlayer
    {
        get;
        set;
    } = null!;

    /// <summary>
    /// Playback speed multiplier for frame advancement.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,3.0,0.01")]
    public float PlaybackSpeed
    {
        get;
        set;
    } = 1f;

    /// <summary>
    /// Whether playback loops when the inferred stream reaches the end.
    /// </summary>
    [Export]
    public bool LoopPlayback
    {
        get;
        set;
    }

    /// <summary>
    /// Amount of streaming inference output (in seconds) that must be buffered before prepared
    /// playback may start audibly. Larger values trade time-to-audible-playback for resilience against
    /// playback outrunning the streaming inference download. Only used by backends that support
    /// streaming inference.
    /// </summary>
    [Export(PropertyHint.Range, "0,5,0.01")]
    public float StreamingStartupBufferSeconds
    {
        get;
        set;
    } = 0.1f;

    /// <summary>
    /// Indicates whether initialisation completed successfully.
    /// </summary>
    public bool IsInitialised
    {
        get;
        private set;
    }

    /// <summary>
    /// Contains the initialisation error message when <see cref="IsInitialised"/> is false.
    /// </summary>
    public string InitialisationError
    {
        get;
        private set;
    } = string.Empty;

    /// <summary>
    /// Number of inferred lip-sync frames known to the active playback source: the full count for batch
    /// playback, or the frames buffered so far for streaming playback.
    /// </summary>
    public int FrameCount => _activeStreamingSession?.Buffer.FrameCount ?? _frames.Length;

    /// <summary>
    /// Number of blendshape channels per frame.
    /// </summary>
    public int BlendshapeChannelCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of frames applied to the mesh bindings during playback.
    /// </summary>
    public int AppliedFrameCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of observed channel-value changes across applied frames.
    /// </summary>
    public int WeightChangeEventCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Largest absolute channel delta observed across applied frames.
    /// </summary>
    public float MaxObservedWeightDelta
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of meshes with at least one mapped blendshape channel.
    /// </summary>
    public int MappedMeshCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Total number of mapped mesh-channel bindings across all meshes.
    /// </summary>
    public int MappedChannelCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Indicates whether the configured audio player is currently playing.
    /// </summary>
    public bool IsAudioPlaying
    {
        get;
        private set;
    }

    /// <summary>
    /// Raised when a playback session ends, observed through the audio-playing polling in <see cref="_Process"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notification is raised exactly once per playback session: the poll detects the falling edge where audio was
    /// playing on the previous poll and has now stopped, or the inferred frames reach their natural end.
    /// </para>
    /// <para>
    /// A session that fails inside the polling loop (for example lost audio synchronisation) can never reach natural
    /// completion, so the failure path raises the notification as well to settle playback watchers.
    /// </para>
    /// <para>
    /// Cutting playback through <see cref="Stop"/> or restarting through <see cref="PlayPrepared"/> halts the
    /// interrupted session without raising this notification for it.
    /// </para>
    /// </remarks>
    public event Action? PlaybackCompleted;

    /// <summary>
    /// Runtime playback error set when audio-synchronised playback fails.
    /// </summary>
    public string PlaybackError
    {
        get;
        private set;
    } = string.Empty;

    private const float DefaultOutputFps = 30f;
    private const float BlendshapeChangeEpsilon = 1e-4f;
    private const long StreamingStarvationWarningIntervalMs = 2000;

    private float[][] _frames = [];
    private readonly List<MeshBinding> _meshBindings = [];
    private float _outputFps = DefaultOutputFps;
    private bool _isPlaying;
    private double _playbackTimeSeconds;
    private int _lastAppliedFrameIndex = -1;
    private float[] _lastAppliedChannelValues = [];
    private bool _audioWasObservedPlaying;
    private bool _audioPlayingAtLastPoll;
    private double _audioStartGraceSeconds;
    private bool _hasLoggedUnmappedBlendshapes;
    private ILogger<LipSyncPlayer>? _logger;
    private readonly Lock _preparationLifetimeLock = new();
    private readonly CancellationTokenSource _preparationLifetimeCancellation = new();
    private int _activePreparationCount;
    private bool _preparationLifetimeEnded;
    private bool _backendDisposalPending;
    private StreamingPlaybackSession? _activeStreamingSession;
    private int _streamingFramesBufferedAtStart;
    private int _streamingMaxStarvedFrameGap;
    private bool _streamingStarving;
    private long _lastStreamingStarvationWarningMs;

    internal bool IsLifetimeEnded
    {
        get
        {
            lock (_preparationLifetimeLock)
            {
                return _preparationLifetimeEnded;
            }
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _logger = GameLoggerResolver.ResolveRequired<LipSyncPlayer>();
        TryInitialise();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        bool disposeBackend;
        lock (_preparationLifetimeLock)
        {
            if (_preparationLifetimeEnded)
            {
                return;
            }

            _preparationLifetimeEnded = true;
            _backendDisposalPending = _activePreparationCount > 0;
            disposeBackend = !_backendDisposalPending;
        }

        _preparationLifetimeCancellation.Cancel();
        StopPlayback(resetWeights: true, clearFrames: true);
        if (disposeBackend)
        {
            DisposeBackend();
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_isPlaying || PlaybackSpeed <= 0f)
        {
            return;
        }

        StreamingFrameBuffer? streamingBuffer = _activeStreamingSession?.Buffer;
        if (streamingBuffer is null && _frames.Length == 0)
        {
            return;
        }

        if (streamingBuffer is not null && streamingBuffer.IsFaulted)
        {
            FailPlaybackAndStop(
                $"LipSyncPlayer: streaming inference failed during playback: {streamingBuffer.Error}");
            return;
        }

        bool audioPlaying = AudioPlayer.IsPlaying();
        bool audioStoppedSinceLastPoll = _audioPlayingAtLastPoll && !audioPlaying;
        _audioPlayingAtLastPoll = audioPlaying;
        IsAudioPlaying = audioPlaying;

        int availableFrames = streamingBuffer?.FrameCount ?? _frames.Length;

        if (!audioPlaying)
        {
            if (audioStoppedSinceLastPoll)
            {
                // Audible playback was observed playing and has now finished, so playback completed naturally.
                CompletePlayback();
                return;
            }

            _audioStartGraceSeconds += delta;

            float durationSeconds = availableFrames / _outputFps;
            bool nearNaturalEnd = _playbackTimeSeconds >= durationSeconds - (1f / _outputFps);
            if (nearNaturalEnd && (streamingBuffer is null || streamingBuffer.IsCompleted))
            {
                CompletePlayback();
                return;
            }

            bool stillWaitingForAudioStart = !_audioWasObservedPlaying && _audioStartGraceSeconds < 0.5d;
            if (stillWaitingForAudioStart)
            {
                return;
            }

            FailPlaybackAndStop("LipSyncPlayer: audio sync lost - AudioPlayer is not playing during active lip-sync playback.");
            return;
        }

        _audioWasObservedPlaying = true;
        _playbackTimeSeconds = AudioPlayer.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix();

        int targetFrameIndex = Mathf.FloorToInt((float)(_playbackTimeSeconds * _outputFps * PlaybackSpeed));
        if (targetFrameIndex >= availableFrames)
        {
            if (streamingBuffer is not null && !streamingBuffer.IsCompleted)
            {
                // The playback cursor outran the streaming download; hold the last applied frame until more
                // frames arrive (starvation hold) while audio continues.
                HandleStreamingStarvation(targetFrameIndex, availableFrames);
                return;
            }

            if (!LoopPlayback)
            {
                // The inferred frames reached their natural end while audio was still progressing.
                CompletePlayback();
                return;
            }

            float durationSeconds = availableFrames / _outputFps;
            if (durationSeconds <= 0f)
            {
                StopPlayback(resetWeights: false, clearFrames: false);
                return;
            }

            _playbackTimeSeconds %= durationSeconds;
            targetFrameIndex = Mathf.FloorToInt((float)(_playbackTimeSeconds * _outputFps * PlaybackSpeed));
            _lastAppliedFrameIndex = -1;
        }

        if (targetFrameIndex == _lastAppliedFrameIndex)
        {
            return;
        }

        _streamingStarving = false;
        ApplyFrame(targetFrameIndex);
        _lastAppliedFrameIndex = targetFrameIndex;
    }

    /// <summary>
    /// Begins manual playback for the supplied speech clip.
    /// </summary>
    /// <remarks>
    /// For streaming backends this blocks until metadata and the startup buffer worth of frames have
    /// arrived (mirroring how the batch path blocks on full inference); the stream download then continues
    /// in the background once playback starts.
    /// </remarks>
    public void Play(AudioStreamWav speech)
    {
        try
        {
            PlayPrepared(SupportsStreamingInference
                ? PrepareStreamingPlaybackBlocking(speech)
                : PreparePlayback(speech, CancellationToken.None));
        }
        catch (Exception ex)
        {
            StopPlayback(resetWeights: true, clearFrames: true);
            SetPlaybackError($"LipSyncPlayer: playback failed: {ex}");
        }
    }

    /// <summary>
    /// Prepares lip-sync inference data for the supplied speech clip off the caller thread.
    /// </summary>
    public Task<PreparedPlayback> PreparePlaybackAsync(
        AudioStreamWav speech,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource preparationCancellation;
        lock (_preparationLifetimeLock)
        {
            if (_preparationLifetimeEnded)
            {
                throw new InvalidOperationException("LipSyncPlayer cannot prepare playback after node teardown.");
            }

            _activePreparationCount++;
            preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _preparationLifetimeCancellation.Token);
        }

        return RunPreparationAsync(speech, preparationCancellation, cancellationToken);
    }

    /// <summary>
    /// Starts playback for lip-sync data prepared by <see cref="PreparePlaybackAsync" />.
    /// </summary>
    public void PlayPrepared(PreparedPlayback playback)
    {
        PlaybackError = string.Empty;

        if (!EnsureInitialised())
        {
            return;
        }

        StopPlayback(resetWeights: true, clearFrames: true);
        StartPlayback(playback);
    }

    /// <summary>
    /// Cuts active playback, halting both audio playback and lip-sync frame application immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe to call when playback is inactive. Cutting does not raise <see cref="PlaybackCompleted"/> for the
    /// interrupted session. For streaming inference sessions, cutting also cancels the in-flight background
    /// stream download (read loop and HTTP request); ordinary non-streaming backend inference requests keep
    /// running to completion.
    /// </para>
    /// </remarks>
    public void Stop() => StopPlayback(resetWeights: true, clearFrames: true);

    /// <summary>
    /// Simulates the natural end of the active playback session for deterministic testing.
    /// </summary>
    internal void CompletePlaybackForTesting() => CompletePlayback();

    /// <summary>
    /// Number of starvation episodes observed by the active streaming playback session: the playback
    /// cursor outran the buffered frames and playback held the last applied frame. Internal diagnostics
    /// seam for tests; only meaningful while a streaming session is active.
    /// </summary>
    internal int StreamingStarvationEpisodeCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of rate-limited starvation warnings emitted by the active streaming playback session.
    /// Internal diagnostics seam for tests; only meaningful while a streaming session is active.
    /// </summary>
    internal int StreamingStarvationWarningCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Indicates a streaming playback session is currently driving playback. Internal diagnostics seam
    /// for tests: <see cref="Stop"/> detaches the session, so this also proves stream cut-off.
    /// </summary>
    internal bool HasActiveStreamingSession => _activeStreamingSession is not null;

    /// <summary>
    /// Sample rate the inference backend requires; the base class normalises inference input to this rate.
    /// </summary>
    protected abstract int BackendSampleRate
    {
        get;
    }

    /// <summary>
    /// Prepares backend resources required before inference runs.
    /// </summary>
    protected abstract void InitialiseBackend();

    /// <summary>
    /// Executes backend inference and returns normalised playback data.
    /// </summary>
    protected abstract LipSyncInferenceResult RunBackendInference(
        AudioStreamWav speech,
        CancellationToken cancellationToken);

    /// <summary>
    /// Indicates whether the backend supports streaming inference, where playback starts once metadata and
    /// a startup buffer worth of frames have arrived while the download continues in the background. Opt-in:
    /// batch-oriented backends keep the full-response behaviour unchanged.
    /// </summary>
    protected virtual bool SupportsStreamingInference => false;

    /// <summary>
    /// Executes backend streaming inference, appending converted frames into the supplied buffer until the
    /// stream completes or fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on a background thread; implementations must not call Godot scene APIs. The base
    /// implementation fails because streaming inference is opt-in through
    /// <see cref="SupportsStreamingInference"/>.
    /// </para>
    /// <para>
    /// The member is internal rather than protected so the <see cref="StreamingFrameBuffer"/> contract can
    /// stay internal; all streaming backends live in this assembly, and friend test assemblies can still
    /// override it through internals visibility.
    /// </para>
    /// </remarks>
    internal virtual Task RunBackendStreamingInferenceAsync(
        AudioStreamWav speech,
        StreamingFrameBuffer frameBuffer,
        CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException(
            "LipSyncPlayer: this backend does not support streaming inference."));

    /// <summary>
    /// Releases backend resources allocated during initialisation or inference.
    /// </summary>
    protected abstract void DisposeBackend();

    /// <summary>
    /// Data returned by a concrete lip-sync inference backend.
    /// </summary>
    protected sealed record LipSyncInferenceResult(float[][] Frames, IReadOnlyList<string> BlendshapeNames, float OutputFps);

    /// <summary>
    /// Speech clip and precomputed lip-sync inference data ready for playback.
    /// </summary>
    /// <remarks>
    /// Batch backends carry the full fixed <see cref="Frames"/> array. Streaming backends carry a
    /// <see cref="StreamingSession"/> whose buffer keeps filling in the background after preparation
    /// completes; <see cref="Frames"/> is empty for those and <see cref="PreparedFrameCount"/> reports the
    /// frames buffered so far.
    /// </remarks>
    public sealed record PreparedPlayback(
        AudioStreamWav Speech,
        float[][] Frames,
        IReadOnlyList<string> BlendshapeNames,
        float OutputFps)
    {
        internal StreamingPlaybackSession? StreamingSession
        {
            get;
            init;
        }

        /// <summary>
        /// Number of inference frames available to this playback: the full count for batch data, or the
        /// frames buffered so far for streaming playback.
        /// </summary>
        public int PreparedFrameCount => StreamingSession?.Buffer.FrameCount ?? Frames.Length;
    }

    /// <summary>
    /// State for one streaming inference session: the shared frame buffer, its cancellation source, and the
    /// background read-loop task that owns the download.
    /// </summary>
    internal sealed class StreamingPlaybackSession(
        StreamingFrameBuffer buffer,
        CancellationTokenSource cancellation,
        Task readLoop)
    {
        private int _cancelled;

        /// <summary>
        /// Frame buffer shared between the background reader thread and main-thread playback.
        /// </summary>
        public StreamingFrameBuffer Buffer => buffer;

        /// <summary>
        /// Cancellation source observed by the read loop; disposed when the loop settles.
        /// </summary>
        public CancellationTokenSource Cancellation => cancellation;

        /// <summary>
        /// Read-loop task; settles when the stream completes, fails, or is cancelled.
        /// </summary>
        public Task ReadLoop => readLoop;

        /// <summary>
        /// Indicates the session was explicitly cut through <see cref="Cancel"/>.
        /// </summary>
        public bool IsCancelled => Volatile.Read(ref _cancelled) == 1;

        /// <summary>
        /// Cuts the session, aborting the in-flight stream download. Safe to call after the read loop has
        /// already settled.
        /// </summary>
        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The read loop already settled and disposed the source; there is nothing to cancel.
            }
        }
    }

    private bool EnsureInitialised()
    {
        if (IsInitialised)
        {
            return true;
        }

        TryInitialise();
        if (IsInitialised)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(InitialisationError))
        {
            PlaybackError = InitialisationError;
        }

        return false;
    }

    private void TryInitialise()
    {
        // Resolve the logger up front on the main thread so background streaming read loops can use it
        // without touching the Godot service provider off-thread.
        _logger ??= GameLoggerResolver.ResolveRequired<LipSyncPlayer>();

        InitialisationError = string.Empty;
        PlaybackError = string.Empty;
        IsInitialised = false;

        try
        {
            Initialise();
            IsInitialised = true;
        }
        catch (Exception ex)
        {
            DisposeBackend();
            IsInitialised = false;
            InitialisationError = ex.ToString();
            GD.PushError($"LipSyncPlayer: initialisation failed: {InitialisationError}");
            SetProcess(false);
        }
    }

    private void Initialise()
    {
        if (AudioPlayer is null)
        {
            throw new InvalidOperationException("LipSyncPlayer: AudioPlayer is not assigned.");
        }

        _ = GetConfiguredSkeleton();

        ResetPlaybackMetrics();
        ResetPlaybackTiming();
        ClearPreparedPlayback();
        InitialiseBackend();
        SetProcess(false);
    }

    private async Task<PreparedPlayback> RunPreparationAsync(
        AudioStreamWav speech,
        CancellationTokenSource preparationCancellation,
        CancellationToken callerCancellation)
    {
        try
        {
            return await (SupportsStreamingInference
                ? PrepareStreamingPlaybackAsync(speech, preparationCancellation.Token, callerCancellation)
                : Task.Run(
                    () => PreparePlayback(speech, preparationCancellation.Token),
                    preparationCancellation.Token));
        }
        finally
        {
            preparationCancellation.Dispose();
            CompletePreparation();
        }
    }

    /// <summary>
    /// Prepares streaming playback: starts the background stream download and returns once metadata and
    /// the startup buffer worth of frames have arrived, leaving the download running behind the returned
    /// session.
    /// </summary>
    private async Task<PreparedPlayback> PrepareStreamingPlaybackAsync(
        AudioStreamWav speech,
        CancellationToken preparationToken,
        CancellationToken callerCancellation)
    {
        preparationToken.ThrowIfCancellationRequested();

        // Run on a worker thread like the batch path so resampling and payload preparation never land on
        // the caller's context. The session is linked to the caller's token directly (not the preparation
        // source, which is disposed once preparation returns) so post-preparation caller cancellation
        // still cuts the background download.
        return await Task.Run(async () =>
        {
            StreamingPlaybackSession? session = null;
            try
            {
                session = StartStreamingInferenceSession(speech, callerCancellation);
                await session.Buffer.StartupBufferReady.WaitAsync(preparationToken);
                return CreateStreamingPreparedPlayback(speech, session);
            }
            catch
            {
                await TeardownStreamingSessionAsync(session);
                throw;
            }
        }, preparationToken);
    }

    /// <summary>
    /// Synchronous variant of <see cref="PrepareStreamingPlaybackAsync"/> used by the manual
    /// <see cref="Play"/> path.
    /// </summary>
    private PreparedPlayback PrepareStreamingPlaybackBlocking(AudioStreamWav speech)
    {
        StreamingPlaybackSession? session = null;
        try
        {
            session = StartStreamingInferenceSession(speech, externalCancellation: null);
            session.Buffer.StartupBufferReady.Wait();
            return CreateStreamingPreparedPlayback(speech, session);
        }
        catch
        {
            session?.Cancel();
            session?.ReadLoop.Wait();
            throw;
        }
    }

    private void ValidatePreparationRequest(AudioStreamWav speech)
    {
        if (speech is null)
        {
            throw new InvalidOperationException("LipSyncPlayer: speech clip is not assigned.");
        }

        if (!IsInitialised)
        {
            throw new InvalidOperationException("LipSyncPlayer: cannot prepare playback before initialisation succeeds.");
        }
    }

    private StreamingPlaybackSession StartStreamingInferenceSession(
        AudioStreamWav speech,
        CancellationToken? externalCancellation)
    {
        ValidatePreparationRequest(speech);

        // Inference consumes a stream normalised to the backend sample rate; playback keeps the original
        // stream so the game hears the generator's original-quality audio.
        AudioStreamWav inferenceSpeech = CreateBackendInferenceStream(speech);
        StreamingFrameBuffer frameBuffer = new(Mathf.Max(0f, StreamingStartupBufferSeconds));

        CancellationTokenSource sessionCancellation;
        lock (_preparationLifetimeLock)
        {
            if (_preparationLifetimeEnded)
            {
                throw new InvalidOperationException("LipSyncPlayer cannot prepare playback after node teardown.");
            }

            // Hold the preparation lifetime (and deferred backend disposal) until the background read loop
            // settles, not just until the startup gate opens.
            _activePreparationCount++;
            sessionCancellation = externalCancellation.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    externalCancellation.Value,
                    _preparationLifetimeCancellation.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(_preparationLifetimeCancellation.Token);
        }

        var readLoop = Task.Run(() => RunStreamingReadLoopAsync(inferenceSpeech, frameBuffer, sessionCancellation));

        return new StreamingPlaybackSession(frameBuffer, sessionCancellation, readLoop);
    }

    private async Task RunStreamingReadLoopAsync(
        AudioStreamWav inferenceSpeech,
        StreamingFrameBuffer frameBuffer,
        CancellationTokenSource sessionCancellation)
    {
        try
        {
            await RunBackendStreamingInferenceAsync(inferenceSpeech, frameBuffer, sessionCancellation.Token);
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            // The session was cut (stop, node teardown, or preparation cancellation); expected outcome.
            GetLogger().LogDebug("LipSyncPlayer streaming inference read loop was cancelled.");
        }
        catch (Exception ex)
        {
            frameBuffer.MarkFailed(ex);
            GetLogger().LogError(ex, "LipSyncPlayer streaming inference read loop failed.");
        }
        finally
        {
            sessionCancellation.Dispose();
            CompletePreparation();
        }
    }

    private static PreparedPlayback CreateStreamingPreparedPlayback(
        AudioStreamWav speech,
        StreamingPlaybackSession session)
    {
        _ = session.Buffer.TryGetMetadata(out float outputFps, out IReadOnlyList<string> blendshapeNames)
            ? true
            : throw new InvalidOperationException("LipSyncPlayer: streaming playback gate opened without metadata.");

        return new PreparedPlayback(speech, [], blendshapeNames, outputFps)
        {
            StreamingSession = session,
        };
    }

    private static async Task TeardownStreamingSessionAsync(StreamingPlaybackSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.Cancel();
        await session.ReadLoop;
    }

    private PreparedPlayback PreparePlayback(
        AudioStreamWav speech,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePreparationRequest(speech);

        // Inference consumes a stream normalised to the backend sample rate; playback keeps the original stream so
        // the game hears the generator's original-quality audio.
        AudioStreamWav inferenceSpeech = CreateBackendInferenceStream(speech);
        LipSyncInferenceResult inferenceResult = RunBackendInference(inferenceSpeech, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInferenceResult(inferenceResult);

        return new PreparedPlayback(
            speech,
            inferenceResult.Frames,
            inferenceResult.BlendshapeNames,
            inferenceResult.OutputFps);
    }

    private AudioStreamWav CreateBackendInferenceStream(AudioStreamWav speech)
    {
        if (speech.Format != AudioStreamWav.FormatEnum.Format16Bits)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: expected AudioStreamWav format {AudioStreamWav.FormatEnum.Format16Bits}, got {speech.Format}.");
        }

        if (speech.Stereo)
        {
            throw new InvalidOperationException("LipSyncPlayer: expected mono audio stream, but stream is stereo.");
        }

        if (speech.MixRate == BackendSampleRate)
        {
            return speech;
        }

        byte[] resampledData = Pcm16Resampler.ResampleMonoPcm16(speech.Data, speech.MixRate, BackendSampleRate);
        return new AudioStreamWav
        {
            Data = resampledData,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = BackendSampleRate,
            Stereo = false,
        };
    }

    private void CompletePreparation()
    {
        bool disposeBackend;
        lock (_preparationLifetimeLock)
        {
            _activePreparationCount--;
            disposeBackend = _preparationLifetimeEnded
                && _backendDisposalPending
                && _activePreparationCount == 0;
            if (disposeBackend)
            {
                _backendDisposalPending = false;
            }
        }

        if (disposeBackend)
        {
            DisposeBackend();
        }
    }

    private static void ValidateInferenceResult(LipSyncInferenceResult inferenceResult)
    {
        if (inferenceResult.Frames.Length == 0)
        {
            throw new InvalidOperationException("LipSyncPlayer: inference produced zero frames.");
        }
    }

    private void StartPlayback(PreparedPlayback playback)
    {
        StreamingPlaybackSession? streamingSession = playback.StreamingSession;
        if (streamingSession is not null)
        {
            // Fail eagerly so a stream that died between the startup gate and hand-off surfaces as a
            // preparation/playback failure rather than a mid-playback session failure.
            _ = streamingSession.Buffer.IsFaulted
                ? throw new InvalidOperationException(
                    $"LipSyncPlayer: streaming inference failed before playback: {streamingSession.Buffer.Error}")
                : true;

            _activeStreamingSession = streamingSession;
            _frames = [];
            _outputFps = playback.OutputFps > 0f ? playback.OutputFps : DefaultOutputFps;
            BlendshapeChannelCount = ResolveFirstFrame(playback).Length;
            _streamingFramesBufferedAtStart = streamingSession.Buffer.FrameCount;
            StreamingStarvationEpisodeCount = 0;
            StreamingStarvationWarningCount = 0;
            _streamingMaxStarvedFrameGap = 0;
            _lastStreamingStarvationWarningMs = 0;
        }
        else
        {
            _frames = playback.Frames;
            _outputFps = playback.OutputFps > 0f ? playback.OutputFps : DefaultOutputFps;
            BlendshapeChannelCount = _frames[0].Length;
        }

        BuildMeshBindings(playback.BlendshapeNames);
        ResetPlaybackMetrics();
        ResetPlaybackTiming();

        if (streamingSession is not null)
        {
            GetLogger().LogInformation(
                "LipSyncPlayer started streaming playback with {BufferedFrameCount} buffered frame(s) at {OutputFps:0.###} fps, mapped {MappedMeshCount} mesh(es).",
                _streamingFramesBufferedAtStart,
                _outputFps,
                _meshBindings.Count);
        }
        else
        {
            GetLogger().LogInformation(
                "LipSyncPlayer loaded {FrameCount} frames at {OutputFps:0.###} fps, mapped {MappedMeshCount} mesh(es).",
                _frames.Length,
                _outputFps,
                _meshBindings.Count);
        }

        AudioPlayer.Stop();
        AudioPlayer.Stream = playback.Speech;
        AudioPlayer.Play();

        _isPlaying = true;
        IsAudioPlaying = true;

        ApplyFrame(0);
        _lastAppliedFrameIndex = 0;
        SetProcess(true);
    }

    private static float[] ResolveFirstFrame(PreparedPlayback playback)
        => playback.StreamingSession is { } session && session.Buffer.TryGetFrame(0, out float[] frame)
            ? frame
            : playback.Frames[0];

    private void ResetPlaybackMetrics()
    {
        AppliedFrameCount = 0;
        WeightChangeEventCount = 0;
        MaxObservedWeightDelta = 0f;
        IsAudioPlaying = false;
        PlaybackError = string.Empty;
    }

    private void ResetPlaybackTiming()
    {
        _isPlaying = false;
        _playbackTimeSeconds = 0d;
        _lastAppliedFrameIndex = -1;
        _lastAppliedChannelValues = [];
        _audioWasObservedPlaying = false;
        _audioPlayingAtLastPoll = false;
        _audioStartGraceSeconds = 0d;
        _streamingStarving = false;
    }

    private void ClearPreparedPlayback()
    {
        _frames = [];
        _meshBindings.Clear();
        BlendshapeChannelCount = 0;
        MappedMeshCount = 0;
        MappedChannelCount = 0;
    }

    private void StopPlayback(bool resetWeights, bool clearFrames)
    {
        // Detach and cut the active streaming session so the background download cannot outlive its
        // playback session; rapid re-play cancels the previous session's read loop before the new one is
        // adopted.
        StreamingPlaybackSession? session = _activeStreamingSession;
        _activeStreamingSession = null;
        session?.Cancel();

        bool hadPreparedBindings = _meshBindings.Count > 0;

        if (AudioPlayer is not null && AudioPlayer.IsPlaying())
        {
            AudioPlayer.Stop();
        }

        if (resetWeights && hadPreparedBindings)
        {
            ResetAppliedBlendshapeWeights();
        }

        ResetPlaybackTiming();
        IsAudioPlaying = false;
        SetProcess(false);

        if (clearFrames)
        {
            ClearPreparedPlayback();
        }
    }

    private void CompletePlayback()
    {
        if (_activeStreamingSession is { } session)
        {
            LogStreamingSessionSummary(session);
        }

        StopPlayback(resetWeights: false, clearFrames: false);
        PlaybackCompleted?.Invoke();
    }

    /// <summary>
    /// Handles the playback cursor outrunning the buffered streaming frames: holds the last applied frame
    /// and emits a rate-limited warning while collecting per-session diagnostics.
    /// </summary>
    private void HandleStreamingStarvation(int targetFrameIndex, int availableFrames)
    {
        if (!_streamingStarving)
        {
            _streamingStarving = true;
            StreamingStarvationEpisodeCount++;
        }

        _streamingMaxStarvedFrameGap = Math.Max(
            _streamingMaxStarvedFrameGap,
            targetFrameIndex - Math.Max(0, availableFrames - 1));

        long nowMs = System.Environment.TickCount64;
        if (nowMs - _lastStreamingStarvationWarningMs >= StreamingStarvationWarningIntervalMs)
        {
            _lastStreamingStarvationWarningMs = nowMs;
            StreamingStarvationWarningCount++;
            GetLogger().LogWarning(
                "LipSyncPlayer streaming playback starved at frame {TargetFrameIndex} with only {BufferedFrameCount} frame(s) buffered; holding the last applied frame.",
                targetFrameIndex,
                availableFrames);
        }
    }

    private void LogStreamingSessionSummary(StreamingPlaybackSession session)
    {
        StreamingFrameBuffer buffer = session.Buffer;
        int bufferedFrameCount = buffer.FrameCount;
        int? declaredFrameCount = buffer.DeclaredFrameCount;

        if (buffer.IsCompleted)
        {
            GetLogger().LogInformation(
                "LipSyncPlayer streaming playback completed: {FramesBufferedAtStart} frame(s) buffered at playback start, {BufferedFrameCount} frame(s) total, {StarvationEpisodeCount} starvation episode(s), largest starved gap {MaxStarvedFrameGap} frame(s).",
                _streamingFramesBufferedAtStart,
                bufferedFrameCount,
                StreamingStarvationEpisodeCount,
                _streamingMaxStarvedFrameGap);
        }
        else
        {
            GetLogger().LogWarning(
                "LipSyncPlayer streaming playback ended before the inference stream completed: {BufferedFrameCount} of {DeclaredFrameCount} frame(s) buffered, {FramesBufferedAtStart} frame(s) buffered at playback start, {StarvationEpisodeCount} starvation episode(s), largest starved gap {MaxStarvedFrameGap} frame(s).",
                bufferedFrameCount,
                declaredFrameCount?.ToString() ?? "unknown",
                _streamingFramesBufferedAtStart,
                StreamingStarvationEpisodeCount,
                _streamingMaxStarvedFrameGap);
        }
    }

    private void BuildMeshBindings(IReadOnlyList<string> blendshapeNames)
    {
        _meshBindings.Clear();
        MappedMeshCount = 0;
        MappedChannelCount = 0;

        if (blendshapeNames.Count == 0)
        {
            GetLogger().LogWarning("LipSyncPlayer config contains zero blendshape names.");
            return;
        }

        bool[] hasGlobalMapping = new bool[blendshapeNames.Count];

        foreach (MeshInstance3D mesh in EnumerateDescendantMeshes(GetConfiguredSkeleton()))
        {
            if (mesh.Mesh is null)
            {
                continue;
            }

            Dictionary<string, int>? meshShapeMap = null;
            var channels = new List<ShapeChannelBinding>(blendshapeNames.Count);
            for (int shapeIndex = 0; shapeIndex < blendshapeNames.Count; shapeIndex++)
            {
                StringName shapeName = blendshapeNames[shapeIndex];
                int meshShapeIndex = mesh.FindBlendShapeByName(shapeName);
                if (meshShapeIndex < 0)
                {
                    meshShapeMap ??= BuildNormalizedMeshShapeMap(mesh);
                    string normalized = NormalizeBlendshapeName(blendshapeNames[shapeIndex]);
                    if (!meshShapeMap.TryGetValue(normalized, out meshShapeIndex))
                    {
                        continue;
                    }
                }

                channels.Add(new ShapeChannelBinding(shapeIndex, meshShapeIndex));
                hasGlobalMapping[shapeIndex] = true;
            }

            if (channels.Count > 0)
            {
                _meshBindings.Add(new MeshBinding(mesh, [.. channels]));
                MappedMeshCount++;
                MappedChannelCount += channels.Count;
            }
        }

        ILogger<LipSyncPlayer> logger = GetLogger();
        if (!_hasLoggedUnmappedBlendshapes && logger.IsEnabled(LogLevel.Debug))
        {
            List<string>? missingNames = null;
            for (int shapeIndex = 0; shapeIndex < blendshapeNames.Count; shapeIndex++)
            {
                if (hasGlobalMapping[shapeIndex])
                {
                    continue;
                }

                missingNames ??= [];
                missingNames.Add(blendshapeNames[shapeIndex]);
            }

            if (missingNames is { Count: > 0 })
            {
                _hasLoggedUnmappedBlendshapes = true;
                logger.LogDebug(
                    "LipSyncPlayer unmapped blendshapes ({UnmappedBlendshapeCount}): {UnmappedBlendshapes}",
                    missingNames.Count,
                    string.Join(", ", missingNames));
            }
        }
    }

    private ILogger<LipSyncPlayer> GetLogger()
        => _logger ??= GameLoggerResolver.ResolveRequired<LipSyncPlayer>();

    private Skeleton3D GetConfiguredSkeleton()
    {
        Skeleton3D skeleton = Skeleton
            ?? throw new InvalidOperationException("LipSyncPlayer: Skeleton is not assigned.");

        return IsInstanceValid(skeleton)
            ? skeleton
            : throw new InvalidOperationException("LipSyncPlayer: configured Skeleton is no longer valid.");
    }

    private static IEnumerable<MeshInstance3D> EnumerateDescendantMeshes(Node root)
    {
        for (int childIndex = 0; childIndex < root.GetChildCount(); childIndex++)
        {
            Node child = root.GetChild(childIndex);
            if (child is MeshInstance3D mesh)
            {
                yield return mesh;
            }

            foreach (MeshInstance3D descendant in EnumerateDescendantMeshes(child))
            {
                yield return descendant;
            }
        }
    }

    private void ResetAppliedBlendshapeWeights()
    {
        foreach (MeshBinding meshBinding in _meshBindings)
        {
            foreach (ShapeChannelBinding channel in meshBinding.Channels)
            {
                meshBinding.Mesh.SetBlendShapeValue(channel.MeshBlendShapeIndex, 0f);
            }
        }
    }

    private void ApplyFrame(int frameIndex)
    {
        if (TryGetFrameForApplication(frameIndex) is not { } frame)
        {
            return;
        }

        if (_lastAppliedChannelValues.Length != frame.Length)
        {
            _lastAppliedChannelValues = new float[frame.Length];
            Array.Fill(_lastAppliedChannelValues, float.NaN);
        }

        for (int channelIndex = 0; channelIndex < frame.Length; channelIndex++)
        {
            float clampedValue = Mathf.Clamp(frame[channelIndex], 0f, 1f);
            float previousValue = _lastAppliedChannelValues[channelIndex];
            if (!float.IsNaN(previousValue))
            {
                float delta = Mathf.Abs(clampedValue - previousValue);
                if (delta > BlendshapeChangeEpsilon)
                {
                    WeightChangeEventCount++;
                }

                MaxObservedWeightDelta = Mathf.Max(MaxObservedWeightDelta, delta);
            }

            _lastAppliedChannelValues[channelIndex] = clampedValue;
        }

        foreach (MeshBinding meshBinding in _meshBindings)
        {
            foreach (ShapeChannelBinding channel in meshBinding.Channels)
            {
                float weight = channel.SourceFrameIndex < frame.Length
                    ? Mathf.Clamp(frame[channel.SourceFrameIndex], 0f, 1f)
                    : 0f;

                meshBinding.Mesh.SetBlendShapeValue(channel.MeshBlendShapeIndex, weight);
            }
        }

        AppliedFrameCount++;
    }

    /// <summary>
    /// Resolves the frame to apply from the active playback source: the streaming buffer for streaming
    /// sessions, or the fixed batch array.
    /// </summary>
    private float[]? TryGetFrameForApplication(int frameIndex) => _activeStreamingSession switch
    {
        { } session when session.Buffer.TryGetFrame(frameIndex, out float[] frame) => frame,
        { } => null,
        _ => frameIndex >= 0 && frameIndex < _frames.Length ? _frames[frameIndex] : null,
    };

    private void FailPlaybackAndStop(string message)
    {
        StopPlayback(resetWeights: false, clearFrames: false);
        SetPlaybackError(message);
        // A failed session can never reach natural completion, so settle playback watchers now. Without this,
        // a watcher such as a voice's speaking window could stay open indefinitely.
        PlaybackCompleted?.Invoke();
    }

    private void SetPlaybackError(string message)
    {
        PlaybackError = message;
        GD.PushError(message);
    }

    /// <summary>
    /// Normalizes a blendshape name by converting camelCase to lowercase without
    /// separators, so that "eyeLookDownLeft" and "Eye Look Down Left" both become
    /// "eyelookdownleft".
    /// </summary>
    internal static string NormalizeBlendshapeName(string name)
    {
        var output = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is ' ' or '_' or '-')
            {
                continue;
            }

            _ = output.Append(char.ToLowerInvariant(c));
        }

        return output.ToString();
    }

    /// <summary>
    /// Builds a dictionary mapping normalized blendshape names to mesh shape indices.
    /// </summary>
    private static Dictionary<string, int> BuildNormalizedMeshShapeMap(MeshInstance3D mesh)
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);
        if (mesh.Mesh is null)
        {
            return map;
        }

        Mesh? meshResource = mesh.Mesh;
        if (meshResource is ArrayMesh arrayMesh)
        {
            int count = arrayMesh.GetBlendShapeCount();
            for (int i = 0; i < count; i++)
            {
                string meshName = arrayMesh.GetBlendShapeName(i).ToString();
                string normalized = NormalizeBlendshapeName(meshName);
                _ = map.TryAdd(normalized, i);
            }
        }

        return map;
    }

    private readonly record struct ShapeChannelBinding(int SourceFrameIndex, int MeshBlendShapeIndex);

    private sealed record MeshBinding(MeshInstance3D Mesh, ShapeChannelBinding[] Channels);
}
