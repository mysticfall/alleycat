namespace AlleyCat.Navigation;

/// <summary>
/// Identifies the request, route, and gait observation basis of a far-route estimate.
/// </summary>
public readonly record struct NPCFarRouteEstimateKey(
    NavigationRequestIdentity RequestIdentity,
    long RouteRevision,
    long SustainedClockGeneration,
    long GaitSnapshotGeneration)
{
    internal bool IsValid => RequestIdentity.RequestID > 0
        && RequestIdentity.Generation > 0
        && RouteRevision > 0
        && SustainedClockGeneration > 0
        && GaitSnapshotGeneration > 0;
}

/// <summary>
/// Fixed-size cached movement basis used to initialise a far-route estimate.
/// </summary>
public readonly record struct NPCFarRouteEstimateBasis(
    float FullCycleLongitudinalDisplacement,
    float FullCycleDurationSeconds,
    float OrdinaryPlaybackRate,
    float CrossfadeTravelDistance,
    float ExitTravelDistance,
    float UncertaintyReserveDistance,
    float FinalToleranceAllowanceDistance);

/// <summary>
/// Collision-realised route sample used to anchor or recalibrate an estimate.
/// </summary>
public readonly record struct NPCFarRouteProgressSample(
    float RemainingRouteDistance,
    float RealisedRouteProgress,
    double UnwrappedPhase);

/// <summary>
/// Deterministic recalibration trigger and collision-realised progress sample.
/// </summary>
public readonly record struct NPCFarRouteRecalibration(
    float RealisedRouteProgress,
    double UnwrappedPhase,
    bool IsGaitBoundary,
    float MaterialDriftDistance);

/// <summary>
/// Constant-time estimate of arrival at the bounded stopping region.
/// </summary>
public readonly record struct NPCFarRouteStoppingEstimate(
    float EstimatedRemainingRouteDistance,
    float StoppingRegionDistance,
    float EstimatedCyclesToStoppingRegion,
    float EstimatedSecondsToStoppingRegion)
{
    /// <summary>Gets whether the estimated remaining distance has reached the bounded stopping region.</summary>
    public bool IsInStoppingRegion => EstimatedRemainingRouteDistance <= StoppingRegionDistance;
}

/// <summary>
/// Fixed-size, route-scoped estimator for cheap far-path stopping decisions.
/// It only changes estimate state; it has no actor or animation dependencies.
/// </summary>
public struct NPCFarRouteStoppingEstimator
{
    private const float MinimumProgress = 0.000001f;

    private NPCFarRouteEstimateKey _key;
    private float _anchorRemainingDistance;
    private float _anchorRealisedProgress;
    private double _anchorUnwrappedPhase;
    private float _cycleDisplacement;
    private float _cycleDurationSeconds;
    private float _playbackRate;
    private float _stoppingRegionDistance;

    /// <summary>Gets whether this instance has a valid, queryable estimate basis.</summary>
    public bool IsAvailable
    {
        readonly get;
        private set;
    }

    /// <summary>
    /// Establishes a fresh estimate basis at a collision-realised route sample.
    /// </summary>
    public bool TryInitialise(
        in NPCFarRouteEstimateKey key,
        in NPCFarRouteEstimateBasis basis,
        in NPCFarRouteProgressSample sample)
    {
        Reset();
        if (!key.IsValid || !IsValid(basis) || !IsValid(sample))
        {
            return false;
        }

        float stoppingRegionDistance = basis.FullCycleLongitudinalDisplacement
            + basis.CrossfadeTravelDistance
            + basis.ExitTravelDistance
            + basis.UncertaintyReserveDistance
            + basis.FinalToleranceAllowanceDistance;
        if (!float.IsFinite(stoppingRegionDistance))
        {
            return false;
        }

        _key = key;
        _anchorRemainingDistance = sample.RemainingRouteDistance;
        _anchorRealisedProgress = sample.RealisedRouteProgress;
        _anchorUnwrappedPhase = sample.UnwrappedPhase;
        _cycleDisplacement = basis.FullCycleLongitudinalDisplacement;
        _cycleDurationSeconds = basis.FullCycleDurationSeconds;
        _playbackRate = basis.OrdinaryPlaybackRate;
        _stoppingRegionDistance = stoppingRegionDistance;
        IsAvailable = true;
        return true;
    }

