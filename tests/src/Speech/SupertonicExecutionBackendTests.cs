using System.Globalization;
using AlleyCat.Speech.Generation.Supertonic;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the Supertonic execution-backend fallback chain (SPCH-007 TR 3).
/// </summary>
/// <remarks>
/// CUDA provider appending and session creation are injected as stubs so the fallback behaviour is
/// deterministic on hosts without CUDA support.
/// </remarks>
public sealed class SupertonicExecutionBackendTests
{
    /// <summary>
    /// CPU requests must build CPU-only session options directly, retaining the shared optimisation
    /// settings through session construction, without any CUDA provider attempt.
    /// </summary>
    [Fact]
    public void Create_WithCpuRequest_BuildsCpuOnlyOptions_DisposesOptionsAfterSessionConstruction()
    {
        List<RecordedCall> calls = [];
        List<SessionOptions> appendedTo = [];

        SupertonicExecutionBackendResolution<object> resolution = SupertonicSessionFactory.Create(
            SupertonicExecutionBackend.Cpu,
            (options, backend) =>
            {
                Assert.False(options.IsInvalid, "Session options must remain alive while sessions are constructed.");
                calls.Add(new RecordedCall(options, backend));
                return new object();
            },
            appendedTo.Add);

        Assert.False(resolution.UsedFallback);
        Assert.Equal(SupertonicExecutionBackend.Cpu, resolution.RequestedBackend);
        Assert.Equal(SupertonicExecutionBackend.Cpu, resolution.ActiveBackend);
        Assert.Null(resolution.FallbackReason);
        Assert.NotNull(resolution.Value);

        RecordedCall call = Assert.Single(calls);
        Assert.Equal(SupertonicExecutionBackend.Cpu, call.Backend);
        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, call.Options.GraphOptimizationLevel);
        Assert.Equal(ExecutionMode.ORT_SEQUENTIAL, call.Options.ExecutionMode);
        Assert.Empty(appendedTo);
        Assert.True(call.Options.IsInvalid, "Expected CPU session options to be disposed after session construction.");
    }

    /// <summary>
    /// CUDA requests whose sessions initialise successfully must report CUDA as the active backend and
    /// dispose options once construction completes.
    /// </summary>
    [Fact]
    public void Create_WithCudaRequest_AndSuccessfulSessionCreation_ActivatesCudaAndDisposesOptions()
    {
        List<RecordedCall> calls = [];
        int appendCount = 0;

        SupertonicExecutionBackendResolution<object> resolution = SupertonicSessionFactory.Create(
            SupertonicExecutionBackend.Cuda,
            (options, backend) =>
            {
                Assert.False(options.IsInvalid, "Session options must remain alive while sessions are constructed.");
                calls.Add(new RecordedCall(options, backend));
                return new object();
            },
            _ => appendCount++);

        Assert.False(resolution.UsedFallback);
        Assert.Equal(SupertonicExecutionBackend.Cuda, resolution.ActiveBackend);
        Assert.Null(resolution.FallbackReason);
        Assert.Equal(1, appendCount);

        RecordedCall call = Assert.Single(calls);
        Assert.Equal(SupertonicExecutionBackend.Cuda, call.Backend);
        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, call.Options.GraphOptimizationLevel);
        Assert.Equal(ExecutionMode.ORT_SEQUENTIAL, call.Options.ExecutionMode);
        Assert.True(call.Options.IsInvalid, "Expected CUDA session options to be disposed after session construction.");
    }

    /// <summary>
    /// CUDA requests whose session creation throws must rebuild CPU-only options exactly once and
    /// report the fallback with the failure reason.
    /// </summary>
    [Fact]
    public void Create_WithCudaRequest_AndThrowingSessionCreation_FallsBackToCpuWithRebuiltOptions()
    {
        List<RecordedCall> calls = [];
        FakeInferenceSession? partiallyCreatedCudaSession = null;

        SupertonicExecutionBackendResolution<object> resolution = SupertonicSessionFactory.Create(
            SupertonicExecutionBackend.Cuda,
            (options, backend) =>
            {
                Assert.False(options.IsInvalid, "Session options must remain alive while sessions are constructed.");
                calls.Add(new RecordedCall(options, backend));
                if (backend == SupertonicExecutionBackend.Cuda)
                {
                    partiallyCreatedCudaSession = new FakeInferenceSession();
                    try
                    {
                        throw new InvalidOperationException("CUDA session initialisation exploded.");
                    }
                    catch
                    {
                        partiallyCreatedCudaSession.Dispose();
                        throw;
                    }
                }

                return new object();
            },
            _ => { });
        Assert.True(resolution.UsedFallback);
        Assert.Equal(SupertonicExecutionBackend.Cuda, resolution.RequestedBackend);
        Assert.Equal(SupertonicExecutionBackend.Cpu, resolution.ActiveBackend);
        Assert.NotNull(resolution.Value);
        Assert.NotNull(resolution.FallbackReason);
        Assert.Contains("CUDA session initialisation exploded.", resolution.FallbackReason, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", resolution.FallbackReason, StringComparison.Ordinal);

        Assert.Equal(2, calls.Count);
        Assert.Equal(SupertonicExecutionBackend.Cuda, calls[0].Backend);
        Assert.Equal(SupertonicExecutionBackend.Cpu, calls[1].Backend);

        // The failed CUDA attempt cleans up its partial session and the CPU retry receives fresh,
        // CPU-only options with the shared optimisation settings.
        Assert.NotNull(partiallyCreatedCudaSession);
        Assert.True(partiallyCreatedCudaSession.IsDisposed, "Expected partial CUDA sessions to be disposed before CPU retry.");
        Assert.NotSame(calls[0].Options, calls[1].Options);
        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, calls[1].Options.GraphOptimizationLevel);
        Assert.Equal(ExecutionMode.ORT_SEQUENTIAL, calls[1].Options.ExecutionMode);
        Assert.True(calls[0].Options.IsInvalid, "Expected the failed CUDA session options to be disposed.");
        Assert.True(calls[1].Options.IsInvalid, "Expected CPU retry session options to be disposed after session construction.");
    }

    /// <summary>
    /// A CUDA provider append failure, such as missing native libraries, must take the same single
    /// fallback path as a session-initialisation failure.
    /// </summary>
    [Fact]
    public void Create_WithCudaRequest_AndFailingProviderAppend_FallsBackToCpu()
    {
        List<RecordedCall> calls = [];

        SupertonicExecutionBackendResolution<object> resolution = SupertonicSessionFactory.Create(
            SupertonicExecutionBackend.Cuda,
            (options, backend) =>
            {
                Assert.False(options.IsInvalid, "Session options must remain alive while sessions are constructed.");
                calls.Add(new RecordedCall(options, backend));
                return new object();
            },
            _ => throw new DllNotFoundException("libonnxruntime_providers_cuda.so"));

        Assert.True(resolution.UsedFallback);
        Assert.Equal(SupertonicExecutionBackend.Cpu, resolution.ActiveBackend);
        Assert.NotNull(resolution.FallbackReason);
        Assert.Contains("libonnxruntime_providers_cuda.so", resolution.FallbackReason, StringComparison.Ordinal);

        RecordedCall call = Assert.Single(calls);
        Assert.Equal(SupertonicExecutionBackend.Cpu, call.Backend);
        Assert.True(call.Options.IsInvalid, "Expected CPU fallback session options to be disposed after session construction.");
    }

    /// <summary>
    /// Context-local CUDA test scopes must favour the innermost active override and remain correct when
    /// nested scopes are disposed in either order.
    /// </summary>
    [Fact]
    public void OverrideCudaExecutionProviderForTesting_NestedScopes_RestoreCorrectlyAfterLifoAndOutOfOrderDisposal()
    {
        const string outerFailure = "outer CUDA override";
        const string innerFailure = "inner CUDA override";

        using IDisposable outerScope = SupertonicSessionFactory.OverrideCudaExecutionProviderForTesting(
            static _ => throw new InvalidOperationException(outerFailure));
        AssertCudaFallbackReason(outerFailure);

        using IDisposable innerScope = SupertonicSessionFactory.OverrideCudaExecutionProviderForTesting(
            static _ => throw new InvalidOperationException(innerFailure));
        AssertCudaFallbackReason(innerFailure);

        innerScope.Dispose();
        AssertCudaFallbackReason(outerFailure);

        using IDisposable secondInnerScope = SupertonicSessionFactory.OverrideCudaExecutionProviderForTesting(
            static _ => throw new InvalidOperationException(innerFailure));
        outerScope.Dispose();
        AssertCudaFallbackReason(innerFailure);

        secondInnerScope.Dispose();
        SupertonicExecutionBackendResolution<object> resolution = SupertonicSessionFactory.Create(
            SupertonicExecutionBackend.Cuda,
            static (_, _) => new object(),
            static _ => { });

        Assert.False(resolution.UsedFallback);
        Assert.Equal(SupertonicExecutionBackend.Cuda, resolution.ActiveBackend);
        Assert.Null(resolution.FallbackReason);
    }

    /// <summary>
    /// The exported default must request CUDA so capable hosts accelerate inference out of the box.
    /// </summary>
    [Fact]
    public void SupertonicExecutionBackend_DefaultEnumValue_IsCuda()
        => Assert.Equal(SupertonicExecutionBackend.Cuda, default);

    /// <summary>
    /// The pipeline factory default must keep call-site compatibility by requesting CUDA when no
    /// backend is passed.
    /// </summary>
    [Fact]
    public void SupertonicInferencePipeline_Create_DefaultBackendParameter_IsCuda()
    {
        System.Reflection.ParameterInfo? backendParameter = typeof(SupertonicInferencePipeline)
            .GetMethod(nameof(SupertonicInferencePipeline.Create))?
            .GetParameters()
            .SingleOrDefault(parameter => parameter.Name == "executionBackend");

        Assert.NotNull(backendParameter);
        Assert.True(backendParameter.HasDefaultValue);
        Assert.Equal((int)SupertonicExecutionBackend.Cuda, Convert.ToInt32(backendParameter.RawDefaultValue, CultureInfo.InvariantCulture));
    }

    private static void AssertCudaFallbackReason(string expectedFailure)
    {
        SupertonicExecutionBackendResolution<object> resolution = SupertonicSessionFactory.Create(
            SupertonicExecutionBackend.Cuda,
            static (_, _) => new object(),
            static _ => { });

        Assert.True(resolution.UsedFallback);
        Assert.Equal(SupertonicExecutionBackend.Cpu, resolution.ActiveBackend);
        Assert.NotNull(resolution.FallbackReason);
        Assert.Contains(expectedFailure, resolution.FallbackReason, StringComparison.Ordinal);
    }

    private sealed record RecordedCall(SessionOptions Options, SupertonicExecutionBackend Backend);

    private sealed class FakeInferenceSession : IDisposable
    {
        public bool IsDisposed
        {
            get;
            private set;
        }

        public void Dispose() => IsDisposed = true;
    }
}
