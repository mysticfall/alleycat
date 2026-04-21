using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AlleyCat.Speech;

/// <summary>
/// wav2arkit_cpu-backed <see cref="BlendShapePlayer"/> implementation.
/// </summary>
[GlobalClass]
public partial class Wav2ArkitBlendShapePlayer : BlendShapePlayer
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

    private const float DefaultOutputFps = 30f;

    private InferenceSession? _session;
    private Wav2ArkitConfig _config = Wav2ArkitConfig.CreateDefault();
    private float[] _monoWaveform = [];

    /// <inheritdoc />
    protected override void InitialiseBackend(AudioStreamWav audioStream)
    {
        _config = LoadConfig(ConfigPath);
        _monoWaveform = LoadAudioWaveform(audioStream, _config.Preprocessing.SampleRate);
        _session = BuildSession(ModelPath);
    }

    /// <inheritdoc />
    protected override BlendShapeInferenceResult RunBackendInference()
    {
        InferenceSession session = _session
            ?? throw new InvalidOperationException("BlendShapePlayer: backend session was not initialised.");

        float[][] frames = RunInference(session, _config, _monoWaveform);
        return new BlendShapeInferenceResult(frames, _config.BlendshapeNames, _config.OutputSpec.Fps);
    }

    /// <inheritdoc />
    protected override void DisposeBackend()
    {
        _session?.Dispose();
        _session = null;
        _monoWaveform = [];
        _config = Wav2ArkitConfig.CreateDefault();
    }

    private static Wav2ArkitConfig LoadConfig(string configPath)
    {
        string absoluteConfigPath = ProjectSettings.GlobalizePath(configPath);
        string configJson = File.ReadAllText(absoluteConfigPath);
        Wav2ArkitConfig? config = JsonSerializer.Deserialize<Wav2ArkitConfig>(configJson, _jsonOptions)
            ?? throw new InvalidOperationException($"BlendShapePlayer: failed to parse config at '{configPath}'.");

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

        outputValue ??= results[0];

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
