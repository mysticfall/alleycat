using Godot;

namespace AlleyCat.Navigation;

/// <summary>Value-only evidence for one terminal yaw convergence interval.</summary>
public readonly record struct NPCYawConvergenceStepTrace(
    bool IsValid,
    float CurrentYawRadians,
    float WrappedErrorRadians,
    float PreviousAppliedYawRate,
    float BrakingTargetYawRate,
    float AccelerationStep,
    float AcceleratedYawRate,
    float ProposedDeltaRadians,
    float AppliedDeltaRadians,
    bool ClampChangedDelta);

/// <summary>
/// Route-independent terminal yaw convergence guard. A commanded rotation may approach or land inside the numerical
/// tolerance, but may not carry the actor through the accepted terminal yaw to the opposite side.
/// </summary>
public static class NPCYawConvergence
{
    private const float MinimumActionableRotationRadians = 0.000001f;

    /// <summary>Maximum final terminal yaw error authorised by NAV-001 numerical convergence.</summary>
    public const float NumericalToleranceRadians = NPCAnimationProgramme.NumericalConvergenceThreshold;

    /// <summary>
    /// Returns whether the runtime executor must apply a finite rotation. This deliberately shares the convergence
    /// guard's minimum instead of Godot's broader approximate-zero predicate, so forecast and execution own the same
    /// micro-rotation decisions.
    /// </summary>
    public static bool IsActionableRotation(float rotationDeltaRadians)
        => float.IsFinite(rotationDeltaRadians)
            && Mathf.Abs(rotationDeltaRadians) > MinimumActionableRotationRadians;

    /// <summary>Applies the same pure-yaw Basis round-trip used by the runtime actor for one actionable rotation.</summary>
    public static float RealiseRuntimeYaw(float currentYawRadians, float rotationDeltaRadians)
    {
        if (!float.IsFinite(currentYawRadians) || !IsActionableRotation(rotationDeltaRadians))
        {
            return currentYawRadians;
        }

        Basis current = new(Vector3.Up, currentYawRadians);
        Basis rotated = new Basis(Vector3.Up, rotationDeltaRadians) * current;
        return NPCNavigationRoutePlan.YawFromDirection(rotated.Orthonormalized() * Vector3.Forward);
    }

    /// <summary>Bounds one rotation command so it cannot cross the terminal yaw beyond numerical tolerance.</summary>
    public static float ClampRotationDelta(
        float currentYawRadians,
        float terminalYawRadians,
        float proposedDeltaRadians)
    {
        if (!float.IsFinite(currentYawRadians)
            || !float.IsFinite(terminalYawRadians)
            || !float.IsFinite(proposedDeltaRadians)
            || Mathf.Abs(proposedDeltaRadians) <= MinimumActionableRotationRadians)
        {
            return 0f;
        }

        float currentError = NPCNavigationRoutePlan.WrapAngle(terminalYawRadians - currentYawRadians);
        float nextError = NPCNavigationRoutePlan.WrapAngle(terminalYawRadians - currentYawRadians - proposedDeltaRadians);
        bool changesSide = Mathf.Sign(currentError) != Mathf.Sign(nextError);
        bool crossesBeyondTolerance = changesSide && Mathf.Abs(nextError) > NumericalToleranceRadians;
        if (!crossesBeyondTolerance)
        {
            return proposedDeltaRadians;
        }

        float side = Mathf.Sign(currentError) != 0f ? Mathf.Sign(currentError) : -Mathf.Sign(nextError);
        float convergenceTolerance = Math.Max(
            NumericalToleranceRadians - MinimumActionableRotationRadians,
            0f);
        float boundaryYaw = terminalYawRadians - (side * convergenceTolerance);
        float boundaryDelta = NPCNavigationRoutePlan.WrapAngle(boundaryYaw - currentYawRadians);
        return Mathf.Sign(boundaryDelta) == Mathf.Sign(proposedDeltaRadians)
            && Mathf.Abs(boundaryDelta) <= Mathf.Abs(proposedDeltaRadians)
            ? boundaryDelta
            : 0f;
    }

    /// <summary>
    /// Exact terminal-approach yaw step shared by production and value-only feasibility simulation. The applied total
    /// yaw rate accelerates toward the braking-rate target, then the existing no-crossing guard bounds the interval.
    /// </summary>
    public static float StepTerminalRotation(
        float currentYawRadians,
        float terminalYawRadians,
        ref float previousAppliedYawRate,
        float deltaSeconds)
        => StepTerminalRotation(
            currentYawRadians,
            terminalYawRadians,
            ref previousAppliedYawRate,
            deltaSeconds,
            out _);

    /// <summary>Steps terminal rotation and returns allocation-free diagnostic evidence for the interval.</summary>
    public static float StepTerminalRotation(
        float currentYawRadians,
        float terminalYawRadians,
        ref float previousAppliedYawRate,
        float deltaSeconds,
        out NPCYawConvergenceStepTrace trace)
    {
        float previousRate = previousAppliedYawRate;
        if (!float.IsFinite(currentYawRadians)
            || !float.IsFinite(terminalYawRadians)
            || !float.IsFinite(previousAppliedYawRate)
            || !float.IsFinite(deltaSeconds)
            || deltaSeconds <= 0f)
        {
            previousAppliedYawRate = 0f;
            trace = default;
            return 0f;
        }

        float error = NPCNavigationRoutePlan.WrapAngle(terminalYawRadians - currentYawRadians);
        // Brake one minimum actionable rotation inside the immutable acceptance boundary. Landing on the outer
        // boundary is not stable across the Basis/direction float round-trip used by the runtime yaw measurement.
        float convergenceTolerance = Math.Max(
            NumericalToleranceRadians - MinimumActionableRotationRadians,
            0f);
        float remaining = Math.Max(Mathf.Abs(error) - convergenceTolerance, 0f);
        float targetRate = Mathf.Sign(error) * Mathf.Sqrt(
            2f * NPCTransitionContinuity.MaximumYawAcceleration * remaining);
        float accelerationStep = NPCTransitionContinuity.MaximumYawAcceleration * deltaSeconds;
        previousAppliedYawRate = Mathf.MoveToward(
            previousAppliedYawRate,
            targetRate,
            accelerationStep);
        float acceleratedRate = previousAppliedYawRate;
        float proposed = previousAppliedYawRate * deltaSeconds;
        float applied = ClampRotationDelta(currentYawRadians, terminalYawRadians, proposed);
        previousAppliedYawRate = applied / deltaSeconds;
        trace = new(
            true,
            currentYawRadians,
            error,
            previousRate,
            targetRate,
            accelerationStep,
            acceleratedRate,
            proposed,
            applied,
            applied != proposed);
        return applied;
    }
}
