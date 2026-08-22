namespace AlleyCat.Speech.LipSync;

/// <summary>
/// Thread-safe buffer of converted lip-sync frames shared between a streaming inference reader thread
/// (producer) and main-thread playback (consumer).
/// </summary>
/// <remarks>
/// <para>The producer appends frames and metadata as they arrive from the backend stream; the consumer
/// reads frames by playback cursor index on the main thread. A simple lock is sufficient because the
/// producer appends at the backend frame rate (tens of records per second at most).</para>
/// <para>The startup gate completes once metadata and a startup buffer worth of frames have arrived, which
/// is the point streaming playback may become audible while the download continues in the
/// background.</para>
/// </remarks>
internal sealed class StreamingFrameBuffer(float startupBufferSeconds)
{
    private readonly Lock _lock = new();
    private readonly List<float[]> _frames = [];
    private readonly float _startupBufferSeconds = Math.Max(0f, startupBufferSeconds);
    private readonly TaskCompletionSource _startupGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IReadOnlyList<string>? _blendshapeNames;
    private float _outputFps;
    private int _startupBufferFrameCount;
    private bool _completed;
    private int _declaredFrameCount = -1;
    private Exception? _error;
    private bool _startupGateResolved;
    private int _frameChannelCount = -1;

    /// <summary>
    /// Number of frames appended so far.
    /// </summary>
    public int FrameCount
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>
    /// Indicates the producer marked the stream complete.
    /// </summary>
    public bool IsCompleted
    {
        get
        {
            lock (_lock)
            {
                return _completed;
            }
        }
    }

    /// <summary>
    /// Indicates the producer failed; see <see cref="Error"/> for the recorded failure.
    /// </summary>
    public bool IsFaulted
    {
        get
        {
            lock (_lock)
            {
                return _error is not null;
            }
        }
    }

    /// <summary>
    /// First recorded producer failure, or null when the producer has not failed.
    /// </summary>
    public Exception? Error
    {
        get
        {
            lock (_lock)
            {
                return _error;
            }
        }
    }

    /// <summary>
    /// Frame count declared by the stream's completion record, or null when not yet known.
    /// </summary>
    public int? DeclaredFrameCount
    {
        get
        {
            lock (_lock)
            {
                return _declaredFrameCount >= 0 ? _declaredFrameCount : null;
            }
        }
    }

    /// <summary>
    /// Task completing once metadata and the startup buffer worth of frames have arrived, or the stream
    /// completed with fewer frames. Completes exceptionally when the producer fails before then.
    /// </summary>
    public Task StartupBufferReady => _startupGate.Task;

    /// <summary>
    /// Captures the stream metadata (output frame rate and retained blendshape channel names). Must be
    /// called exactly once, before any frames are appended.
    /// </summary>
    public void SetMetadata(float outputFps, IReadOnlyList<string> blendshapeNames)
    {
        lock (_lock)
        {
            if (_blendshapeNames is not null)
            {
                throw new InvalidOperationException(
                    "LipSyncPlayer: streaming metadata record was received more than once.");
            }

            if (outputFps <= 0f)
            {
                throw new InvalidOperationException(
                    $"LipSyncPlayer: streaming metadata declared invalid frame rate {outputFps}.");
            }

            if (blendshapeNames.Count == 0)
            {
                throw new InvalidOperationException("LipSyncPlayer: streaming metadata declared no blendshape names.");
            }

            _outputFps = outputFps;
            _blendshapeNames = blendshapeNames;
            _startupBufferFrameCount = Math.Max(1, (int)Math.Ceiling(_startupBufferSeconds * outputFps));
            SignalStartupGateLocked();
        }
    }

    /// <summary>
    /// Appends a converted frame. Requires metadata first, and the channel count must match the first
    /// appended frame.
    /// </summary>
    public void Append(float[] frame)
    {
        lock (_lock)
        {
            if (_blendshapeNames is null)
            {
                throw new InvalidOperationException("LipSyncPlayer: streaming frame record arrived before metadata.");
            }

            if (_completed)
            {
                throw new InvalidOperationException(
                    "LipSyncPlayer: streaming frame record arrived after the complete record.");
            }

            if (_frameChannelCount < 0)
            {
                _frameChannelCount = frame.Length;
            }
            else if (frame.Length != _frameChannelCount)
            {
                throw new InvalidOperationException(
                    $"LipSyncPlayer: streaming frame {_frames.Count} has {frame.Length} channels, expected {_frameChannelCount}.");
            }

            _frames.Add(frame);
            SignalStartupGateLocked();
        }
    }

    /// <summary>
    /// Marks the stream complete. The caller is responsible for validating the declared frame count
    /// against the received frames before calling.
    /// </summary>
    public void MarkComplete(int declaredFrameCount)
    {
        lock (_lock)
        {
            if (_blendshapeNames is null)
            {
                throw new InvalidOperationException("LipSyncPlayer: streaming complete record arrived before metadata.");
            }

            _declaredFrameCount = declaredFrameCount;
            _completed = true;
            SignalStartupGateLocked();
        }
    }

    /// <summary>
    /// Records a producer failure. The first failure wins; later failures are ignored.
    /// </summary>
    public void MarkFailed(Exception error)
    {
        lock (_lock)
        {
            _error ??= error;
            SignalStartupGateLocked();
        }
    }

    /// <summary>
    /// Attempts to read the frame at the supplied zero-based index.
    /// </summary>
    public bool TryGetFrame(int frameIndex, out float[] frame)
    {
        lock (_lock)
        {
            if (frameIndex >= 0 && frameIndex < _frames.Count)
            {
                frame = _frames[frameIndex];
                return true;
            }
        }

        frame = [];
        return false;
    }

    /// <summary>
    /// Attempts to read the captured stream metadata.
    /// </summary>
    public bool TryGetMetadata(out float outputFps, out IReadOnlyList<string> blendshapeNames)
    {
        lock (_lock)
        {
            if (_blendshapeNames is null)
            {
                outputFps = 0f;
                blendshapeNames = [];
                return false;
            }

            outputFps = _outputFps;
            blendshapeNames = _blendshapeNames;
            return true;
        }
    }

    private void SignalStartupGateLocked()
    {
        if (_startupGateResolved)
        {
            return;
        }

        if (_error is not null)
        {
            _startupGateResolved = true;
            _ = _startupGate.TrySetException(_error);
            return;
        }

        if (_blendshapeNames is null)
        {
            return;
        }

        if (_completed || _frames.Count >= _startupBufferFrameCount)
        {
            _startupGateResolved = true;
            _ = _startupGate.TrySetResult();
        }
    }
}
