using AlleyCat.Motion;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Motion;

/// <summary>MB-N01 ownership tests: barriers model native work, not CUDA cancellation or scene behaviour.</summary>
public sealed class SomaInferenceWorkerTests
{
    private static Transform3D[][] Poses() => [new Transform3D[1], new Transform3D[1], new Transform3D[1], new Transform3D[1]];

    private sealed class Session(Func<Transform3D[][], Transform3D[][]> generate) : SomaInferenceWorker.ISession
    {
        public bool Disposed
        {
            get; private set;
        }
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            Assert.False(Disposed);
            return generate(context);
        }
        public void Dispose() => Disposed = true;
    }

    /// <summary>A scene may close while model construction is still inside native code.</summary>
    [Fact]
    public async Task ShutdownDuringLoad_DoesNotWaitOrDisposeBeforeConstructionReturns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new Session(_ => throw new InvalidOperationException("Must not generate after shutdown."));
        using var worker = new SomaInferenceWorker(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return session;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            _ = worker.Submit(Poses(), Poses());
            worker.Dispose();
            Assert.False(worker.Completion.IsCompleted);
            Assert.False(session.Disposed);
            Assert.False(worker.TryTake(out _));
        }
        finally
        {
            release.Set();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(session.Disposed);
    }

    /// <summary>Replacement owns copied inputs and cannot expose a superseded generation.</summary>
    [Fact]
    public async Task Replacement_RejectsStaleOutputAndCopiesInputs()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        int calls = 0;
        Transform3D[][]? received = null;
        var session = new Session(context =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            else
            {
                received = context;
                second.Set();
            }
            return context;
        });
        var observed = new List<long>();
        using var worker = new SomaInferenceWorker(() => session, result =>
        {
            observed.Add(result.Request);
            throw new IOException("Synthetic diagnostic sink failure must not change provider results.");
        });
        try
        {
            _ = worker.Submit(Poses(), Poses());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Transform3D[][] input = Poses();
            long current = worker.Submit(input, Poses());
            input[0] = [];
            Assert.False(worker.TryTake(out _));
            release.Set();
            Assert.True(second.Wait(TimeSpan.FromSeconds(10)));
            SomaInferenceWorker.Result? result = null;
            Assert.True(SpinWait.SpinUntil(() => worker.TryTake(out result), TimeSpan.FromSeconds(10)));
            Assert.Equal(current, result!.Request);
            Assert.Null(result.Error);
            _ = Assert.Single(received![0]);
            Assert.NotSame(input, received);
        }
        finally
        {
            release.Set();
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(session.Disposed);
        Assert.Equal(2, observed.Count); // Includes stale and current output despite observer errors.
    }

    /// <summary>Native work completes before disposal even when the scene stops during generation.</summary>
    [Fact]
    public async Task ShutdownDuringGeneration_DiscardsResultAndDisposesAfterReturn()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new Session(context =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return context;
        });
        using var worker = new SomaInferenceWorker(() => session);
        try
        {
            _ = worker.Submit(Poses(), Poses());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            worker.Dispose();
            Assert.False(session.Disposed);
            Assert.False(worker.Completion.IsCompleted);
        }
        finally
        {
            release.Set();
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(session.Disposed);
        Assert.False(worker.TryTake(out _));
    }

    /// <summary>A load failure is finite and remains available to the current request, including late submissions.</summary>
    [Fact]
    public async Task LoadFailure_IsDeliveredWithoutRetry()
    {
        int attempts = 0;
        var failure = new InvalidDataException("Missing model fixture.");
        using var worker = new SomaInferenceWorker(() =>
        {
            _ = Interlocked.Increment(ref attempts);
            throw failure;
        });
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        long request = worker.Submit(Poses(), Poses());
        Assert.True(worker.TryTake(out SomaInferenceWorker.Result? result));
        Assert.Equal(request, result!.Request);
        Assert.Same(failure, result.Error);
        Assert.Equal(1, attempts);
    }

    /// <summary>Cancellation while loading must not turn a later load failure into a phantom request result.</summary>
    [Fact]
    public async Task CancelDuringLoad_DiscardsFailure()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var worker = new SomaInferenceWorker(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            throw new InvalidDataException("Cancelled load fixture.");
        });
        try
        {
            _ = worker.Submit(Poses(), Poses());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            worker.CancelRequest();
        }
        finally
        {
            release.Set();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(worker.TryTake(out _));
    }
}
