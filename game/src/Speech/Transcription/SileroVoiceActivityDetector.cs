using Godot;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Local Silero VAD v6 detector executing the committed ONNX model on CPU.
/// </summary>
/// <remarks>
/// The model path is fixed in code and never user-configurable: model selection is a system concern while the
/// speech-probability threshold remains a tuning surface. The detector consumes exact 512-sample mono 16 kHz frames,
/// carrying the model's recurrent state and a 64-sample input context between frames; <see cref="Reset" /> zeroes
/// both. There is no alternative detection path when the model fails to load — initialisation failures propagate to
/// the automatic-input failure policy in <see cref="Transcriber" />.
/// </remarks>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector, IDisposable
{
    /// <summary>Fixed repository model path; never exported, never configurable.</summary>
    public const string ModelPath = "res://models/silero/silero_vad.onnx";

    /// <summary>Exact mono 16 kHz sample count consumed per detection.</summary>
    public const int FrameSampleCount = 512;

    internal const int StateLength = 2 * 1 * 128;

    private const int ContextSampleCount = 64;
    private const int ModelInputSampleCount = ContextSampleCount + FrameSampleCount;

    private readonly ISileroVadSession _session;
    private float[] _state = new float[StateLength];
    private float[] _nextState = new float[StateLength];
    private readonly float[] _modelInput = new float[ModelInputSampleCount];
    private bool _disposed;

    /// <summary>Creates a Silero detector from the fixed committed model path.</summary>
    public SileroVoiceActivityDetector(float speechProbabilityThreshold)
        : this(speechProbabilityThreshold, SileroVadSessions.CreateDefault)
    {
    }

    /// <summary>Creates a Silero detector whose session creation is injected for deterministic testing.</summary>
    /// <remarks>
    /// The factory always receives <see cref="ModelPath" /> — the fixed production path — so tests substitute the
    /// session implementation, never the model location.
    /// </remarks>
    internal SileroVoiceActivityDetector(float speechProbabilityThreshold, Func<string, ISileroVadSession> sessionFactory)
    {
        if (!float.IsFinite(speechProbabilityThreshold) || speechProbabilityThreshold < 0f || speechProbabilityThreshold > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speechProbabilityThreshold),
                "The speech probability threshold must be finite and between zero and one.");
        }

        SpeechProbabilityThreshold = speechProbabilityThreshold;
        _session = sessionFactory(ModelPath);
    }

    /// <summary>Gets the speech-probability threshold applied to each frame.</summary>
    public float SpeechProbabilityThreshold
    {
        get;
    }

    /// <inheritdoc />
    public VoiceActivityDetection Process(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Length != FrameSampleCount)
        {
            throw new ArgumentException(
                $"Silero detection requires exactly {FrameSampleCount} samples at 16 kHz; got {samples.Length}.",
                nameof(samples));
        }

        // The model input carries the retained 64-sample context followed by the current frame.
        samples.CopyTo(_modelInput.AsSpan(ContextSampleCount));

        float speechProbability = _session.Run(_modelInput, _state, _nextState);
        (_state, _nextState) = (_nextState, _state);

        // Retain the latest 64 input samples — the tail of the current frame — as the next frame's context.
        Array.Copy(_modelInput, _modelInput.Length - ContextSampleCount, _modelInput, 0, ContextSampleCount);

        return new VoiceActivityDetection(speechProbability >= SpeechProbabilityThreshold, speechProbability);
    }

    /// <inheritdoc />
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _state.AsSpan().Clear();
        _nextState.AsSpan().Clear();
        _modelInput.AsSpan(0, ContextSampleCount).Clear();
    }

    /// <summary>Deterministically disposes the underlying ONNX session.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }
}

/// <summary>One Silero ONNX inference session invocation boundary for deterministic testing.</summary>
internal interface ISileroVadSession : IDisposable
{
    /// <summary>
    /// Runs one framed invocation, returning the speech probability and writing the next recurrent state into
    /// <paramref name="nextState" />. The state buffers must not overlap.
    /// </summary>
    float Run(float[] modelInput, float[] state, float[] nextState);
}

/// <summary>Builds Silero VAD sessions with an explicit CPU execution provider.</summary>
internal static class SileroVadSessions
{
    /// <summary>Creates the production session from the fixed model path, globalised for the host filesystem.</summary>
    public static ISileroVadSession CreateDefault(string modelPath)
    {
        string absoluteModelPath = ProjectSettings.GlobalizePath(modelPath);
        return File.Exists(absoluteModelPath)
            ? new OnnxSileroVadSession(CreateCpuSession(absoluteModelPath))
            : throw new FileNotFoundException(
                $"The committed Silero VAD model was not found at '{modelPath}'.",
                absoluteModelPath);
    }

    /// <summary>
    /// Creates a CPU-only <see cref="InferenceSession" /> from an absolute model path. The explicit CPU provider
    /// keeps detection working on every host — the GPU-flavoured runtime package must never make Silero require CUDA.
    /// </summary>
    internal static InferenceSession CreateCpuSession(string absoluteModelPath)
    {
        using SessionOptions sessionOptions = new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };

        sessionOptions.AppendExecutionProvider_CPU();
        return new InferenceSession(absoluteModelPath, sessionOptions);
    }
}

/// <summary>ONNX Runtime implementation of the Silero framing and state contract.</summary>
internal sealed class OnnxSileroVadSession(InferenceSession session) : ISileroVadSession
{
    private const long ModelSampleRate = 16000L;

    private static readonly long[] _sampleRateValue = [ModelSampleRate];
    private static readonly int[] _scalarDimensions = [1];

    private bool _disposed;

    public float Run(float[] modelInput, float[] state, float[] nextState)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inputTensor = new DenseTensor<float>(modelInput, [1, modelInput.Length]);
        var stateTensor = new DenseTensor<float>(state, [2, 1, SileroVoiceActivityDetector.StateLength / 2]);
        var sampleRateTensor = new DenseTensor<long>(_sampleRateValue, _scalarDimensions);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", sampleRateTensor),
        ]);

        float speechProbability = 0f;
        foreach (DisposableNamedOnnxValue result in results)
        {
            if (result.Name == "output")
            {
                speechProbability = result.AsTensor<float>()[0];
            }
            else if (result.Name == "stateN")
            {
                CopyState(result.AsTensor<float>(), nextState);
            }
        }

        return speechProbability;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        session.Dispose();
    }

    private static void CopyState(Tensor<float> stateTensor, float[] destination)
    {
        if (stateTensor is DenseTensor<float> denseTensor)
        {
            denseTensor.Buffer.Span.CopyTo(destination.AsSpan());
            return;
        }

        stateTensor.ToArray().AsSpan().CopyTo(destination);
    }
}
