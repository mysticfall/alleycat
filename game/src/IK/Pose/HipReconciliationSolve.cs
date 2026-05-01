using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Desired hip-reconciliation inputs for a single tick before pose-state limit application.
/// </summary>
public sealed record HipReconciliationProfileResult
{
    /// <summary>
    /// Gets the desired absolute hip position in skeleton-local space before pose-state limiting.
    /// </summary>
    public Vector3 DesiredHipLocalPosition { get; init; } = Vector3.Zero;

    /// <summary>
    /// Gets the optional data required to derive a limited head target from the applied hip offset.
    /// </summary>
    public HeadTargetLimitSolve? HeadTargetLimit
    {
        get; init;
    }
}

/// <summary>
/// Resolved per-tick outputs after state-defined reference-relative limit application.
/// </summary>
public sealed record HipReconciliationTickResult
{
    /// <summary>
    /// Gets the clamped absolute hip target in skeleton-local space for this tick.
    /// </summary>
    public Vector3 AppliedHipLocalPosition { get; init; } = Vector3.Zero;

    /// <summary>
    /// Gets the requested hip offset from the animated baseline before clamping.
    /// </summary>
    public Vector3 DesiredFinalHipOffset { get; init; } = Vector3.Zero;

    /// <summary>
    /// Gets the applied hip offset from the animated baseline after clamping.
    /// </summary>
    public Vector3 AppliedFinalHipOffset { get; init; } = Vector3.Zero;

    /// <summary>
    /// Gets the limited head target transform to use before downstream IK, when available.
    /// </summary>
    public Transform3D? LimitedHeadTargetTransform
    {
        get; init;
    }

    /// <summary>
    /// Gets the remaining desired-vs-applied hip offset mismatch after clamping.
    /// </summary>
    public Vector3 ResidualFinalHipOffset => DesiredFinalHipOffset - AppliedFinalHipOffset;
}

/// <summary>
/// Data required to derive a limited head target that matches a clamped hip solve.
/// </summary>
public sealed record HeadTargetLimitSolve
{
    private const float GainEpsilon = 1e-4f;

    /// <summary>
    /// Gets the hip rest position in skeleton-local space.
    /// </summary>
    public Vector3 HipRestLocalPosition { get; init; } = Vector3.Zero;

    /// <summary>
    /// Gets the head target rest transform in skeleton-local space.
    /// </summary>
    public Transform3D RestHeadLocalTransform { get; init; } = Transform3D.Identity;

    /// <summary>
    /// Gets the current unclamped head target transform in skeleton-local space.
    /// </summary>
    public Transform3D CurrentHeadLocalTransform { get; init; } = Transform3D.Identity;

    /// <summary>
    /// Gets the hip rest up axis in skeleton-local space.
    /// </summary>
    public Vector3 HipRestUpLocal { get; init; } = Vector3.Up;

    /// <summary>
    /// Gets the hip rest forward axis in skeleton-local space.
    /// </summary>
    public Vector3 HipRestForwardLocal { get; init; } = Vector3.Forward;

    /// <summary>
    /// Gets the hip rest lateral axis in skeleton-local space.
    /// </summary>
    public Vector3 HipRestLateralLocal { get; init; } = Vector3.Right;

    /// <summary>
    /// Original positional head offset components in the order lateral, vertical, forward.
    /// </summary>
    public Vector3 OriginalHeadOffsetComponents { get; init; } = Vector3.Zero;

    /// <summary>
    /// Effective positional gains in the order lateral, vertical, forward.
    /// </summary>
    public Vector3 EffectivePositionalGains { get; init; } = Vector3.One;

    /// <summary>
    /// Gets the weighted rotation-compensation contribution preserved during head-target limiting.
    /// </summary>
    public Vector3 WeightedRotationCompensationLocal { get; init; } = Vector3.Zero;

    /// <summary>
    /// Creates the limited global head target transform that matches <paramref name="appliedHipLocalPosition"/>.
    /// </summary>
    public Transform3D CreateLimitedHeadTargetTransform(
        Vector3 appliedHipLocalPosition,
        Transform3D skeletonGlobalTransform)
    {
        Vector3 appliedHipOffsetFromRestLocal = appliedHipLocalPosition - HipRestLocalPosition;
        Vector3 appliedWeightedPositionalOffsetLocal = appliedHipOffsetFromRestLocal + WeightedRotationCompensationLocal;

        float limitedLateral = ResolveLimitedComponent(
            appliedWeightedPositionalOffsetLocal.Dot(HipRestLateralLocal),
            EffectivePositionalGains.X,
            OriginalHeadOffsetComponents.X);
        float limitedVertical = ResolveLimitedComponent(
            appliedWeightedPositionalOffsetLocal.Dot(HipRestUpLocal),
            EffectivePositionalGains.Y,
            OriginalHeadOffsetComponents.Y);
        float limitedForward = ResolveLimitedComponent(
            appliedWeightedPositionalOffsetLocal.Dot(HipRestForwardLocal),
            EffectivePositionalGains.Z,
            OriginalHeadOffsetComponents.Z);

        Vector3 limitedHeadOffsetLocal = (HipRestLateralLocal * limitedLateral)
            + (HipRestUpLocal * limitedVertical)
            + (HipRestForwardLocal * limitedForward);

        Transform3D limitedHeadLocalTransform = new(
            CurrentHeadLocalTransform.Basis,
            RestHeadLocalTransform.Origin + limitedHeadOffsetLocal);

        return skeletonGlobalTransform * limitedHeadLocalTransform;
    }

    private static float ResolveLimitedComponent(float weightedComponent, float effectiveGain, float fallback)
        => Mathf.Abs(effectiveGain) <= GainEpsilon ? fallback : weightedComponent / effectiveGain;
}
