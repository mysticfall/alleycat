using AlleyCat.Models;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// Shared-backend key for a Supertonic pipeline (SPCH-007 TR-16): generators with the same globalised model directory
/// and requested execution backend share one loaded pipeline.
/// </summary>
/// <param name="ModelDirectory">Normalised absolute model directory.</param>
/// <param name="RequestedBackend">Execution backend requested by the generator, before any CPU fallback.</param>
internal sealed record SupertonicPipelineKey(string ModelDirectory, SupertonicExecutionBackend RequestedBackend)
    : ModelKey
{
    /// <summary>Creates a key from a globalised directory, normalising separators and trailing slashes.</summary>
    /// <param name="modelDirectory">Globalised model directory.</param>
    /// <param name="requestedBackend">Requested execution backend.</param>
    /// <returns>The key.</returns>
    public static SupertonicPipelineKey Create(string modelDirectory, SupertonicExecutionBackend requestedBackend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        string normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDirectory));
        return new SupertonicPipelineKey(normalised, requestedBackend);
    }
}

/// <summary>
/// Loads one shared Supertonic pipeline. The CUDA → CPU fallback inside the pipeline factory therefore runs once per
/// shared backend, and <paramref name="onLoaded" /> reports its diagnostics once per backend.
/// </summary>
/// <param name="key">Shared-backend key.</param>
/// <param name="factory">Pipeline factory receiving the model directory and requested backend.</param>
/// <param name="onLoaded">Optional once-per-backend diagnostics callback; it runs off the scene thread.</param>
internal sealed class SupertonicPipelineDefinition(
    SupertonicPipelineKey key,
    Func<string, SupertonicExecutionBackend, ISupertonicInferencePipeline> factory,
    Action<SupertonicPipelineKey, ISupertonicInferencePipeline>? onLoaded = null)
    : ModelDefinition<ISupertonicInferencePipeline>
{
    /// <inheritdoc />
    public override ModelKey Key => key;

    /// <inheritdoc />
    public override Task<ISupertonicInferencePipeline> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ISupertonicInferencePipeline pipeline = factory(key.ModelDirectory, key.RequestedBackend);
        onLoaded?.Invoke(key, pipeline);
        return Task.FromResult(pipeline);
    }
}

/// <summary>
/// Result of one synthesis through a shared pipeline: the vocoder samples and the rate they were produced at.
/// </summary>
/// <param name="Samples">Mono float32 PCM samples.</param>
/// <param name="SampleRate">Sample rate of <paramref name="Samples" />, in hertz.</param>
internal sealed record SupertonicSynthesisResult(float[] Samples, int SampleRate);

/// <summary>
/// Per-generator handle over a shared Supertonic pipeline (CORE-011 TR19–22). Voice style, language, speed and
/// quality steps travel with each request; the shared pipeline keeps no state between generators' calls.
/// </summary>
internal sealed class SupertonicPipelineHandle(
    ISharedModel<ISupertonicInferencePipeline> model,
    string consumerName)
    : SharedModelHandle<ISupertonicInferencePipeline>(model, consumerName), ISupertonicInferencePipeline
{
    private BackendSnapshot? _snapshot;

    /// <summary>Completes when the shared pipeline has loaded, or faults with its cached load failure.</summary>
    public Task Loaded => Model.Loaded;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Thrown before this handle has completed a synthesis.</exception>
    public int SampleRate => RequireSnapshot().SampleRate;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Thrown before this handle has completed a synthesis.</exception>
    public SupertonicExecutionBackend ActiveBackend => RequireSnapshot().ActiveBackend;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Thrown before this handle has completed a synthesis.</exception>
    public string? BackendFallbackReason => RequireSnapshot().FallbackReason;

    /// <summary>
    /// Queues a synthesis on the shared pipeline. The returned task settles off the calling thread with the samples,
    /// the synthesis failure, the cached load failure, or cancellation.
    /// </summary>
    /// <param name="request">Per-call synthesis inputs.</param>
    /// <param name="cancellationToken">Per-call cancellation.</param>
    /// <returns>The synthesised samples and their sample rate.</returns>
    public Task<SupertonicSynthesisResult> SynthesiseAsync(
        SupertonicSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Submit(
            pipeline =>
            {
                float[] samples = pipeline.Synthesise(request);
                var snapshot = new BackendSnapshot(pipeline.SampleRate, pipeline.ActiveBackend, pipeline.BackendFallbackReason);
                Volatile.Write(ref _snapshot, snapshot);
                return new SupertonicSynthesisResult(samples, snapshot.SampleRate);
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Runs one generator request end to end: waits for the shared load, prepares the per-request inputs off the
    /// calling thread, then synthesises. Every stage observes this handle's lifetime, so a request started before the
    /// generator releases the handle (for example on scene exit) settles as cancelled, never as
    /// <see cref="ObjectDisposedException" /> or a stale result (CORE-011 UR3, TR22). A cached load failure still
    /// faults the request.
    /// </summary>
    /// <param name="prepareRequest">Builds the request, for example by loading the voice style; runs on the thread pool.</param>
    /// <returns>The synthesised samples and their sample rate.</returns>
    public async Task<SupertonicSynthesisResult> SynthesiseWhenLoadedAsync(Func<SupertonicSynthesisRequest> prepareRequest)
    {
        ArgumentNullException.ThrowIfNull(prepareRequest);
        CancellationToken lifetime = Lifetime;
        try
        {
            await Model.Loaded.WaitAsync(lifetime).ConfigureAwait(false);
            SupertonicSynthesisRequest request = await Task.Run(prepareRequest, lifetime).ConfigureAwait(false);
            lifetime.ThrowIfCancellationRequested();
            return await SynthesiseAsync(request, lifetime).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            // Release can mark the handle disposed just before it cancels the lifetime, so a submission landing in
            // that window is the same release race; the generator's own misuse guard rejects requests started after
            // release before they reach this handle.
            throw new OperationCanceledException("The generator released its Supertonic handle before the request settled.", lifetime);
        }
    }

    /// <summary>
    /// Synchronous form of <see cref="SynthesiseAsync" /> for the <see cref="ISupertonicInferencePipeline" /> contract.
    /// It waits for the shared scheduler, so it must never be called on the scene thread.
    /// </summary>
    /// <param name="request">Per-call synthesis inputs.</param>
    /// <returns>The synthesised samples.</returns>
    public float[] Synthesise(SupertonicSynthesisRequest request)
        => SynthesiseAsync(request).GetAwaiter().GetResult().Samples;

    private BackendSnapshot RequireSnapshot()
        => Volatile.Read(ref _snapshot)
            ?? throw new InvalidOperationException(
                "The shared Supertonic pipeline metadata is available only after this handle completes a synthesis.");

    private sealed record BackendSnapshot(
        int SampleRate,
        SupertonicExecutionBackend ActiveBackend,
        string? FallbackReason);
}
