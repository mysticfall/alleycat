using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Synchronises foot IK targets from the current animated foot bone poses.
/// </summary>
/// <remarks>
/// <para>
/// Scene authoring and runtime pipeline order are part of this component's contract.
/// This stage must execute before <see cref="Pose.HipReconciliationModifier"/> and
/// before any other skeleton modifier that mutates the animated foot transforms used
/// as sync sources.
/// </para>
/// <para>
/// This stage also runs before leg IK solving so crouch/kneel animation changes update
/// both foot targets deterministically before <see cref="LegIKController"/> and
/// <see cref="TwoBoneIK3D"/> consume them.
/// </para>
/// <para>
/// Ordering is a scene-authoring/runtime-pipeline responsibility. This controller does
/// not auto-reorder modifiers at runtime.
/// </para>
/// </remarks>
[Tool]
[GlobalClass]
public partial class FootTargetSyncController : SkeletonModifier3D
{
    /// <summary>
    /// Runtime left-foot target updated from the animated left-foot bone each IK cycle.
    /// </summary>
    [Export]
    public Node3D? LeftFootTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Runtime right-foot target updated from the animated right-foot bone each IK cycle.
    /// </summary>
    [Export]
    public Node3D? RightFootTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Skeleton bone name sampled to drive <see cref="LeftFootTarget"/>.
    /// </summary>
    [Export]
    public StringName LeftFootBoneName
    {
        get;
        set;
    } = new("LeftFoot");

    /// <summary>
    /// Skeleton bone name sampled to drive <see cref="RightFootTarget"/>.
    /// </summary>
    [Export]
    public StringName RightFootBoneName
    {
        get;
        set;
    } = new("RightFoot");

    private Skeleton3D? _skeleton;
    private int _leftFootBoneIndex = -1;
    private int _rightFootBoneIndex = -1;

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        if (!Active)
        {
            return;
        }

        if (!TryResolveDependencies())
        {
            return;
        }

        SyncFootTarget(LeftFootTarget!, _leftFootBoneIndex);
        SyncFootTarget(RightFootTarget!, _rightFootBoneIndex);
    }

    private bool TryResolveDependencies()
    {
        if (_leftFootBoneIndex >= 0 && _rightFootBoneIndex >= 0 && _skeleton is not null)
        {
            return LeftFootTarget is not null && RightFootTarget is not null;
        }

        if (LeftFootTarget is null || RightFootTarget is null)
        {
            return false;
        }

        Skeleton3D? skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return false;
        }

        _skeleton = skeleton;
        _leftFootBoneIndex = skeleton.FindBone(LeftFootBoneName);
        _rightFootBoneIndex = skeleton.FindBone(RightFootBoneName);
        return _leftFootBoneIndex >= 0 && _rightFootBoneIndex >= 0;
    }

    private void SyncFootTarget(Node3D footTarget, int footBoneIndex)
    {
        Transform3D footGlobalPose = _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(footBoneIndex);
        footTarget.GlobalPosition = footGlobalPose.Origin;
        footTarget.GlobalBasis = footGlobalPose.Basis.Orthonormalized();
    }
}
