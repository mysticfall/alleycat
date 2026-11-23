using AlleyCat.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AlleyCat.Speech.LipSync;

/// <summary>
/// Shared-backend key for a Wav2Arkit inference session (SPCH-001 TR9): players with the same globalised model path
/// and execution provider share one loaded session.
/// </summary>
/// <param name="ModelPath">Normalised absolute ONNX model path.</param>
/// <param name="ExecutionProvider">ONNX Runtime execution provider the session is created for.</param>
internal sealed record Wav2ArkitModelKey(string ModelPath, string ExecutionProvider) : ModelKey
{
    /// <summary>The only execution provider Wav2Arkit uses.</summary>
    public const string CpuExecutionProvider = "CPU";

    /// <summary>Creates a CPU key from a globalised model path.</summary>
    /// <param name="modelPath">Globalised model path.</param>
    /// <returns>The key.</returns>
    public static Wav2ArkitModelKey Create(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        return new Wav2ArkitModelKey(Path.GetFullPath(modelPath), CpuExecutionProvider);
    }
}

/// <summary>
/// Shared Wav2Arkit backend: one loaded model that turns a mono 16 kHz waveform into blendshape frames. Holds no
/// per-player state; tensor names travel with each call because they come from each player's own config.
/// </summary>
internal interface IWav2ArkitInferenceSession : IDisposable
{
    /// <summary>Runs inference over one waveform.</summary>
    /// <param name="monoWaveform">Mono float waveform at the model's sample rate; must not be empty.</param>
    /// <param name="inputName">Input tensor name, or empty for the model's first input.</param>
    /// <param name="outputName">Output tensor name, or empty for the model's first output.</param>
    /// <returns>Blendshape frames, one array of channel values per frame.</returns>
    float[][] Infer(float[] monoWaveform, string inputName, string outputName);
}

/// <summary>
/// ONNX Runtime CPU implementation of <see cref="IWav2ArkitInferenceSession" />.
/// </summary>
/// <param name="session">Loaded ONNX session, owned by this instance.</param>
internal sealed class Wav2ArkitOnnxSession(InferenceSession session) : IWav2ArkitInferenceSession
{

    /// <summary>Loads the model synchronously; the shared registry calls this off the scene thread.</summary>
    /// <param name="absoluteModelPath">Absolute ONNX model path.</param>
    /// <returns>The loaded session.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the model file does not exist.</exception>
    public static Wav2ArkitOnnxSession Load(string absoluteModelPath)
    {
        if (!File.Exists(absoluteModelPath))
        {
            throw new FileNotFoundException(
                $"LipSyncPlayer: ONNX model not found at '{absoluteModelPath}'.",
                absoluteModelPath);
        }

        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };

        return new Wav2ArkitOnnxSession(new InferenceSession(absoluteModelPath, sessionOptions));
    }

    /// <inheritdoc />
    public float[][] Infer(float[] monoWaveform, string inputName, string outputName)
    {
        string resolvedInputName = !string.IsNullOrWhiteSpace(inputName)
            ? inputName
            : session.InputMetadata.Keys.First();

        var inputTensor = new DenseTensor<float>(monoWaveform, [1, monoWaveform.Length]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run([
            NamedOnnxValue.CreateFromTensor(resolvedInputName, inputTensor)
        ]);

        string resolvedOutputName = !string.IsNullOrWhiteSpace(outputName)
            ? outputName
            : session.OutputMetadata.Keys.First();

        DisposableNamedOnnxValue outputValue = results.FirstOrDefault(result => string.Equals(
                result.Name,
                resolvedOutputName,
                StringComparison.Ordinal))
            ?? results[0];

        Tensor<float> outputTensor = outputValue.AsTensor<float>();
        int[] dimensions = outputTensor.Dimensions.ToArray();
        if (dimensions.Length != 3)
        {
            throw new InvalidOperationException(
                $"LipSyncPlayer: expected output tensor rank 3, got rank {dimensions.Length}.");
        }

        int frameCount = dimensions[1];
        int blendshapeCount = dimensions[2];
        float[] outputData = [.. outputTensor];

        float[][] frames = new float[frameCount][];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            float[] frame = new float[blendshapeCount];
            Array.Copy(outputData, frameIndex * blendshapeCount, frame, 0, blendshapeCount);
            frames[frameIndex] = frame;
        }

        return frames;
    }

    /// <inheritdoc />
    public void Dispose() => session.Dispose();
}

/// <summary>
/// Loads one shared Wav2Arkit session through a substitutable factory.
/// </summary>
/// <param name="key">Shared-backend key.</param>
/// <param name="factory">Session factory receiving the absolute model path.</param>
internal sealed class Wav2ArkitModelDefinition(
    Wav2ArkitModelKey key,
    Func<string, IWav2ArkitInferenceSession> factory) : ModelDefinition<IWav2ArkitInferenceSession>
{
    /// <inheritdoc />
    public override ModelKey Key => key;

    /// <inheritdoc />
    public override Task<IWav2ArkitInferenceSession> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(factory(key.ModelPath));
    }
}

/// <summary>
/// Per-player handle over a shared Wav2Arkit session (CORE-011 TR19–22). The player keeps its config, channel
/// mapping and playback state; only the waveform and tensor names travel with each call.
/// </summary>
internal sealed class Wav2ArkitSessionHandle(ISharedModel<IWav2ArkitInferenceSession> model, string consumerName)
    : SharedModelHandle<IWav2ArkitInferenceSession>(model, consumerName)
{
    /// <summary>Completes when the shared session has loaded, or faults with its cached load failure.</summary>
    public Task Loaded => Model.Loaded;

    /// <summary>Queues inference on the shared session.</summary>
    /// <param name="monoWaveform">Per-call waveform.</param>
    /// <param name="inputName">Per-player input tensor name.</param>
    /// <param name="outputName">Per-player output tensor name.</param>
    /// <param name="cancellationToken">Per-call cancellation.</param>
    /// <returns>The inferred frames.</returns>
    public Task<float[][]> InferAsync(
        float[] monoWaveform,
        string inputName,
        string outputName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monoWaveform);
        return Submit(
            session => session.Infer(monoWaveform, inputName, outputName),
            cancellationToken: cancellationToken);
    }
}
