using Godot;

namespace AlleyCat.Speech;

/// <summary>
/// Loads an audio clip through a concrete backend and plays ARKit blendshape values onto character meshes.
/// </summary>
public abstract partial class BlendShapePlayer : Node
{
    /// <summary>
    /// List of meshes to drive.
    /// </summary>
    [Export]
    public MeshInstance3D[] Meshes
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Audio stream used for both inference input and runtime playback.
    /// </summary>
    [Export]
    public AudioStreamWav AudioStream
    {
        get;
        set;
    } = null!;

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
    /// Fallback node path used when the direct audio player reference is unresolved.
    /// </summary>
    [Export]
    public NodePath AudioPlayerPath
    {
        get;
        set;
    } = new("../Subject/Female/Female_export/GeneralSkeleton/HeadBone/AudioStreamPlayer3D");

    /// <summary>
    /// Whether playback should begin automatically when initialisation succeeds.
    /// </summary>
    [Export]
    public bool PlayOnReady
    {
        get;
        set;
    } = true;

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
    /// Number of inferred blendshape frames.
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
    public override void _Ready()
    {
        InitialisationError = string.Empty;
        PlaybackError = string.Empty;
        IsAudioPlaying = false;
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
            GD.PushError($"BlendShapePlayer: initialisation failed: {InitialisationError}");
            SetProcess(false);
        }
    }

    /// <inheritdoc />
    public override void _ExitTree() => DisposeBackend();

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
                _isPlaying = false;
                return;
            }

            bool stillWaitingForAudioStart = !_audioWasObservedPlaying && _audioStartGraceSeconds < 0.5d;
            if (stillWaitingForAudioStart)
            {
                return;
            }

            FailPlaybackAndStop("BlendShapePlayer: audio sync lost - AudioPlayer is not playing during active blendshape playback.");
            return;
        }

        int targetFrameIndex = Mathf.FloorToInt((float)(_playbackTimeSeconds * _outputFps));
        if (targetFrameIndex >= _frames.Length)
        {
            if (!LoopPlayback)
            {
                _isPlaying = false;
                return;
            }

            float durationSeconds = _frames.Length / _outputFps;
            if (durationSeconds <= 0f)
            {
                _isPlaying = false;
                return;
            }

            _playbackTimeSeconds %= durationSeconds;
            targetFrameIndex = Mathf.FloorToInt((float)(_playbackTimeSeconds * _outputFps));
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
    /// Prepares backend resources required before inference runs.
    /// </summary>
    /// <param name="audioStream">Resolved input audio stream.</param>
    protected abstract void InitialiseBackend(AudioStreamWav audioStream);

    /// <summary>
    /// Executes backend inference and returns normalised playback data.
    /// </summary>
    protected abstract BlendShapeInferenceResult RunBackendInference();

    /// <summary>
    /// Releases backend resources allocated during initialisation or inference.
    /// </summary>
    protected abstract void DisposeBackend();

    /// <summary>
    /// Data returned by a concrete blendshape inference backend.
    /// </summary>
    protected sealed record BlendShapeInferenceResult(float[][] Frames, IReadOnlyList<string> BlendshapeNames, float OutputFps);

    private void Initialise()
    {
        BlendshapeChannelCount = 0;
        AppliedFrameCount = 0;
        WeightChangeEventCount = 0;
        MaxObservedWeightDelta = 0f;
        MappedMeshCount = 0;
        MappedChannelCount = 0;
        IsAudioPlaying = false;
        PlaybackError = string.Empty;
        _audioWasObservedPlaying = false;
        _audioStartGraceSeconds = 0d;
        _lastAppliedChannelValues = [];

        if (AudioStream is null)
        {
            throw new InvalidOperationException("BlendShapePlayer: AudioStream is not assigned.");
        }

        AudioStreamPlayer3D resolvedAudioPlayer = AudioPlayer
            ?? GetNodeOrNull<AudioStreamPlayer3D>(AudioPlayerPath)
            ?? throw new InvalidOperationException("BlendShapePlayer: AudioPlayer is not assigned.");

        AudioPlayer = resolvedAudioPlayer;

        AudioStreamWav audioStream = AudioStream;
        InitialiseBackend(audioStream);
        BlendShapeInferenceResult inferenceResult = RunBackendInference();
        _frames = inferenceResult.Frames;
        _outputFps = inferenceResult.OutputFps > 0f ? inferenceResult.OutputFps : DefaultOutputFps;
        BlendshapeChannelCount = _frames.Length > 0
            ? _frames[0].Length
            : inferenceResult.BlendshapeNames.Count;

        BuildMeshBindings(inferenceResult.BlendshapeNames);

        GD.Print(
            $"BlendShapePlayer: loaded {_frames.Length} frames at {_outputFps:0.###} fps, mapped {_meshBindings.Count} mesh(es).");

        if (PlayOnReady && _frames.Length > 0)
        {
            _playbackTimeSeconds = 0d;
            _lastAppliedFrameIndex = -1;
            _isPlaying = true;
            _audioWasObservedPlaying = false;
            _audioStartGraceSeconds = 0d;

            AudioPlayer.Stream = audioStream;
            AudioPlayer.Play();

            ApplyFrame(0);
            _lastAppliedFrameIndex = 0;
        }

        SetProcess(true);
    }

    private void BuildMeshBindings(IReadOnlyList<string> blendshapeNames)
    {
        _meshBindings.Clear();
        MappedMeshCount = 0;
        MappedChannelCount = 0;

        if (blendshapeNames.Count == 0)
        {
            GD.PushWarning("BlendShapePlayer: config contains zero blendshape names.");
            return;
        }

        bool[] hasGlobalMapping = new bool[blendshapeNames.Count];

        foreach (MeshInstance3D mesh in Meshes)
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
            GD.PushWarning($"BlendShapePlayer: unmapped blendshapes ({missingNames.Count}): {string.Join(", ", missingNames)}");
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
        _isPlaying = false;
        IsAudioPlaying = false;
        PlaybackError = message;
        IsInitialised = false;
        InitialisationError = message;
        GD.PushError(message);
        SetProcess(false);
    }

    /// <summary>
    /// Normalizes a blendshape name by converting camelCase to lowercase without
    /// separators, so that "eyeLookDownLeft" and "Eye Look Down Left" both become
    /// "eyelookdownleft".
    /// </summary>
    protected static string NormalizeBlendshapeName(string name)
    {
        var output = new System.Text.StringBuilder(name.Length);
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
