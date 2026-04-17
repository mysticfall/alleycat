using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Hip reconciliation profile that maps the full head viewpoint offset onto the hip bone,
/// producing an absolute skeleton-local hip position that tracks the player's head 1:1.
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
///   <item><description><c>targetHipLocal = hipLocalRest + headOffsetLocal</c>.</description></item>
/// </list>
/// <para>
/// This expresses the "1:1 head-tracking hip" heuristic for the Standing/Crouching pose family:
/// the hip translates exactly as the head translates, preserving the rest-pose upper-body
/// posture. No Y stripping is applied — vertical ownership now lives with this profile and the
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
/// An epsilon-based jitter suppression snaps <c>headOffsetLocal</c> to zero when its squared
/// length falls inside the threshold, so the hip returns cleanly to <c>hipLocalRest</c> and
/// does not wobble on residual calibration noise.
/// </para>
/// </remarks>
[GlobalClass]
public partial class HeadTrackingHipProfile : HipReconciliationProfile
{
    private const float PositionEpsilon = 1e-4f;
    private const float PositionEpsilonSquared = PositionEpsilon * PositionEpsilon;

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

        Vector3 restHeadLocal = skeletonInverse * context.ViewpointGlobalRest.Origin;
        Vector3 currentHeadLocal = skeletonInverse * context.CameraTransform.Origin;

        return ComputeHipLocalPosition(hipLocalRest, restHeadLocal, currentHeadLocal);
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
        Vector3 currentHeadLocal)
    {
        Vector3 headOffsetLocal = currentHeadLocal - restHeadLocal;
        return headOffsetLocal.LengthSquared() <= PositionEpsilonSquared
            ? hipLocalRest
            : hipLocalRest + headOffsetLocal;
    }
}
