using AlleyCat.Models;
using AlleyCat.Motion;
using AlleyCat.Tests.Models;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Motion;

/// <summary>
/// CORE-011 TR19–23 for MotionBricks: workers are per-consumer handles over one shared native session per model key,
/// with codec state and its failures kept per consumer. Native-free: the shared backend is a detached session.
/// </summary>
public sealed class SomaSharedModelTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(10);
    private static readonly SomaModelKey _key = new("/models/mmm_v6_soma_fp32.onnx", SomaNativeSession.CudaExecutionProvider);

    private static Transform3D[][] Poses(float marker = 0) =>
        [.. Enumerable.Range(0, 4).Select(_ => new[] { new Transform3D(Basis.Identity, new Vector3(marker, 0, 0)) })];

    private sealed class CountingDefinition(ModelKey key, Exception? failure = null) : ModelDefinition<SomaNativeSession>
    {
        private int _loads;

        public int Loads => Volatile.Read(ref _loads);

        public override ModelKey Key => key;

        public override Task<SomaNativeSession> LoadAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _loads);
            return failure is null ? Task.FromResult(SomaNativeSession.CreateDetached()) : Task.FromException<SomaNativeSession>(failure);
        }
    }

    private sealed class Session(float marker, Action? generating = null) : SomaInferenceWorker.ISession
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            generating?.Invoke();
            return [[new Transform3D(Basis.Identity, new Vector3(marker, 0, 0))]];
        }

        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private static SomaInferenceWorker.Result Take(SomaInferenceWorker worker)
    {
        SomaInferenceWorker.Result? result = null;
        Assert.True(SpinWait.SpinUntil(() => worker.TryTake(out result), _bound), "No result was published.");
        return result!;
    }

    /// <summary>Consumers with equal keys load one native session, but each builds and uses its own codec session.</summary>
    [Fact]
    public async Task HandlesOnOneKey_LoadOnceAndKeepPerConsumerSessions()
    {
        var observer = new RecordingObserver();
        var registry = new SharedModelRegistry(observer: observer);
        var firstDefinition = new CountingDefinition(_key);
        var secondDefinition = new CountingDefinition(_key with
        {
        });
        var backends = new List<SomaNativeSession>();
        SomaInferenceWorker Create(CountingDefinition definition, float marker)
        {
            return new(registry.Resolve(definition), backend =>
            {
                lock (backends)
                {
                    backends.Add(backend);
                }
                return new Session(marker);
            });
        }

        using SomaInferenceWorker first = Create(firstDefinition, 1);
        using SomaInferenceWorker second = Create(secondDefinition, 2);
        long firstRequest = first.Submit(Poses(), Poses());
        long secondRequest = second.Submit(Poses(), Poses());
        SomaInferenceWorker.Result firstResult = Take(first);
        SomaInferenceWorker.Result secondResult = Take(second);

        Assert.Equal(firstRequest, firstResult.Request);
        Assert.Equal(secondRequest, secondResult.Request);
        Assert.Equal(1, firstResult.Poses![0][0].Origin.X);
        Assert.Equal(2, secondResult.Poses![0][0].Origin.X);
        Assert.Equal(1, firstDefinition.Loads + secondDefinition.Loads);
        Assert.Equal(1, observer.Count(SharedModelEventKind.LoadStarted));
        Assert.Equal(2, backends.Count);
        Assert.Same(backends[0], backends[1]);
        await registry.Stop().WaitAsync(_bound);
    }

    /// <summary>A codec failure is its handle's failure only; the shared key stays loaded and usable by others.</summary>
    [Fact]
    public async Task CodecFailure_StaysWithItsHandleAndIsNotCached()
    {
        var observer = new RecordingObserver();
        var registry = new SharedModelRegistry(observer: observer);
        var definition = new CountingDefinition(_key);
        var failure = new InvalidDataException("Source is missing a required SOMA joint.");

        using var failed = new SomaInferenceWorker(registry.Resolve(definition), _ => throw failure);
        await failed.Completion.WaitAsync(_bound);
        long failedRequest = failed.Submit(Poses(), Poses());
        Assert.True(failed.TryTake(out SomaInferenceWorker.Result? failedResult));
        Assert.Equal(failedRequest, failedResult!.Request);
        Assert.Same(failure, failedResult.Error);

        using var healthy = new SomaInferenceWorker(registry.Resolve(new CountingDefinition(_key)), _ => new Session(3));
        _ = healthy.Submit(Poses(), Poses());
        SomaInferenceWorker.Result healthyResult = Take(healthy);
        Assert.Null(healthyResult.Error);
        Assert.Equal(3, healthyResult.Poses![0][0].Origin.X);

        Assert.Equal(1, definition.Loads);
        Assert.Equal(0, observer.Count(SharedModelEventKind.LoadFailed));
        await registry.Stop().WaitAsync(_bound);
    }

    /// <summary>A shared load failure reaches every handle of the key as the same failure, without a second load.</summary>
    [Fact]
    public async Task SharedLoadFailure_ReachesEveryHandleWithoutRetry()
    {
        var registry = new SharedModelRegistry();
        var failure = new FileNotFoundException("MotionBricks SOMA FP32 model is missing.", _key.ModelPath);
        var definition = new CountingDefinition(_key, failure);
        int factories = 0;
        SomaInferenceWorker.ISession Factory(SomaNativeSession backend)
        {
            _ = Interlocked.Increment(ref factories);
            return new Session(0);
        }

        using var first = new SomaInferenceWorker(registry.Resolve(definition), Factory);
        long pending = first.Submit(Poses(), Poses());
        using var second = new SomaInferenceWorker(registry.Resolve(new CountingDefinition(_key, failure)), Factory);
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(_bound);

        Assert.True(first.TryTake(out SomaInferenceWorker.Result? pendingResult));
        Assert.Equal(pending, pendingResult!.Request);
        Assert.Same(failure, pendingResult.Error);
        long late = second.Submit(Poses(), Poses());
        Assert.True(second.TryTake(out SomaInferenceWorker.Result? lateResult));
        Assert.Equal(late, lateResult!.Request);
        Assert.Same(failure, lateResult.Error);
        Assert.Equal(1, definition.Loads);
        Assert.Equal(0, factories);
        await registry.Stop().WaitAsync(_bound);
    }

    /// <summary>
    /// Releasing a handle during its in-flight call neither blocks nor disposes the shared session: its own session is
    /// released after the call returns, and another consumer keeps generating on the same backend.
    /// </summary>
    [Fact]
    public async Task HandleRelease_DuringGeneration_ReleasesOwnSessionButNotSharedBackend()
    {
        var observer = new RecordingObserver();
        var registry = new SharedModelRegistry(observer: observer);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var gated = new Session(1, () =>
        {
            entered.Set();
            Assert.True(release.Wait(_bound));
        });
        var released = new SomaInferenceWorker(registry.Resolve(new CountingDefinition(_key)), _ => gated);
        try
        {
            _ = released.Submit(Poses(), Poses());
            Assert.True(entered.Wait(_bound));
            released.Dispose();
            Assert.False(released.Completion.IsCompleted);
            Assert.Equal(0, gated.Disposals);
        }
        finally
        {
            release.Set();
        }
        await released.Completion.WaitAsync(_bound);
        Assert.Equal(1, gated.Disposals);
        Assert.False(released.TryTake(out _));
        Assert.Equal(1, released.DiscardedResults);

        using var survivor = new SomaInferenceWorker(registry.Resolve(new CountingDefinition(_key)), _ => new Session(2));
        _ = survivor.Submit(Poses(), Poses());
        Assert.Null(Take(survivor).Error);
        Assert.Equal(0, observer.Count(SharedModelEventKind.BackendDisposed));

        await registry.Stop().WaitAsync(_bound);
        Assert.Equal(1, observer.Count(SharedModelEventKind.BackendDisposed));
    }

    /// <summary>The key covers the resolved path and execution provider; different paths never share.</summary>
    [Fact]
    public void ModelDefinition_KeysByResolvedPathAndExecutionProvider()
    {
        Assert.Equal(new SomaModelDefinition("/models/a.onnx").Key, new SomaModelDefinition("/models/a.onnx").Key);
        Assert.NotEqual(new SomaModelDefinition("/models/a.onnx").Key, new SomaModelDefinition("/models/b.onnx").Key);
        Assert.Equal(new SomaModelKey("/models/a.onnx", SomaNativeSession.CudaExecutionProvider),
            new SomaModelDefinition("/models/a.onnx").Key);
    }
}
