using AlleyCat.Navigation;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>Deterministic coverage for route-scoped far-path stopping estimates.</summary>
public sealed class NPCFarRouteStoppingEstimatorTests
{
    private static readonly NPCFarRouteEstimateKey _key = new(new(11, 3), 7, 13, 17);
    private static readonly NPCFarRouteEstimateBasis _basis = new(2f, 1.2f, 1.5f, 0.4f, 0.8f, 0.2f, 0.1f);

    /// <inheritdoc/>
    [Fact]
    public void FullCycleDisplacementAndRateDriveExpectedEstimate()
    {
        NPCFarRouteStoppingEstimator estimator = Initialise(20f, phase: 4d);

        Assert.True(estimator.TryEstimate(_key, 6d, out NPCFarRouteStoppingEstimate estimate));

        Assert.Equal(16f, estimate.EstimatedRemainingRouteDistance, 5);
        Assert.Equal(6.25f, estimate.EstimatedCyclesToStoppingRegion, 5);
        Assert.Equal(5f, estimate.EstimatedSecondsToStoppingRegion, 5);
        Assert.False(estimate.IsInStoppingRegion);
    }

    /// <inheritdoc/>
    [Theory]
    [InlineData(30f)]
    [InlineData(30_000f)]
    public void StoppingRegionUsesOnlyBoundedTravelAndReserve(float routeLength)
    {
        NPCFarRouteStoppingEstimator estimator = Initialise(routeLength);

        Assert.True(estimator.TryEstimate(_key, 0d, out NPCFarRouteStoppingEstimate estimate));

        Assert.Equal(3.5f, estimate.StoppingRegionDistance, 5);
    }

