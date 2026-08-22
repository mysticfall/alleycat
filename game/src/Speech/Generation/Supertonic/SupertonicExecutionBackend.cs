using Microsoft.ML.OnnxRuntime;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// Execution backend used for Supertonic ONNX inference.
/// </summary>
public enum SupertonicExecutionBackend
{
    /// <summary>
    /// CUDA GPU inference, falling back to CPU when the CUDA execution provider or its sessions
    /// cannot be initialised on the host.
    /// </summary>
    Cuda,

    /// <summary>
    /// CPU-only inference without any CUDA initialisation attempt.
    /// </summary>
    Cpu
}

/// <summary>
/// Outcome of resolving a requested Supertonic execution backend onto initialised ONNX sessions.
/// </summary>
/// <typeparam name="T">Value produced from the active session options by the session-creation delegate.</typeparam>
/// <param name="Value">Value produced by the successful session-creation call.</param>
/// <param name="RequestedBackend">Backend originally requested through configuration.</param>
/// <param name="ActiveBackend">Backend that actually initialised and produced <paramref name="Value" />.</param>
/// <param name="FallbackReason">
/// Description of the CUDA failure when a fallback occurred, otherwise <see langword="null" />.
/// </param>
public sealed record SupertonicExecutionBackendResolution<T>(
    T Value,
    SupertonicExecutionBackend RequestedBackend,
    SupertonicExecutionBackend ActiveBackend,
    string? FallbackReason)
{
    /// <summary>
    /// Gets a value indicating whether the requested backend was unavailable and a CPU fallback was applied.
    /// </summary>
    public bool UsedFallback => RequestedBackend != ActiveBackend;
}

/// <summary>
/// Builds Supertonic ONNX session options and resolves the active execution backend through the
/// incremental CUDA → CPU fallback chain.
/// </summary>
/// <remarks>
/// CUDA availability is determined by appending the CUDA execution provider and attempting session
/// initialisation inside a guarded block — never by inspecting the registered provider list, because
/// the GPU package lists CUDA even when its native libraries cannot load on the host.
/// </remarks>
public static class SupertonicSessionFactory
{
    private static readonly AsyncLocal<CudaExecutionProviderOverride?> _testingCudaExecutionProviderOverride = new();

    /// <summary>
    /// Resolves the requested backend onto fully initialised sessions.
    /// </summary>
    /// <typeparam name="T">Value produced from the session options by <paramref name="createSessions" />.</typeparam>
    /// <param name="requestedBackend">Backend to attempt; <see cref="SupertonicExecutionBackend.Cpu" /> never attempts CUDA.</param>
    /// <param name="createSessions">
    /// Delegate that initialises every ONNX session from the supplied options. It receives the backend
    /// flavour of the options so the created value can record the backend it was built with. The delegate
    /// must dispose partially created sessions before rethrowing so the fallback rebuild starts clean.
    /// </param>
    /// <param name="appendCudaExecutionProvider">
    /// Optional override for the CUDA provider append, injected by unit tests; defaults to the real append.
    /// </param>
    /// <returns>The resolution carrying the sessions plus the requested and active backends.</returns>
    public static SupertonicExecutionBackendResolution<T> Create<T>(
        SupertonicExecutionBackend requestedBackend,
        Func<SessionOptions, SupertonicExecutionBackend, T> createSessions,
        Action<SessionOptions>? appendCudaExecutionProvider = null)
    {
        ArgumentNullException.ThrowIfNull(createSessions);

        if (requestedBackend == SupertonicExecutionBackend.Cpu)
        {
            return BuildCpuResolution(requestedBackend, createSessions);
        }

        try
        {
            using SessionOptions cudaOptions = BuildSessionOptions();
            (GetTestingCudaExecutionProviderOverride() ?? appendCudaExecutionProvider ?? AppendCudaExecutionProvider)(cudaOptions);

            return new SupertonicExecutionBackendResolution<T>(
                createSessions(cudaOptions, SupertonicExecutionBackend.Cuda),
                requestedBackend,
                SupertonicExecutionBackend.Cuda,
                null);
        }
        catch (Exception ex)
        {
            // Any CUDA failure — missing native libraries, provider append, or session initialisation —
            // falls back to a full CPU rebuild rather than failing generation.
            return new SupertonicExecutionBackendResolution<T>(
                BuildCpuValue(createSessions),
                requestedBackend,
                SupertonicExecutionBackend.Cpu,
                DescribeFailure(ex));
        }
    }

    private static SupertonicExecutionBackendResolution<T> BuildCpuResolution<T>(
        SupertonicExecutionBackend requestedBackend,
        Func<SessionOptions, SupertonicExecutionBackend, T> createSessions)
        => new(
            BuildCpuValue(createSessions),
            requestedBackend,
            SupertonicExecutionBackend.Cpu,
            null);

    private static T BuildCpuValue<T>(Func<SessionOptions, SupertonicExecutionBackend, T> createSessions)
    {
        using SessionOptions cpuOptions = BuildSessionOptions();
        return createSessions(cpuOptions, SupertonicExecutionBackend.Cpu);
    }

    private static void AppendCudaExecutionProvider(SessionOptions sessionOptions)
        => sessionOptions.AppendExecutionProvider_CUDA(deviceId: 0);

    /// <summary>
    /// Temporarily replaces CUDA provider appending for a scoped test.
    /// </summary>
    /// <remarks>
    /// The override is intentionally internal and context-local, so it flows into a test's <c>Task.Run</c>
    /// inference work without affecting unrelated execution contexts. Scopes are identity-tracked and inactive
    /// scopes are skipped, preserving the innermost active override even when an outer scope is disposed first.
    /// </remarks>
    internal static IDisposable OverrideCudaExecutionProviderForTesting(Action<SessionOptions> appendCudaExecutionProvider)
    {
        ArgumentNullException.ThrowIfNull(appendCudaExecutionProvider);

        CudaExecutionProviderOverride overrideScope = new(
            appendCudaExecutionProvider,
            _testingCudaExecutionProviderOverride.Value);
        _testingCudaExecutionProviderOverride.Value = overrideScope;
        return overrideScope;
    }

    private static Action<SessionOptions>? GetTestingCudaExecutionProviderOverride()
    {
        for (CudaExecutionProviderOverride? overrideScope = _testingCudaExecutionProviderOverride.Value;
             overrideScope is not null;
             overrideScope = overrideScope.Previous)
        {
            if (!overrideScope.IsDisposed)
            {
                return overrideScope.AppendCudaExecutionProvider;
            }
        }

        return null;
    }

    private static SessionOptions BuildSessionOptions()
        => new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
        };

    private static string DescribeFailure(Exception exception)
        => $"{exception.GetType().Name}: {exception.Message}";

    private sealed class CudaExecutionProviderOverride(
        Action<SessionOptions> appendCudaExecutionProvider,
        CudaExecutionProviderOverride? previous) : IDisposable
    {
        private int _disposed;

        public Action<SessionOptions> AppendCudaExecutionProvider { get; } = appendCudaExecutionProvider;

        public CudaExecutionProviderOverride? Previous { get; } = previous;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(_testingCudaExecutionProviderOverride.Value, this))
            {
                _testingCudaExecutionProviderOverride.Value = Previous;
            }
        }
    }
}
