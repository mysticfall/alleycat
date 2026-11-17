using AlleyCat.Motion;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Motion;

/// <summary>Application ownership retains removed-scene workers through their native disposal.</summary>
public sealed class MotionBricksWorkersTests
{
    private sealed class Session : SomaInferenceWorker.ISession
    {
        internal int Disposals;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target) => context;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    /// <summary>Removed scenes do not unregister pending native construction; every worker is drained exactly once.</summary>
    [Fact]
    public async Task Stop_RetainsRemovedSceneWorkerAndClosesAdmission()
    {
        var owner = new MotionBricksWorkers();
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var first = new Session();
        var second = new Session();
        SomaInferenceWorker Create(Session session)
        {
            return owner.Create(() =>
        {
            _ = entered.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return session;
        });
        }

        SomaInferenceWorker removed = Create(first);
        SomaInferenceWorker active = Create(second);
        Task? completion = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            removed.Dispose();
            completion = owner.Stop();
            Assert.Same(completion, owner.Stop());
            Assert.False(completion.IsCompleted);
            Assert.True(owner.IsStopping);
            _ = Assert.Throws<ObjectDisposedException>(() => owner.Create(() => new Session()));
            Transform3D[][] poses = [new Transform3D[1], new Transform3D[1], new Transform3D[1], new Transform3D[1]];
            _ = Assert.Throws<ObjectDisposedException>(() => active.Submit(poses, poses));
            Assert.Equal(0, first.Disposals);
            Assert.Equal(0, second.Disposals);
        }
        finally
        {
            release.Set();
            await (completion ?? owner.Stop()).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
    }

    /// <summary>Without native workers shutdown has no asynchronous delay.</summary>
    [Fact]
    public void Stop_WithoutWorkersCompletesImmediately()
    {
        var owner = new MotionBricksWorkers();
        Assert.True(owner.Stop().IsCompletedSuccessfully);
    }
}
