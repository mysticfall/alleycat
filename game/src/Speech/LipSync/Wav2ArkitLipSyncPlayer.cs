using System.Text.Json;
using System.Text.Json.Serialization;
using AlleyCat.Models;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Speech.LipSync;

/// <summary>
/// wav2arkit_cpu-backed <see cref="LipSyncPlayer"/> implementation.
/// </summary>
/// <remarks>
/// The inference session is a shared backend under CORE-011 (SPCH-001 TR9), keyed by the globalised model path:
/// initialisation requests it without blocking the scene thread, and <see cref="LipSyncPlayer.IsInitialised"/> becomes
/// true only after the config is validated and the shared load completes.
/// </remarks>
[GlobalClass]
public partial class Wav2ArkitLipSyncPlayer : LipSyncPlayer
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

    private Wav2ArkitSessionHandle? _session;
    private Wav2ArkitConfig _config = Wav2ArkitConfig.CreateDefault();
    private ISharedModelRegistry? _sharedModels;
    private Func<string, IWav2ArkitInferenceSession> _sessionFactory = Wav2ArkitOnnxSession.Load;

    /// <inheritdoc />
    protected override int BackendSampleRate => _config.Preprocessing.SampleRate;

    /// <summary>
    /// Supplies the shared model registry and, optionally, the session factory used when this player's key is first
    /// loaded, instead of the <see cref="Game" />-owned registry and the ONNX loader (CORE-011 TR5, TR23). Must be
    /// called before the node becomes ready; the caller owns the registry's lifetime.
    /// </summary>
    /// <param name="sharedModels">Registry to resolve the shared session from.</param>
    /// <param name="sessionFactory">Optional session factory receiving the absolute model path.</param>
    internal void OverrideSharedModelsForTesting(
        ISharedModelRegistry sharedModels,
        Func<string, IWav2ArkitInferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sharedModels);
        if (_session is not null)
        {
            throw new InvalidOperationException("The Wav2Arkit session handle has already been created.");
        }

        _sharedModels = sharedModels;
        _sessionFactory = sessionFactory ?? _sessionFactory;
    }

    /// <inheritdoc />
    protected override void InitialiseBackend()
    {
        _config = LoadConfig(ConfigPath);

        // Godot-dependent inputs are resolved here on the scene thread; the shared load runs off it.
        var key = Wav2ArkitModelKey.Create(ProjectSettings.GlobalizePath(ModelPath));
        ISharedModelRegistry registry = _sharedModels ??= Game.Instance.GetRequiredService<ISharedModelRegistry>();
        _session?.Dispose();
        _session = new Wav2ArkitSessionHandle(
            registry.Resolve(new Wav2ArkitModelDefinition(key, _sessionFactory)),
            Name);
    }

    /// <inheritdoc />
    protected override Task? GetPendingBackendLoad() => _session?.Loaded;

    /// <inheritdoc />
    protected override LipSyncInferenceResult RunBackendInference(
        AudioStreamWav speech,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Wav2ArkitSessionHandle session = _session
            ?? throw new InvalidOperationException("LipSyncPlayer: backend session was not initialised.");

        float[] monoWaveform = LoadAudioWaveform(speech, _config.Preprocessing.SampleRate);
        // Preparation runs off the scene thread; the synchronous Play path keeps its documented blocking inference.
        float[][] frames = monoWaveform.Length == 0
            ? []
            : session.InferAsync(monoWaveform, _config.InputSpec.Name, _config.OutputSpec.Name, cancellationToken)
                .GetAwaiter()
                .GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        return new LipSyncInferenceResult(frames, _config.BlendshapeNames, _config.OutputSpec.Fps);
    }

    /// <inheritdoc />
    protected override void DisposeBackend()
    {
        // Releasing the handle never unloads the shared session (CORE-011 TR10, TR22).
        _session?.Dispose();
        _session = null;
        _config = Wav2ArkitConfig.CreateDefault();
    }

    private static Wav2ArkitConfig LoadConfig(string configPath)
    {
        string absoluteConfigPath = ProjectSettings.GlobalizePath(configPath);
        string configJson = File.ReadAllText(absoluteConfigPath);
        Wav2ArkitConfig? config = JsonSerializer.Deserialize<Wav2ArkitConfig>(configJson, _jsonOptions)
            ?? throw new InvalidOperationException($"LipSyncPlayer: failed to parse config at '{configPath}'.");

        return config.Preprocessing.SampleRate <= 0
            ? throw new InvalidOperationException("LipSyncPlayer: config sample rate must be > 0.")
            : config.BlendshapeNames.Count == 0
            ? throw new InvalidOperationException("LipSyncPlayer: config blendshape_names is empty.")
            : config;
    }

    private static float[] LoadAudioWaveform(AudioStreamWav audioStream, int targetSampleRate)
    {
        if (targetSampleRate != 16000)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: model expects {targetSampleRate} Hz but strict prototype requires 16000 Hz.");
        }

        byte[] data = audioStream.Data.Length == 0
            ? throw new InvalidOperationException("LipSyncPlayer: AudioStreamWav contains no PCM data.")
            : audioStream.Data;

        return DecodePcm16Bytes(data);
    }

    private static float[] DecodePcm16Bytes(IReadOnlyList<byte> data)
    {
        if ((data.Count & 1) != 0)
        {
            throw new InvalidOperationException("LipSyncPlayer: PCM16 data length must be even.");
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