    /// <summary>
    /// Estimates remaining distance and time using only scalar cached state.
    /// A stale or invalid observation invalidates this estimator and fails closed.
    /// </summary>
    public bool TryEstimate(
        in NPCFarRouteEstimateKey key,
        double currentUnwrappedPhase,
        out NPCFarRouteStoppingEstimate estimate)
    {
        estimate = default;
        if (!HasMatchingKey(key)
            || !double.IsFinite(currentUnwrappedPhase)
            || currentUnwrappedPhase < _anchorUnwrappedPhase)
        {
            Reset();
            return false;
        }

        double phaseAdvance = currentUnwrappedPhase - _anchorUnwrappedPhase;
        double predictedAdvance = phaseAdvance * _cycleDisplacement;
        if (!double.IsFinite(predictedAdvance))
        {
            Reset();
            return false;
        }

        float remaining = (float)Math.Max(_anchorRemainingDistance - predictedAdvance, 0d);
        float cycles = Math.Max((remaining - _stoppingRegionDistance) / _cycleDisplacement, 0f);
        float seconds = cycles * _cycleDurationSeconds / _playbackRate;
        if (!float.IsFinite(remaining) || !float.IsFinite(cycles) || !float.IsFinite(seconds))
        {
            Reset();
            return false;
        }

        estimate = new(remaining, _stoppingRegionDistance, cycles, seconds);
        return true;
    }

    /// <summary>
    /// Replaces accumulated phase-estimate error at a gait boundary or after material drift.
    /// </summary>
    public bool TryRecalibrate(
        in NPCFarRouteEstimateKey key,
        in NPCFarRouteRecalibration recalibration)
    {
        if (!HasMatchingKey(key) || !IsValid(recalibration))
        {
            Reset();
            return false;
        }

        double phaseAdvance = recalibration.UnwrappedPhase - _anchorUnwrappedPhase;
        float realisedAdvance = recalibration.RealisedRouteProgress - _anchorRealisedProgress;
        if (phaseAdvance < 0d || realisedAdvance < 0f)
        {
            Reset();
            return false;
        }

        double expectedAdvance = phaseAdvance * _cycleDisplacement;
        double drift = Math.Abs(realisedAdvance - expectedAdvance);
        bool shouldRecalibrate = recalibration.IsGaitBoundary
            || drift >= recalibration.MaterialDriftDistance;
        if (!shouldRecalibrate)
        {
            return false;
        }

        if (phaseAdvance <= MinimumProgress || realisedAdvance <= MinimumProgress)
        {
            Reset();
            return false;
        }

        double observedCycleDisplacement = realisedAdvance / phaseAdvance;
        float remaining = Math.Max(_anchorRemainingDistance - realisedAdvance, 0f);
        if (!double.IsFinite(observedCycleDisplacement)
            || observedCycleDisplacement <= MinimumProgress
            || observedCycleDisplacement > float.MaxValue
            || !float.IsFinite(remaining))
        {
            Reset();
            return false;
        }

        // Preserve the non-lead-in part of the threshold while replacing the calibrated cycle demand.
        _stoppingRegionDistance += (float)observedCycleDisplacement - _cycleDisplacement;
        _cycleDisplacement = (float)observedCycleDisplacement;
        _anchorRemainingDistance = remaining;
        _anchorRealisedProgress = recalibration.RealisedRouteProgress;
        _anchorUnwrappedPhase = recalibration.UnwrappedPhase;
        if (!float.IsFinite(_stoppingRegionDistance) || _stoppingRegionDistance < 0f)
        {
            Reset();
            return false;
        }

        return true;
    }

    /// <summary>Clears all cached estimate identity and scalar state.</summary>
    public void Reset() => this = default;

    private readonly bool HasMatchingKey(in NPCFarRouteEstimateKey key)
        => IsAvailable && key.IsValid && key == _key;

    private static bool IsValid(in NPCFarRouteEstimateBasis basis)
        => float.IsFinite(basis.FullCycleLongitudinalDisplacement)
        && basis.FullCycleLongitudinalDisplacement > MinimumProgress
        && float.IsFinite(basis.FullCycleDurationSeconds)
        && basis.FullCycleDurationSeconds > MinimumProgress
        && float.IsFinite(basis.OrdinaryPlaybackRate)
        && basis.OrdinaryPlaybackRate > MinimumProgress
        && IsNonNegativeFinite(basis.CrossfadeTravelDistance)
        && IsNonNegativeFinite(basis.ExitTravelDistance)
        && IsNonNegativeFinite(basis.UncertaintyReserveDistance)
        && IsNonNegativeFinite(basis.FinalToleranceAllowanceDistance);

    private static bool IsValid(in NPCFarRouteProgressSample sample)
        => IsNonNegativeFinite(sample.RemainingRouteDistance)
        && IsNonNegativeFinite(sample.RealisedRouteProgress)
        && double.IsFinite(sample.UnwrappedPhase)
        && sample.UnwrappedPhase >= 0d;

    private static bool IsValid(in NPCFarRouteRecalibration recalibration)
        => IsNonNegativeFinite(recalibration.RealisedRouteProgress)
        && double.IsFinite(recalibration.UnwrappedPhase)
        && recalibration.UnwrappedPhase >= 0d
        && IsNonNegativeFinite(recalibration.MaterialDriftDistance);

    private static bool IsNonNegativeFinite(float value) => float.IsFinite(value) && value >= 0f;
}
