using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Hip reconciliation profile that axis-weights the head-target positional offset onto the hip
/// bone in the hip rest basis, producing an absolute skeleton-local hip position that preserves
/// strong vertical motion while damping lateral and forward/back travel, and compensating neck
/// bend induced by head-target rotation.
/// </summary>
/// <remarks>
/// <para>
/// The profile computes, in skeleton-local space:
/// </para>
/// <list type="number">
///   <item><description><c>hipLocalRest = skeleton.GetBoneRest(hipBoneIndex).Origin</c>.</description></item>
///   <item><description>
///     The current and rest head target transforms in skeleton-local space from
///     <see cref="PoseStateContext.HeadTargetTransform"/> and
///     <see cref="PoseStateContext.HeadTargetRestTransform"/> respectively, by multiplying with
///     <c>skeleton.GlobalTransform.AffineInverse()</c>.
///   </description></item>
///   <item><description>
///     <c>headOffsetLocal = currentHeadLocal - restHeadLocal</c>, then project it into the hip
///     bone's rest-pose basis, apply independent per-axis weights, and transform it back into
///     skeleton-local space.
///   </description></item>
///   <item><description>
///     A rotational displacement by rotating the rest neck→head vector with the head rotation
///     offset from rest to current, then subtracting the original rest vector.
///   </description></item>
///   <item><description>
///     <c>targetHipLocal = hipLocalRest + weightedHeadOffsetLocal - rotationDisplacementLocal</c>.
///   </description></item>
/// </list>
/// <para>
/// This expresses the standing/crouching head-tracking heuristic: the hip translates with the
/// head-target offset, but preserves full travel for movement along the hip rest up/down axis
/// while damping side-to-side and forward/back lean. It additionally applies the opposite of the
/// rotation-implied neck displacement so side tilt and chin up/down do not over-bend the neck.
/// No Y stripping is applied — vertical ownership now lives with this profile and the
/// <c>TimeSeek</c>-driven crouch clip handles feet animation independently.
/// </para>
/// <para>
/// The profile <em>must not</em> read the currently animated hip bone pose: the animation
/// sample itself is being scrubbed by <c>TimeSeek</c> each tick, so mixing that value into the
/// hip target would create a feedback loop (hip pose → hip profile output → hip pose) and
/// manifest as flicker during crouch descent. All math is therefore derived from the
/// <em>rest</em> hip pose plus the head-derived offset.
/// </para>
/// <para>
/// An epsilon-based jitter suppression snaps the combined positional + rotational correction to
/// zero when its squared length falls inside the threshold, so the hip returns cleanly to
/// <c>hipLocalRest</c> and does not wobble on residual calibration noise.
/// </para>
/// </remarks>
[GlobalClass]
public partial class HeadTrackingHipProfile : HipReconciliationProfile
{
    private const float DefaultRotationCompensationWeight = 1.25f;
    private const float DefaultVerticalPositionWeight = 1.0f;
    private const float DefaultLateralPositionWeight = 0.5f;
    private const float DefaultForwardPositionWeight = 0.1f;
    private const float PositionEpsilon = 1e-4f;
    private const float PositionEpsilonSquared = PositionEpsilon * PositionEpsilon;
    private const float VectorEpsilonSquared = 1e-8f;

    /// <summary>
    /// Scales the rotation-derived neck compensation before applying it in the opposite direction
    /// to the hip target.
    /// </summary>
    [Export(PropertyHint.Range, "0,3,0.01")]
    public float RotationCompensationWeight { get; set; } = DefaultRotationCompensationWeight;

    /// <summary>
    /// Positional weighting applied to movement along the hip rest up/down axis.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float VerticalPositionWeight { get; set; } = DefaultVerticalPositionWeight;

    /// <summary>
    /// Positional weighting applied to movement along the hip rest left/right axis.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float LateralPositionWeight { get; set; } = DefaultLateralPositionWeight;

    /// <summary>
    /// Positional weighting applied to movement along the hip rest forward/back axis.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ForwardPositionWeight { get; set; } = DefaultForwardPositionWeight;

