using Microsoft.ML.OnnxRuntime;

namespace AlleyCat.Motion;

/// <summary>
/// Shared MotionBricks backend: the native SOMA inference session only, loaded once per <see cref="SomaModelKey" />
/// and shared by every consumer under CORE-011. It holds no per-consumer codec state and no state carried between
/// calls. Calls must run one at a time on a worker, never on the scene thread.
/// </summary>
public sealed class SomaNativeSession : IDisposable
{
    /// <summary>The only execution provider this backend loads: CUDA on device 0.</summary>
    public const string CudaExecutionProvider = "CUDA:0";

    private readonly InferenceSession? _session;

    private SomaNativeSession(InferenceSession? session)
    {
        _session = session;
    }

    /// <summary>
    /// Loads the FP32 model with the CUDA execution provider. Blocking native work: call off the scene thread.
    /// </summary>
    /// <param name="modelPath">Absolute, already globalised model path.</param>
    /// <param name="nativeObservation">Optional passive phase observer; it must not access scene nodes.</param>
    /// <returns>The loaded backend.</returns>
    /// <exception cref="FileNotFoundException">Thrown before any native call when the model file is missing.</exception>
    public static SomaNativeSession Load(string modelPath, Action<string>? nativeObservation = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("MotionBricks SOMA FP32 model is missing. Provision game/models/motionbricks/onnx or set ModelPath/MOTIONBRICKS_MODEL.", modelPath);
        }
        using var options = new SessionOptions();
        options.AppendExecutionProvider_CUDA(0);
        nativeObservation?.Invoke("load-enter");
        try
        {
            return new SomaNativeSession(new InferenceSession(modelPath, options));
        }
        finally { nativeObservation?.Invoke("load-exit"); }
    }

    /// <summary>
    /// A backend without a native model, for handles whose per-consumer session never calls native inference
    /// (deterministic test sessions). <see cref="Run" /> throws.
    /// </summary>
    internal static SomaNativeSession CreateDetached() => new(null);

    /// <summary>Runs one native inference call. Worker-only; the caller owns and disposes the outputs.</summary>
    /// <param name="inputs">Named model inputs.</param>
    /// <returns>Model outputs.</returns>
    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
        => (_session ?? throw new InvalidOperationException("This MotionBricks backend has no native model."))
            .Run(inputs);

    /// <summary>Releases the native session. Only the shared model registry calls this, when its owner's lifetime ends.</summary>
    public void Dispose() => _session?.Dispose();
}
