using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>
/// Settlement-only planar convergence profile and guard. Resolution has no magnitude bound:
/// the tapered target starts brisk and decelerates smoothly into the numerical tolerance, while the guard clears
/// stale smoothing once the actor is numerically at the endpoint and keeps each requested correction on the
/// endpoint-facing side of the current planar error.
/// </summary>
internal static class NPCSettlementPlanarConvergence
{
    /// <summary>
    /// Brisk settlement ceiling in metres per second. A residual of any magnitude resolves; only the approach speed
    /// is bounded so the convergence stays continuous at the 60 Hz physics cadence.
    /// </summary>
    public const float MaximumRate = 0.7f;

    /// <summary>
    /// Deceleration of the taper in metres per second squared. It matches the acceleration limit applied by
    /// <see cref="CalculateRequestedDelta" />, so the smoothed velocity tracks the braking curve
    /// v = sqrt(2·a·remaining) down to a smooth stop inside the tolerance.
    /// </summary>
    public const float TaperAcceleration = 2f;

    private const float NumericalToleranceSquared = NPCAnimationProgramme.NumericalConvergenceThreshold
        * NPCAnimationProgramme.NumericalConvergenceThreshold;

    /// <summary>
    /// Resolves the tapered settlement target velocity for one frame: brisk at large residuals, decelerating along a
    /// constant-deceleration braking curve into the tolerance, and never magnitude-capped below the ceiling.
    /// </summary>
    internal static Vector2 ResolveTaperedVelocity(Vector2 endpointError, float maximumRate)
    {
        if (!endpointError.IsFinite()
            || endpointError.LengthSquared() <= NumericalToleranceSquared
            || !float.IsFinite(maximumRate)
            || maximumRate <= 0f)
        {
            return Vector2.Zero;
        }

        float remaining = endpointError.Length();
        float brakingSpeed = Mathf.Sqrt(2f * TaperAcceleration * remaining);
        return endpointError / remaining * Mathf.Min(brakingSpeed, maximumRate);
    }

    internal static Vector2 CalculateRequestedDelta(
        Vector2 endpointError,
        Vector2 targetVelocity,
        ref Vector2 smoothedVelocity,
        ref Vector2 acceleration,
        float delta)
    {
        if (!endpointError.IsFinite()
            || endpointError.LengthSquared() <= NumericalToleranceSquared
            || !float.IsFinite(delta)
            || delta <= 0f)
        {
            ClearSmoothing(ref smoothedVelocity, ref acceleration);
            return Vector2.Zero;
        }

        smoothedVelocity = NPCTransitionContinuity.StepVelocityCommand(
            smoothedVelocity,
            targetVelocity,
            ref acceleration,
            delta,
            2f,
            30f);
        return ClampRequestedDelta(endpointError, smoothedVelocity * delta);
    }

    internal static bool IsWithinNumericalTolerance(Vector2 endpointError)
        => endpointError.IsFinite() && endpointError.LengthSquared() <= NumericalToleranceSquared;

    internal static void ClearSmoothing(ref Vector2 smoothedVelocity, ref Vector2 acceleration)
    {
        smoothedVelocity = Vector2.Zero;
        acceleration = Vector2.Zero;
    }

    internal static Vector2 ClampRequestedDelta(Vector2 endpointError, Vector2 requestedDelta)
    {
        if (!endpointError.IsFinite() || !requestedDelta.IsFinite())
        {
            return Vector2.Zero;
        }

        float remainingSquared = endpointError.LengthSquared();
        if (!float.IsFinite(remainingSquared) || remainingSquared <= NumericalToleranceSquared)
        {
            return Vector2.Zero;
        }

        float projection = requestedDelta.Dot(endpointError) / remainingSquared;
        return !float.IsFinite(projection) || projection <= 0f
            ? Vector2.Zero
            : projection >= 1f ? endpointError : endpointError * projection;
    }
}
