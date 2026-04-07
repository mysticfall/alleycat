using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AlleyCat.Speech;

/// <summary>
/// Loads an audio clip, runs wav2arkit ONNX inference, and plays ARKit blendshape values onto character meshes.
/// </summary>
[GlobalClass]
public partial class BlendShapePlayer : Node
{
    /// <summary>
    /// Path to wav2arkit JSON config.
    /// </summary>
    [Export(PropertyHint.File)]
    public string ConfigPath
    {
        get;
        set;
    } = "res://models/wav2arkit_cpu/config.json";

    /// <summary>
    /// Path to wav2arkit ONNX model file.
    /// </summary>
    [Export(PropertyHint.File)]
    public string ModelPath
    {
        get;
        set;
    } = "res://models/wav2arkit_cpu/wav2arkit_cpu.onnx";

    /// <summary>
    /// Skeleton that owns immediate child meshes to drive.
    /// </summary>
    [Export]
    public Skeleton3D Skeleton
    {
        get;
        set;
    } = null!;

    /// <summary>
    /// Fallback node path used when the direct skeleton reference is unresolved.
    /// </summary>
    [Export]
    public NodePath SkeletonPath
    {
        get;
        set;
    } = new("../Subject/Female/Female_export/GeneralSkeleton");

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

    private static readonly StringName[] _canonicalArkitBlendshapes =
    [
        "jawOpen",
        "mouthSmileLeft",
        "mouthSmileRight",
        "eyeBlinkLeft",
        "eyeBlinkRight"
    ];

    private InferenceSession? _session;
    private Wav2ArkitConfig _config = Wav2ArkitConfig.CreateDefault();
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
            IsInitialised = false;
            InitialisationError = ex.ToString();
            GD.PushError($"BlendShapePlayer: initialisation failed: {InitialisationError}");
            SetProcess(false);
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        _session?.Dispose();
        _session = null;
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

        _config = LoadConfig(ConfigPath);
        _outputFps = _config.OutputSpec.Fps > 0 ? _config.OutputSpec.Fps : DefaultOutputFps;

        Skeleton3D resolvedSkeleton = Skeleton
            ?? GetNodeOrNull<Skeleton3D>(SkeletonPath)
            ?? throw new InvalidOperationException("BlendShapePlayer: Skeleton is not assigned.");

        if (AudioStream is null)
        {
            throw new InvalidOperationException("BlendShapePlayer: AudioStream is not assigned.");
        }

        AudioStreamPlayer3D resolvedAudioPlayer = AudioPlayer
            ?? GetNodeOrNull<AudioStreamPlayer3D>(AudioPlayerPath)
            ?? throw new InvalidOperationException("BlendShapePlayer: AudioPlayer is not assigned.");

        Skeleton = resolvedSkeleton;
        AudioPlayer = resolvedAudioPlayer;

        AudioStreamWav audioStream = AudioStream;
        float[] monoWaveform = LoadAudioWaveform(audioStream, _config.Preprocessing.SampleRate);
        _session = BuildSession(ModelPath);
        InferenceSession session = _session;
        _frames = RunInference(session, _config, monoWaveform);
        BlendshapeChannelCount = _frames.Length > 0 ? _frames[0].Length : _config.BlendshapeNames.Count;

        Skeleton3D skeleton = resolvedSkeleton;

        BuildMeshBindings(skeleton, _config.BlendshapeNames);

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

