using System.Text;
using Godot;

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
    /// Number of inferred lip-sync frames.
    /// </summary>
    public int FrameCount => _frames.Length;

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
    /// Runtime playback error set when audio-synchronised playback fails.
    /// </summary>
    public string PlaybackError
    {
        get;
        private set;
    } = string.Empty;

    private const float DefaultOutputFps = 30f;
    private const float BlendshapeChangeEpsilon = 1e-4f;

    private float[][] _frames = [];
    private readonly List<MeshBinding> _meshBindings = [];
    private float _outputFps = DefaultOutputFps;
    private bool _isPlaying;
    private double _playbackTimeSeconds;
    private int _lastAppliedFrameIndex = -1;
    private float[] _lastAppliedChannelValues = [];
    private bool _audioWasObservedPlaying;
    private double _audioStartGraceSeconds;

    /// <inheritdoc />
    public override void _Ready() => TryInitialise();

    /// <inheritdoc />
    public override void _ExitTree()
    {
        StopPlayback(resetWeights: true, clearFrames: true);
        DisposeBackend();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_isPlaying || _frames.Length == 0 || PlaybackSpeed <= 0f)
        {
            return;
        }

        bool audioPlaying = AudioPlayer.IsPlaying();
        IsAudioPlaying = audioPlaying;

        if (audioPlaying)
        {
            _audioWasObservedPlaying = true;
            _playbackTimeSeconds = AudioPlayer.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix();
        }
        else
        {
            _audioStartGraceSeconds += delta;

            float durationSeconds = _frames.Length / _outputFps;
            bool nearNaturalEnd = _playbackTimeSeconds >= durationSeconds - (1f / _outputFps);
            if (nearNaturalEnd)
            {
                StopPlayback(resetWeights: false, clearFrames: false);
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

        int targetFrameIndex = Mathf.FloorToInt((float)(_playbackTimeSeconds * _outputFps * PlaybackSpeed));
        if (targetFrameIndex >= _frames.Length)
        {
            if (!LoopPlayback)
            {
                StopPlayback(resetWeights: false, clearFrames: false);
                return;
            }

            float durationSeconds = _frames.Length / _outputFps;
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

        ApplyFrame(targetFrameIndex);
        _lastAppliedFrameIndex = targetFrameIndex;
    }

    /// <summary>
    /// Begins manual playback for the supplied speech clip.
    /// </summary>
    public void Play(AudioStreamWav speech)
    {
        try
        {
            PlayPrepared(PreparePlayback(speech));
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
    public Task<PreparedPlayback> PreparePlaybackAsync(AudioStreamWav speech)
        => Task.Run(() => PreparePlayback(speech));

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
    /// Prepares backend resources required before inference runs.
    /// </summary>
    protected abstract void InitialiseBackend();

    /// <summary>
    /// Executes backend inference and returns normalised playback data.
    /// </summary>
    protected abstract LipSyncInferenceResult RunBackendInference(AudioStreamWav speech);

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
    public sealed record PreparedPlayback(
        AudioStreamWav Speech,
        float[][] Frames,
        IReadOnlyList<string> BlendshapeNames,
        float OutputFps);

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

    private PreparedPlayback PreparePlayback(AudioStreamWav speech)
    {
        if (speech is null)
        {
            throw new InvalidOperationException("LipSyncPlayer: speech clip is not assigned.");
        }

        if (!IsInitialised)
        {
            throw new InvalidOperationException("LipSyncPlayer: cannot prepare playback before initialisation succeeds.");
        }

        LipSyncInferenceResult inferenceResult = RunBackendInference(speech);
        ValidateInferenceResult(inferenceResult);

        return new PreparedPlayback(
            speech,
            inferenceResult.Frames,
            inferenceResult.BlendshapeNames,
            inferenceResult.OutputFps);
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
        _frames = playback.Frames;
        _outputFps = playback.OutputFps > 0f ? playback.OutputFps : DefaultOutputFps;
        BlendshapeChannelCount = _frames[0].Length;

        BuildMeshBindings(playback.BlendshapeNames);
        ResetPlaybackMetrics();
        ResetPlaybackTiming();

        GD.Print(
            $"LipSyncPlayer: loaded {_frames.Length} frames at {_outputFps:0.###} fps, mapped {_meshBindings.Count} mesh(es).");

        AudioPlayer.Stop();
        AudioPlayer.Stream = playback.Speech;
        AudioPlayer.Play();

        _isPlaying = true;
        IsAudioPlaying = true;

        ApplyFrame(0);
        _lastAppliedFrameIndex = 0;
        SetProcess(true);
    }

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
        _audioStartGraceSeconds = 0d;
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

    private void BuildMeshBindings(IReadOnlyList<string> blendshapeNames)
    {
        _meshBindings.Clear();
        MappedMeshCount = 0;
        MappedChannelCount = 0;

        if (blendshapeNames.Count == 0)
        {
            GD.PushWarning("LipSyncPlayer: config contains zero blendshape names.");
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
            GD.PushWarning($"LipSyncPlayer: unmapped blendshapes ({missingNames.Count}): {string.Join(", ", missingNames)}");
        }
    }

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
        if (frameIndex < 0 || frameIndex >= _frames.Length)
        {
            return;
        }

        float[] frame = _frames[frameIndex];
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

    private void FailPlaybackAndStop(string message)
    {
        StopPlayback(resetWeights: false, clearFrames: false);
        SetPlaybackError(message);
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
    protected static string NormalizeBlendshapeName(string name)
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
