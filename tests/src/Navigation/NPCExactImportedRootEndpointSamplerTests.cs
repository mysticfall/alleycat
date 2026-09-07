using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCExactImportedRootEndpointSamplerTests
{
    private const float Delta = 1f / 60f;

    [Fact]
    public void AdjacentIntervals_ReuseSharedEndpointAndMatchUncachedCompositionAcrossScales()
    {
        var cache = new NPCExactImportedRootEndpointCache<object>(8);
        object snapshot = new();
        object animation = new();
        int sampleCalls = 0;
        using NPCExactImportedRootEndpointCache<object>.Scope scope = cache.BeginScope(snapshot);

        NPCImportedRootDelta cachedAB = SampleInterval(
            cache, snapshot, animation, 2, 3, 0.1d, 0.2d, 1f, true, ref sampleCalls);
        NPCImportedRootDelta cachedBC = SampleInterval(
            cache, snapshot, animation, 2, 3, 0.2d, 0.3d, 1f, true, ref sampleCalls);
        NPCImportedRootDelta scaledBC = SampleInterval(
            cache, snapshot, animation, 2, 3, 0.2d, 0.3d, 1.7f, true, ref sampleCalls);

        Assert.Equal(3, sampleCalls);
        Assert.Equal(UncachedInterval(0.1d, 0.2d, 1f), cachedAB);
        Assert.Equal(UncachedInterval(0.2d, 0.3d, 1f), cachedBC);
        Assert.Equal(UncachedInterval(0.2d, 0.3d, 1.7f), scaledBC);
        Assert.Equal(cachedBC.Yaw, scaledBC.Yaw);
    }

    [Fact]
    public void Key_UsesClampedFloatBitsAndOnlySnapshotAnimationAndTrackIdentity()
    {
        var cache = new NPCExactImportedRootEndpointCache<object>(32);
        object snapshot = new();
        object otherSnapshot = new();
        object animation = new();
        object otherAnimation = new();
        double firstSourceTime = 0.5d;
        double sameFloatSourceTime = 0.5d + 1e-12d;
        float first = Clamp(firstSourceTime);
        float collapsed = Clamp(sameFloatSourceTime);
        Assert.Equal(BitConverter.SingleToInt32Bits(first), BitConverter.SingleToInt32Bits(collapsed));
        NPCImportedRootEndpoint endpoint = Endpoint(first);

        using (NPCExactImportedRootEndpointCache<object>.Scope scope = cache.BeginScope(snapshot))
        {
            cache.Store(snapshot, animation, 2, 3, first, in endpoint);
            Assert.True(cache.TryGet(snapshot, animation, 2, 3, collapsed, out _));
            Assert.False(cache.TryGet(snapshot, otherAnimation, 2, 3, first, out _));
            Assert.False(cache.TryGet(snapshot, animation, 4, 3, first, out _));
            Assert.False(cache.TryGet(snapshot, animation, 2, 5, first, out _));
            float positiveZero = 0f;
            NPCImportedRootEndpoint zeroEndpoint = Endpoint(positiveZero);
            cache.Store(snapshot, animation, 2, 3, positiveZero, in zeroEndpoint);
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            Assert.False(cache.TryGet(snapshot, animation, 2, 3, negativeZero, out _));
        }

        using NPCExactImportedRootEndpointCache<object>.Scope replacement = cache.BeginScope(otherSnapshot);
        Assert.False(cache.TryGet(otherSnapshot, animation, 2, 3, first, out _));
    }

    [Fact]
    public void WrappingScaleAndMissingRotation_PreserveEndpointAndCompositionSemantics()
    {
        var cache = new NPCExactImportedRootEndpointCache<object>(8);
        object snapshot = new();
        object animation = new();
        int sampleCalls = 0;
        using NPCExactImportedRootEndpointCache<object>.Scope scope = cache.BeginScope(snapshot);

        NPCImportedRootEndpoint zero = SampleEndpoint(
            cache, snapshot, animation, 2, -1, 0d, true, ref sampleCalls);
        NPCImportedRootEndpoint end = SampleEndpoint(
            cache, snapshot, animation, 2, -1, 1d, true, ref sampleCalls);
        NPCImportedRootEndpoint nearEnd = SampleEndpoint(
            cache, snapshot, animation, 2, -1, 0.9d, true, ref sampleCalls);
        NPCImportedRootEndpoint afterWrap = SampleEndpoint(
            cache, snapshot, animation, 2, -1, 0.1d, true, ref sampleCalls);
        NPCImportedRootDelta tail = NPCExactImportedRootEndpointSampler.Compose(in nearEnd, in end, 1f);
        NPCImportedRootDelta head = NPCExactImportedRootEndpointSampler.Compose(in zero, in afterWrap, 1f);
        NPCImportedRootDelta scaledTail = NPCExactImportedRootEndpointSampler.Compose(in nearEnd, in end, 2f);

        Assert.Equal(4, sampleCalls);
        Assert.NotEqual(zero.Position, end.Position);
        Assert.Equal(Quaternion.Identity, zero.Rotation);
        Assert.Equal(Quaternion.Identity, end.Rotation);
        Assert.Equal(tail.Root * 2f, scaledTail.Root);
        Assert.Equal(tail.Yaw, scaledTail.Yaw);
        Assert.Equal(0f, NPCNavigationRoutePlan.WrapAngle(tail.Yaw + head.Yaw), 5);
    }

    [Fact]
    public void CapacityOverflow_NestedScopesAndCleanup_KeepReuseBoundedAndFailClosed()
    {
        var cache = new NPCExactImportedRootEndpointCache<object>(1);
        object snapshot = new();
        object replacementSnapshot = new();
        object animation = new();
        int sampleCalls = 0;
        NPCExactImportedRootEndpointCache<object>.Scope outer = cache.BeginScope(snapshot);

        _ = SampleInterval(cache, snapshot, animation, 2, 3, 0.1d, 0.2d, 1f, true, ref sampleCalls);
        using (NPCExactImportedRootEndpointCache<object>.Scope nested = cache.BeginScope(snapshot))
        {
            _ = SampleInterval(cache, snapshot, animation, 2, 3, 0.2d, 0.3d, 1f, true, ref sampleCalls);
        }
        Assert.Equal(3, sampleCalls);
        _ = Assert.Throws<InvalidOperationException>(() => cache.BeginScope(replacementSnapshot));

        cache.Clear();
        outer.Dispose();
        using (NPCExactImportedRootEndpointCache<object>.Scope replacement = cache.BeginScope(replacementSnapshot))
        {
            _ = SampleEndpoint(cache, replacementSnapshot, animation, 2, 3, 0.2d, true, ref sampleCalls);
            Assert.Equal(4, sampleCalls);
        }

        using (NPCExactImportedRootEndpointCache<object>.Scope standalone = cache.BeginScope(replacementSnapshot))
        {
            Assert.False(cache.TryGet(replacementSnapshot, animation, 2, 3, 0.2f, out _));
        }

        try
        {
            using NPCExactImportedRootEndpointCache<object>.Scope failed = cache.BeginScope(replacementSnapshot);
            _ = SampleEndpoint(cache, replacementSnapshot, animation, 2, 3, 0.4d, true, ref sampleCalls);
            throw new InvalidOperationException("Expected sampling failure.");
        }
        catch (InvalidOperationException)
        {
        }

        using NPCExactImportedRootEndpointCache<object>.Scope afterFailure = cache.BeginScope(replacementSnapshot);
        Assert.False(cache.TryGet(replacementSnapshot, animation, 2, 3, 0.4f, out _));
    }

    [Fact]
    public void CachedAndUncachedProjectionResultsAndEveryTraceIntervalRemainEqualForYawSiblings()
    {
        ProjectionPair uncached = ProjectYawSiblings(useCache: false);
        ProjectionPair cached = ProjectYawSiblings(useCache: true);

        Assert.Equal(uncached.Baseline, cached.Baseline);
        Assert.Equal(uncached.BaselineTrace, cached.BaselineTrace);
        Assert.Equal(uncached.Sibling, cached.Sibling);
        Assert.Equal(uncached.SiblingTrace, cached.SiblingTrace);
        Assert.NotEmpty(cached.BaselineTrace);
        Assert.NotEmpty(cached.SiblingTrace);
        Assert.True(cached.CallsAfterBaseline > 0);
        Assert.Equal(cached.CallsAfterBaseline, cached.Calls);
        Assert.True(cached.Calls < uncached.Calls);
    }

    private static ProjectionPair ProjectYawSiblings(bool useCache)
    {
        NPCCommandedReleaseProjectionRequest baseline = CreateRequest() with
        {
            MovingYawTarget = 0.2f,
            TerminalYawTarget = 0.4f,
            RequestedYawRate = 0.6f,
        };
        NPCCommandedReleaseProjectionRequest sibling = baseline with
        {
            ExitStartYaw = -0.15f,
        };
        var cache = new NPCExactImportedRootEndpointCache<object>(32);
        object snapshot = new();
        object animation = new();
        int calls = 0;
        using NPCExactImportedRootEndpointCache<object>.Scope scope = cache.BeginScope(snapshot);

        NPCCommandedReleaseAuthoredIntervalSample Sample(
            Vector2 previousBlend,
            float previousPhase,
            Vector2 currentBlend,
            float currentPhase,
            float playbackRate)
        {
            _ = previousBlend;
            _ = currentBlend;
            _ = playbackRate;
            NPCImportedRootDelta delta = SampleInterval(
                cache,
                snapshot,
                animation,
                2,
                3,
                previousPhase,
                currentPhase,
                1f,
                useCache,
                ref calls);
            return new(delta.Root, delta.Yaw);
        }

        List<NPCCommandedReleaseProjectionInterval> baselineTrace = [];
        NPCCommandedReleaseProjectionResult baselineResult = NPCCommandedReleaseProjection.Project(
            baseline,
            Sample,
            baselineTrace,
            maximumTicks: 3);
        int callsAfterBaseline = calls;
        List<NPCCommandedReleaseProjectionInterval> siblingTrace = [];
        NPCCommandedReleaseProjectionResult siblingResult = NPCCommandedReleaseProjection.Project(
            sibling,
            Sample,
            siblingTrace,
            maximumTicks: 3);
        return new(
            baselineResult,
            [.. baselineTrace],
            siblingResult,
            [.. siblingTrace],
            callsAfterBaseline,
            calls);
    }

    private static NPCImportedRootDelta SampleInterval(
        NPCExactImportedRootEndpointCache<object> cache,
        object snapshot,
        object animation,
        int positionTrack,
        int rotationTrack,
        double previousTime,
        double currentTime,
        float motionScale,
        bool useCache,
        ref int sampleCalls)
    {
        NPCImportedRootEndpoint previous = SampleEndpoint(
            cache, snapshot, animation, positionTrack, rotationTrack, previousTime, useCache, ref sampleCalls);
        NPCImportedRootEndpoint current = SampleEndpoint(
            cache, snapshot, animation, positionTrack, rotationTrack, currentTime, useCache, ref sampleCalls);
        return NPCExactImportedRootEndpointSampler.Compose(in previous, in current, motionScale);
    }

    private static NPCImportedRootEndpoint SampleEndpoint(
        NPCExactImportedRootEndpointCache<object> cache,
        object snapshot,
        object animation,
        int positionTrack,
        int rotationTrack,
        double sourceTime,
        bool useCache,
        ref int sampleCalls)
    {
        float clamped = Clamp(sourceTime);
        if (useCache && cache.TryGet(
                snapshot,
                animation,
                positionTrack,
                rotationTrack,
                clamped,
                out NPCImportedRootEndpoint cached))
        {
            return cached;
        }

        sampleCalls++;
        NPCImportedRootEndpoint sampled = Endpoint(clamped, rotationTrack);
        if (useCache)
        {
            cache.Store(snapshot, animation, positionTrack, rotationTrack, clamped, in sampled);
        }
        return sampled;
    }

    private static NPCImportedRootDelta UncachedInterval(double previousTime, double currentTime, float motionScale)
    {
        NPCImportedRootEndpoint previous = Endpoint(Clamp(previousTime));
        NPCImportedRootEndpoint current = Endpoint(Clamp(currentTime));
        return NPCExactImportedRootEndpointSampler.Compose(in previous, in current, motionScale);
    }

    private static NPCImportedRootEndpoint Endpoint(float time, int rotationTrack = 3)
        => new(
            new Vector3(time, time * time, time * 2f),
            rotationTrack >= 0 ? new Quaternion(Vector3.Up, time * 0.5f).Normalized() : Quaternion.Identity);

    private static float Clamp(double sourceTime) => (float)Math.Clamp(sourceTime, 0d, 1d);

    private static NPCCommandedReleaseProjectionRequest CreateRequest() => new(
        new Vector2(0.2f, 0f),
        Vector2.Zero,
        new Vector2(1f, 0f),
        0.1f,
        1f,
        1f,
        0.6f,
        0f,
        0.4f,
        Delta);

    private readonly record struct ProjectionPair(
        NPCCommandedReleaseProjectionResult Baseline,
        NPCCommandedReleaseProjectionInterval[] BaselineTrace,
        NPCCommandedReleaseProjectionResult Sibling,
        NPCCommandedReleaseProjectionInterval[] SiblingTrace,
        int CallsAfterBaseline,
        int Calls);
}