    private void BuildMeshBindings(Skeleton3D skeleton, IReadOnlyList<string> blendshapeNames)
    {
        _meshBindings.Clear();
        MappedMeshCount = 0;
        MappedChannelCount = 0;

        if (blendshapeNames.Count == 0)
        {
            GD.PushWarning("BlendShapePlayer: config contains zero blendshape names.");
            return;
        }

        List<MeshInstance3D> meshNodes = FindEligibleImmediateMeshChildren(skeleton);

        if (meshNodes.Count == 0)
        {
            GD.PushWarning($"BlendShapePlayer: no eligible immediate MeshInstance3D nodes found under '{skeleton.GetPath()}'.");
            return;
        }

        bool[] hasGlobalMapping = new bool[blendshapeNames.Count];

        foreach (MeshInstance3D mesh in meshNodes)
        {
            if (mesh.Mesh is null)
            {
                continue;
            }

            var channels = new List<ShapeChannelBinding>(blendshapeNames.Count);
            for (int shapeIndex = 0; shapeIndex < blendshapeNames.Count; shapeIndex++)
            {
                StringName shapeName = blendshapeNames[shapeIndex];
                int meshShapeIndex = mesh.FindBlendShapeByName(shapeName);
                if (meshShapeIndex < 0)
                {
                    continue;
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

    private static List<MeshInstance3D> FindEligibleImmediateMeshChildren(Skeleton3D skeleton)
    {
        var output = new List<MeshInstance3D>();
        foreach (Node child in skeleton.GetChildren())
        {
            if (child is MeshInstance3D meshInstance)
            {
                if (HasCanonicalArkitBlendshape(meshInstance))
                {
                    output.Add(meshInstance);
                }
            }
        }

        return output;
    }

    private static bool HasCanonicalArkitBlendshape(MeshInstance3D mesh)
    {
        if (mesh.Mesh is null)
        {
            return false;
        }

        foreach (StringName blendshapeName in _canonicalArkitBlendshapes)
        {
            if (mesh.FindBlendShapeByName(blendshapeName) >= 0)
            {
                return true;
            }
        }

        return false;
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

    private static Wav2ArkitConfig LoadConfig(string configPath)
    {
        string absoluteConfigPath = ProjectSettings.GlobalizePath(configPath);
        string configJson = File.ReadAllText(absoluteConfigPath);
        Wav2ArkitConfig? config = JsonSerializer.Deserialize<Wav2ArkitConfig>(configJson, _jsonOptions) ?? throw new InvalidOperationException($"BlendShapePlayer: failed to parse config at '{configPath}'.");

        return config.Preprocessing.SampleRate <= 0
            ? throw new InvalidOperationException("BlendShapePlayer: config sample rate must be > 0.")
            : config.BlendshapeNames.Count == 0
            ? throw new InvalidOperationException("BlendShapePlayer: config blendshape_names is empty.")
            : config;
    }

    private static InferenceSession BuildSession(string modelPath)
    {
        string absoluteModelPath = ProjectSettings.GlobalizePath(modelPath);
        if (!File.Exists(absoluteModelPath))
        {
            throw new FileNotFoundException($"BlendShapePlayer: ONNX model not found at '{modelPath}'.", absoluteModelPath);
        }

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };

        return new InferenceSession(absoluteModelPath, sessionOptions);
    }

    private static float[][] RunInference(InferenceSession session, Wav2ArkitConfig config, float[] monoWaveform)
    {
        if (monoWaveform.Length == 0)
        {
            return [];
        }

        string inputName = !string.IsNullOrWhiteSpace(config.InputSpec.Name)
            ? config.InputSpec.Name
            : session.InputMetadata.Keys.First();

        var inputTensor = new DenseTensor<float>(monoWaveform, [1, monoWaveform.Length]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run([
            NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
        ]);

        string outputName = !string.IsNullOrWhiteSpace(config.OutputSpec.Name)
            ? config.OutputSpec.Name
            : session.OutputMetadata.Keys.First();

        DisposableNamedOnnxValue? outputValue = results.FirstOrDefault(result => string.Equals(result.Name,
            outputName,
            StringComparison.Ordinal));

        outputValue ??= results.First();

        Tensor<float> outputTensor = outputValue.AsTensor<float>();
        int[] dimensions = outputTensor.Dimensions.ToArray();
        if (dimensions.Length != 3)
        {
            throw new InvalidOperationException(
                $"BlendShapePlayer: expected output tensor rank 3, got rank {dimensions.Length}.");
        }

        int frameCount = dimensions[1];
        int blendshapeCount = dimensions[2];
        float[] outputData = [.. outputTensor];

        float[][] frames = new float[frameCount][];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float[] frame = new float[blendshapeCount];
            int sourceOffset = frameIndex * blendshapeCount;
            Array.Copy(outputData, sourceOffset, frame, 0, blendshapeCount);
            frames[frameIndex] = frame;
        }

        return frames;
    }

    private static float[] LoadAudioWaveform(AudioStreamWav audioStream, int targetSampleRate)
    {
        if (audioStream.Format != AudioStreamWav.FormatEnum.Format16Bits)
        {
            throw new InvalidOperationException(
                $"BlendShapePlayer: expected AudioStreamWav format {AudioStreamWav.FormatEnum.Format16Bits}, got {audioStream.Format}.");
        }

        if (audioStream.MixRate != 16000)
        {
            throw new InvalidOperationException($"BlendShapePlayer: expected 16000 Hz audio, got {audioStream.MixRate} Hz.");
        }

        if (audioStream.Stereo)
        {
            throw new InvalidOperationException("BlendShapePlayer: expected mono audio stream, but stream is stereo.");
        }

        if (targetSampleRate != 16000)
        {
            throw new InvalidOperationException(
                $"BlendShapePlayer: model expects {targetSampleRate} Hz but strict prototype requires 16000 Hz.");
        }

        byte[] data = audioStream.Data.Length == 0
            ? throw new InvalidOperationException("BlendShapePlayer: AudioStreamWav contains no PCM data.")
            : audioStream.Data;

        return DecodePcm16Bytes(data);
    }

    private static float[] DecodePcm16Bytes(IReadOnlyList<byte> data)
    {
        if ((data.Count & 1) != 0)
        {
            throw new InvalidOperationException("BlendShapePlayer: PCM16 data length must be even.");
        }

        int sampleCount = data.Count / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int dataOffset = i * 2;
            short value = (short)(data[dataOffset] | (data[dataOffset + 1] << 8));
            samples[i] = value / 32768f;
        }

        return samples;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly record struct ShapeChannelBinding(int SourceFrameIndex, int MeshBlendShapeIndex);

    private sealed record MeshBinding(MeshInstance3D Mesh, ShapeChannelBinding[] Channels);

    private sealed class Wav2ArkitConfig
    {
        [JsonPropertyName("preprocessing")]
        public PreprocessingConfig Preprocessing
        {
            get;
            init;
        } = new();

        [JsonPropertyName("input_spec")]
        public InputSpecConfig InputSpec
        {
            get;
            init;
        } = new();

        [JsonPropertyName("output_spec")]
        public OutputSpecConfig OutputSpec
        {
            get;
            init;
        } = new();

        [JsonPropertyName("blendshape_names")]
        public List<string> BlendshapeNames
        {
            get;
            init;
        } = [];

        public static Wav2ArkitConfig CreateDefault() => new();
    }

    private sealed class PreprocessingConfig
    {
        [JsonPropertyName("sample_rate")]
        public int SampleRate
        {
            get;
            init;
        } = 16000;
    }

    private sealed class InputSpecConfig
    {
        [JsonPropertyName("name")]
        public string Name
        {
            get;
            init;
        } = "audio_waveform";
    }

    private sealed class OutputSpecConfig
    {
        [JsonPropertyName("name")]
        public string Name
        {
            get;
            init;
        } = "blendshapes";

        [JsonPropertyName("fps")]
        public float Fps
        {
            get;
            init;
        } = DefaultOutputFps;
    }
}