    /// <inheritdoc/>
    [Fact]
    public void RecalibrationReplacesDriftFromCollisionRealisedProgress()
    {
        NPCFarRouteStoppingEstimator estimator = Initialise(20f, progress: 5f);
        Assert.True(estimator.TryEstimate(_key, 1d, out NPCFarRouteStoppingEstimate before));

        Assert.True(estimator.TryRecalibrate(_key, new(6f, 1d, true, 100f)));
        Assert.True(estimator.TryEstimate(_key, 2d, out NPCFarRouteStoppingEstimate after));

        Assert.Equal(18f, before.EstimatedRemainingRouteDistance, 5);
        Assert.Equal(18f, after.EstimatedRemainingRouteDistance, 5);
        Assert.Equal(2.5f, after.StoppingRegionDistance, 5);
        Assert.Equal(15.5f, after.EstimatedCyclesToStoppingRegion, 5);
        // The estimator accepts only scalar observations and exposes no actor transform or movement operation.
        Assert.DoesNotContain(
            typeof(NPCFarRouteStoppingEstimator).GetMethods(),
            method => method.Name.Contains("Move", StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    [Fact]
    public void MaterialDriftTriggersRecalibrationAwayFromBoundary()
    {
        NPCFarRouteStoppingEstimator estimator = Initialise(20f);

        Assert.True(estimator.TryRecalibrate(_key, new(1f, 1d, false, 0.5f)));
        Assert.True(estimator.TryEstimate(_key, 2d, out NPCFarRouteStoppingEstimate estimate));

        Assert.Equal(18f, estimate.EstimatedRemainingRouteDistance, 5);
    }

    /// <inheritdoc/>
    [Fact]
    public void RequestRouteClockAndSnapshotChangesInvalidateStaleState()
    {
        NPCFarRouteEstimateKey[] staleKeys =
        [
            _key with { RequestIdentity = new(12, 3) },
            _key with { RequestIdentity = new(11, 4) },
            _key with { RouteRevision = 8 },
            _key with { SustainedClockGeneration = 14 },
            _key with { GaitSnapshotGeneration = 18 },
        ];

        foreach (NPCFarRouteEstimateKey staleKey in staleKeys)
        {
            NPCFarRouteStoppingEstimator estimator = Initialise(20f);
            Assert.False(estimator.TryEstimate(staleKey, 1d, out NPCFarRouteStoppingEstimate estimate));
            Assert.False(estimator.IsAvailable);
            Assert.Equal(default, estimate);
        }
    }

    /// <inheritdoc/>
    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(-1f, 1f, 1f)]
    [InlineData(float.NaN, 1f, 1f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(1f, 1f, 0f)]
    public void InvalidOrNearZeroMovementBasisFailsClosed(float displacement, float duration, float rate)
    {
        var estimator = new NPCFarRouteStoppingEstimator();
        NPCFarRouteEstimateBasis basis = _basis with
        {
            FullCycleLongitudinalDisplacement = displacement,
            FullCycleDurationSeconds = duration,
            OrdinaryPlaybackRate = rate,
        };

        Assert.False(estimator.TryInitialise(_key, basis, new(10f, 0f, 0d)));
        Assert.False(estimator.TryEstimate(_key, 1d, out NPCFarRouteStoppingEstimate estimate));
        AssertFinite(estimate);
    }

    /// <inheritdoc/>
    [Fact]
    public void InvalidPhaseAndZeroRealisedRecalibrationFailClosedWithoutNonFiniteOutput()
    {
        NPCFarRouteStoppingEstimator invalidPhase = Initialise(20f);
        Assert.False(invalidPhase.TryEstimate(_key, double.NaN, out NPCFarRouteStoppingEstimate invalid));
        AssertFinite(invalid);

        var negativePhase = new NPCFarRouteStoppingEstimator();
        Assert.False(negativePhase.TryInitialise(_key, _basis, new(20f, 0f, -1d)));

        NPCFarRouteStoppingEstimator noProgress = Initialise(20f);
        Assert.False(noProgress.TryRecalibrate(_key, new(0f, 1d, true, 0f)));
        Assert.False(noProgress.TryEstimate(_key, 2d, out NPCFarRouteStoppingEstimate unavailable));
        AssertFinite(unavailable);
    }

    /// <inheritdoc/>
    [Fact]
    public void EquivalentInputsProduceBitwiseEquivalentResults()
    {
        NPCFarRouteStoppingEstimator first = Initialise(123.5f, 8.25f, 3.75d);
        NPCFarRouteStoppingEstimator second = Initialise(123.5f, 8.25f, 3.75d);

        Assert.True(first.TryEstimate(_key, 14.125d, out NPCFarRouteStoppingEstimate a));
        Assert.True(second.TryEstimate(_key, 14.125d, out NPCFarRouteStoppingEstimate b));

        Assert.Equal(BitConverter.SingleToInt32Bits(a.EstimatedRemainingRouteDistance), BitConverter.SingleToInt32Bits(b.EstimatedRemainingRouteDistance));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.StoppingRegionDistance), BitConverter.SingleToInt32Bits(b.StoppingRegionDistance));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.EstimatedCyclesToStoppingRegion), BitConverter.SingleToInt32Bits(b.EstimatedCyclesToStoppingRegion));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.EstimatedSecondsToStoppingRegion), BitConverter.SingleToInt32Bits(b.EstimatedSecondsToStoppingRegion));
    }

    /// <inheritdoc/>
    [Fact]
    public void RouteLengthChangesNeitherEstimatorCallCountNorInputShape()
    {
        int shortRouteCalls = EstimateCallsForRoute(2);
        int longRouteCalls = EstimateCallsForRoute(2_000_000);

        Assert.Equal(1, shortRouteCalls);
        Assert.Equal(shortRouteCalls, longRouteCalls);
        Assert.All(
            typeof(NPCFarRouteStoppingEstimator).GetMethods(),
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType.IsArray
                    || typeof(System.Collections.IEnumerable).IsAssignableFrom(parameter.ParameterType)));
    }

    /// <inheritdoc/>
    [Fact]
    public void WarmedUpdateAndQueryAllocateNoManagedMemory()
    {
        NPCFarRouteStoppingEstimator estimator = Initialise(100f);
        Assert.True(estimator.TryEstimate(_key, 0.5d, out _));
        Assert.True(estimator.TryRecalibrate(_key, new(1f, 0.5d, true, 0f)));
        Assert.True(estimator.TryEstimate(_key, 0.75d, out _));

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool recalibrated = estimator.TryRecalibrate(_key, new(1.5f, 0.75d, true, 0f));
        bool estimated = estimator.TryEstimate(_key, 1d, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(recalibrated);
        Assert.True(estimated);
        Assert.Equal(0, allocated);
    }

    private static NPCFarRouteStoppingEstimator Initialise(
        float remaining,
        float progress = 0f,
        double phase = 0d)
    {
        var estimator = new NPCFarRouteStoppingEstimator();
        Assert.True(estimator.TryInitialise(_key, _basis, new(remaining, progress, phase)));
        return estimator;
    }

    private static int EstimateCallsForRoute(int routePointCount)
    {
        NPCFarRouteStoppingEstimator estimator = Initialise(routePointCount);
        int calls = 0;
        calls++;
        Assert.True(estimator.TryEstimate(_key, 0.5d, out _));
        return calls;
    }

    private static void AssertFinite(in NPCFarRouteStoppingEstimate estimate)
    {
        Assert.True(float.IsFinite(estimate.EstimatedRemainingRouteDistance));
        Assert.True(float.IsFinite(estimate.StoppingRegionDistance));
        Assert.True(float.IsFinite(estimate.EstimatedCyclesToStoppingRegion));
        Assert.True(float.IsFinite(estimate.EstimatedSecondsToStoppingRegion));
    }
}