    /// <inheritdoc />
    public override Vector3? ComputeHipLocalPosition(PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Skeleton3D? skeleton = context.Skeleton;
        if (skeleton is null || context.HipBoneIndex < 0)
        {
            return null;
        }

        Vector3 hipLocalRest = skeleton.GetBoneRest(context.HipBoneIndex).Origin;
        Basis hipRestBasisLocal = skeleton.GetBoneGlobalRest(context.HipBoneIndex).Basis.Orthonormalized();
        Transform3D skeletonInverse = skeleton.GlobalTransform.AffineInverse();

        Transform3D restHeadLocalTransform = skeletonInverse * context.HeadTargetRestTransform;
        Transform3D currentHeadLocalTransform = skeletonInverse * context.HeadTargetTransform;

        Vector3 headRotationDisplacementLocal =
            TryComputeHeadRotationDisplacementLocal(
                context,
                skeleton,
                restHeadLocalTransform,
                currentHeadLocalTransform,
                out Vector3 displacement)
                ? displacement
                : Vector3.Zero;

        return ComputeHipLocalPosition(
            hipLocalRest,
            hipRestBasisLocal,
            restHeadLocalTransform.Origin,
            currentHeadLocalTransform.Origin,
            headRotationDisplacementLocal,
            RotationCompensationWeight,
            VerticalPositionWeight,
            LateralPositionWeight,
            ForwardPositionWeight);
    }

    /// <summary>
    /// Pure helper that computes the absolute hip-local target position from the rest hip
    /// position and the rest and current head-target positions, all expressed in skeleton-local
    /// space.
    /// </summary>
    /// <remarks>
    /// Exposed as a <see langword="public"/> static helper so the profile's math can be covered
    /// by unit tests without instantiating a Godot <see cref="Resource"/> subclass or a live
    /// <see cref="Skeleton3D"/>.
    /// </remarks>
    /// <param name="hipLocalRest">Rest hip bone position in skeleton-local space.</param>
    /// <param name="restHeadLocal">Rest head viewpoint position in skeleton-local space.</param>
    /// <param name="currentHeadLocal">Current head viewpoint position in skeleton-local space.</param>
    /// <returns>
    /// <paramref name="hipLocalRest"/> when the head offset is within the epsilon band;
    /// otherwise <c>hipLocalRest + (currentHeadLocal - restHeadLocal)</c>.
    /// </returns>
    public static Vector3 ComputeHipLocalPosition(
        Vector3 hipLocalRest,
        Vector3 restHeadLocal,
        Vector3 currentHeadLocal) =>
        ComputeHipLocalPosition(
            hipLocalRest,
            Basis.Identity,
            restHeadLocal,
            currentHeadLocal,
            headRotationDisplacementLocal: Vector3.Zero,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f);

    /// <summary>
    /// Pure helper that computes the absolute hip-local target position from the rest hip
    /// position, the rest/current head-target positions, and an optional rotation-derived neck
    /// displacement (all in skeleton-local space).
    /// </summary>
    /// <param name="hipLocalRest">Rest hip bone position in skeleton-local space.</param>
    /// <param name="hipRestBasisLocal">Hip bone rest-pose basis expressed in skeleton-local space.</param>
    /// <param name="restHeadLocal">Rest head viewpoint position in skeleton-local space.</param>
    /// <param name="currentHeadLocal">Current head viewpoint position in skeleton-local space.</param>
    /// <param name="headRotationDisplacementLocal">
    /// Displacement implied by rotating the rest neck→head vector from rest to current head
    /// orientation. The helper applies this in the opposite direction as hip correction.
    /// </param>
    /// <param name="rotationCompensationWeight">
    /// Scalar applied only to <paramref name="headRotationDisplacementLocal"/> before opposite-
    /// direction hip compensation.
    /// </param>
    /// <param name="verticalPositionWeight">
    /// Positional weight applied to movement along the hip rest up/down axis.
    /// </param>
    /// <param name="lateralPositionWeight">
    /// Positional weight applied to movement along the hip rest left/right axis.
    /// </param>
    /// <param name="forwardPositionWeight">
    /// Positional weight applied to movement along the hip rest forward/back axis.
    /// </param>
    /// <returns>
    /// <paramref name="hipLocalRest"/> when the combined positional and rotationally-derived
    /// offset is within the epsilon band; otherwise
    /// <c>hipLocalRest + (weightedHeadOffsetLocal - (headRotationDisplacementLocal * rotationCompensationWeight))</c>.
    /// </returns>
    public static Vector3 ComputeHipLocalPosition(
        Vector3 hipLocalRest,
        Basis hipRestBasisLocal,
        Vector3 restHeadLocal,
        Vector3 currentHeadLocal,
        Vector3 headRotationDisplacementLocal,
        float rotationCompensationWeight,
        float verticalPositionWeight,
        float lateralPositionWeight,
        float forwardPositionWeight)
    {
        Vector3 headOffsetLocal = currentHeadLocal - restHeadLocal;
        Vector3 weightedHeadOffsetLocal = ComputeAxisWeightedPositionOffset(
            headOffsetLocal,
            hipRestBasisLocal,
            verticalPositionWeight,
            lateralPositionWeight,
            forwardPositionWeight);
        Vector3 weightedRotationDisplacementLocal =
            headRotationDisplacementLocal * Mathf.Max(rotationCompensationWeight, 0.0f);
        Vector3 combinedOffsetLocal = weightedHeadOffsetLocal - weightedRotationDisplacementLocal;

        return combinedOffsetLocal.LengthSquared() <= PositionEpsilonSquared
            ? hipLocalRest
            : hipLocalRest + combinedOffsetLocal;
    }

