using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Hip reconciliation profile that maps the full head viewpoint offset onto the hip bone,
/// producing an absolute skeleton-local hip position that tracks the player's head while
/// compensating neck bend induced by headset rotation.
/// </summary>
/// <remarks>
/// <para>
/// The profile computes, in skeleton-local space:
/// </para>
/// <list type="number">
///   <item><description><c>hipLocalRest = skeleton.GetBoneRest(hipBoneIndex).Origin</c>.</description></item>
///   <item><description>
///     The current and rest head viewpoints in skeleton-local space from
///     <see cref="PoseStateContext.CameraTransform"/> and
///     <see cref="PoseStateContext.ViewpointGlobalRest"/> respectively, by multiplying with
///     <c>skeleton.GlobalTransform.AffineInverse()</c>.
///   </description></item>
///   <item><description>
///     <c>headOffsetLocal = currentHeadLocal - restHeadLocal</c>.
///   </description></item>
///   <item><description>
///     A rotational displacement by rotating the rest neck→head vector with the head rotation
///     offset from rest to current, then subtracting the original rest vector.
///   </description></item>
///   <item><description>
///     <c>targetHipLocal = hipLocalRest + headOffsetLocal - rotationDisplacementLocal</c>.
///   </description></item>
/// </list>
/// <para>
/// This expresses the standing/crouching head-tracking heuristic: the hip translates with the
/// head viewpoint offset and additionally applies the opposite of the rotation-implied neck
/// displacement so side tilt and chin up/down do not over-bend the neck. No Y stripping is
/// applied — vertical ownership now lives with this profile and the <c>TimeSeek</c>-driven crouch
/// clip handles feet animation independently.
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
    private const float PositionEpsilon = 1e-4f;
    private const float PositionEpsilonSquared = PositionEpsilon * PositionEpsilon;
    private const float VectorEpsilonSquared = 1e-8f;

    /// <summary>
    /// Scales the rotation-derived neck compensation before applying it in the opposite direction
    /// to the hip target.
    /// </summary>
    [Export(PropertyHint.Range, "0,3,0.01")]
    public float RotationCompensationWeight { get; set; } = DefaultRotationCompensationWeight;

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
        Transform3D skeletonInverse = skeleton.GlobalTransform.AffineInverse();

        Transform3D restHeadLocalTransform = skeletonInverse * context.ViewpointGlobalRest;
        Transform3D currentHeadLocalTransform = skeletonInverse * context.CameraTransform;

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
            restHeadLocalTransform.Origin,
            currentHeadLocalTransform.Origin,
            headRotationDisplacementLocal,
            RotationCompensationWeight);
    }

    /// <summary>
    /// Pure helper that computes the absolute hip-local target position from the rest hip
    /// position and the rest and current head viewpoints, all expressed in skeleton-local
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
            restHeadLocal,
            currentHeadLocal,
            headRotationDisplacementLocal: Vector3.Zero);

    /// <summary>
    /// Pure helper that computes the absolute hip-local target position from the rest hip
    /// position, the rest/current head viewpoints, and an optional rotation-derived neck
    /// displacement (all in skeleton-local space).
    /// </summary>
    /// <param name="hipLocalRest">Rest hip bone position in skeleton-local space.</param>
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
    /// <returns>
    /// <paramref name="hipLocalRest"/> when the combined positional and rotationally-derived
    /// offset is within the epsilon band; otherwise
    /// <c>hipLocalRest + (currentHeadLocal - restHeadLocal - (headRotationDisplacementLocal * rotationCompensationWeight))</c>.
    /// </returns>
    public static Vector3 ComputeHipLocalPosition(
        Vector3 hipLocalRest,
        Vector3 restHeadLocal,
        Vector3 currentHeadLocal,
        Vector3 headRotationDisplacementLocal,
        float rotationCompensationWeight)
    {
        Vector3 headOffsetLocal = currentHeadLocal - restHeadLocal;
        Vector3 weightedRotationDisplacementLocal =
            headRotationDisplacementLocal * Mathf.Max(rotationCompensationWeight, 0.0f);
        Vector3 combinedOffsetLocal = headOffsetLocal - weightedRotationDisplacementLocal;

        return combinedOffsetLocal.LengthSquared() <= PositionEpsilonSquared
            ? hipLocalRest
            : hipLocalRest + combinedOffsetLocal;
    }

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
            restHeadLocal,
            currentHeadLocal,
            headRotationDisplacementLocal,
            rotationCompensationWeight: 1.0f);

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
