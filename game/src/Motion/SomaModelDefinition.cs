using AlleyCat.Models;

namespace AlleyCat.Motion;

/// <summary>
/// CORE-011 definition of the shared MotionBricks backend. It loads only the native session; per-consumer codec state
/// (model frames, rest pose, index maps) belongs to each <see cref="SomaInferenceWorker" />, so codec failures are
/// never cached under this key. Concurrency stays at the default of one call at a time.
/// </summary>
/// <param name="modelPath">Absolute, globalised model path, resolved on the scene thread.</param>
public sealed class SomaModelDefinition(string modelPath) : ModelDefinition<SomaNativeSession>
{
    private readonly SomaModelKey _key = new(
        string.IsNullOrWhiteSpace(modelPath) ? throw new ArgumentException("A model path is required.", nameof(modelPath)) : modelPath,
        SomaNativeSession.CudaExecutionProvider);

    /// <inheritdoc />
    public override ModelKey Key => _key;

    /// <inheritdoc />
    public override Task<SomaNativeSession> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SomaNativeSession.Load(_key.ModelPath));
}