    /// <summary>
    /// Compatibility overload that assumes the identity rest basis and full positional weight.
    /// </summary>
    public static Vector3 ComputeHipLocalPosition(
        Vector3 hipLocalRest,
        Vector3 restHeadLocal,
        Vector3 currentHeadLocal,
        Vector3 headRotationDisplacementLocal,
        float rotationCompensationWeight) =>
        ComputeHipLocalPosition(
            hipLocalRest,
            Basis.Identity,
            restHeadLocal,
            currentHeadLocal,
            headRotationDisplacementLocal,
            rotationCompensationWeight,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f);

    /// <summary>
    /// Compatibility overload that applies unit weight to rotational compensation.
    /// </summary>
    public static Vector3 ComputeHipLocalPosition(
        Vector3 hipLocalRest,
        Vector3 restHeadLocal,
        Vector3 currentHeadLocal,
        Vector3 headRotationDisplacementLocal) =>
        ComputeHipLocalPosition(
            hipLocalRest,
            Basis.Identity,
            restHeadLocal,
            currentHeadLocal,
            headRotationDisplacementLocal,
            rotationCompensationWeight: 1.0f,
            verticalPositionWeight: 1.0f,
            lateralPositionWeight: 1.0f,
            forwardPositionWeight: 1.0f);

    private static Vector3 ComputeAxisWeightedPositionOffset(
        Vector3 headOffsetLocal,
        Basis hipRestBasisLocal,
        float verticalPositionWeight,
        float lateralPositionWeight,
        float forwardPositionWeight)
    {
        if (headOffsetLocal.LengthSquared() <= VectorEpsilonSquared)
        {
            return Vector3.Zero;
        }

        Basis orthonormalHipRestBasisLocal = hipRestBasisLocal.Orthonormalized();
        float determinant = orthonormalHipRestBasisLocal.Determinant();
        if (Mathf.Abs(determinant) <= VectorEpsilonSquared)
        {
            return headOffsetLocal;
        }

        Vector3 headOffsetInHipRestLocal = orthonormalHipRestBasisLocal.Inverse() * headOffsetLocal;
        Vector3 weightedOffsetInHipRestLocal = new(
            headOffsetInHipRestLocal.X * Mathf.Clamp(lateralPositionWeight, 0.0f, 1.0f),
            headOffsetInHipRestLocal.Y * Mathf.Clamp(verticalPositionWeight, 0.0f, 1.0f),
            headOffsetInHipRestLocal.Z * Mathf.Clamp(forwardPositionWeight, 0.0f, 1.0f));

        return orthonormalHipRestBasisLocal * weightedOffsetInHipRestLocal;
    }

    private static bool TryComputeHeadRotationDisplacementLocal(
        PoseStateContext context,
        Skeleton3D skeleton,
        Transform3D restHeadLocalTransform,
        Transform3D currentHeadLocalTransform,
        out Vector3 displacementLocal)
    {
        displacementLocal = Vector3.Zero;

        if (context.HeadBoneIndex < 0)
        {
            return false;
        }

        int neckBoneIndex = skeleton.GetBoneParent(context.HeadBoneIndex);
        if (neckBoneIndex < 0)
        {
            return false;
        }

        Vector3 restNeckToHeadLocal =
            skeleton.GetBoneGlobalRest(context.HeadBoneIndex).Origin
            - skeleton.GetBoneGlobalRest(neckBoneIndex).Origin;

        if (restNeckToHeadLocal.LengthSquared() <= VectorEpsilonSquared)
        {
            return false;
        }

        Basis restHeadBasisLocal = restHeadLocalTransform.Basis.Orthonormalized();
        Basis currentHeadBasisLocal = currentHeadLocalTransform.Basis.Orthonormalized();
        Basis rotationOffsetBasisLocal = currentHeadBasisLocal * restHeadBasisLocal.Inverse();

        Vector3 rotatedNeckToHeadLocal = rotationOffsetBasisLocal * restNeckToHeadLocal;
        displacementLocal = rotatedNeckToHeadLocal - restNeckToHeadLocal;
        return true;
    }
}
