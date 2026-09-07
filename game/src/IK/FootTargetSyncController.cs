using Godot;

namespace AlleyCat.IK;

/// <summary>Optional read-only graph-pose diagnostic captured before this stage publishes either foot target.</summary>
public readonly record struct PreIKGraphPoseDiagnostic(
    bool IsAvailable,
    ulong PhysicsTick,
    int ModifierIndex,
    Transform3D RootSkeletonLocal,
    Transform3D HipsSkeletonLocal,
    Transform3D LeftFootSkeletonLocal,
    Transform3D RightFootSkeletonLocal,
    FootPosePointSet LeftPointsSkeletonLocal,
    FootPosePointSet RightPointsSkeletonLocal,
    FootPosePointDefinition LeftPointDefinition,
    FootPosePointDefinition RightPointDefinition,
    Transform3D SkeletonWorld);

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
    private int _leftToeBoneIndex = -1;
    private int _rightToeBoneIndex = -1;
    private int _rootBoneIndex = -1;
    private int _hipsBoneIndex = -1;
    private FootPosePointDefinition _leftPointDefinition;
    private FootPosePointDefinition _rightPointDefinition;
    private bool _pointDefinitionsAvailable;

    /// <summary>Enables the read-only pre-IK pose hook used only by focused diagnostics.</summary>
    public bool CapturePreIKDiagnostics
    {
        get; set;
    }

    /// <summary>Gets the last diagnostic graph pose; post-IK observations must not substitute for this value.</summary>
    public PreIKGraphPoseDiagnostic LastPreIKDiagnostic
    {
        get; private set;
    }

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

        if (CapturePreIKDiagnostics)
        {
            CaptureDiagnostic();
        }

        SyncFootTarget(LeftFootTarget!, _leftFootBoneIndex);
        SyncFootTarget(RightFootTarget!, _rightFootBoneIndex);
    }

    private void CaptureDiagnostic()
    {
        Skeleton3D skeleton = _skeleton!;
        _rootBoneIndex = _rootBoneIndex >= 0 ? _rootBoneIndex : skeleton.FindBone("Root");
        _hipsBoneIndex = _hipsBoneIndex >= 0 ? _hipsBoneIndex : skeleton.FindBone("Hips");
        if (_rootBoneIndex < 0 || _hipsBoneIndex < 0 || !TryResolveDiagnosticDependencies(skeleton))
        {
            LastPreIKDiagnostic = default;
            return;
        }

        Transform3D leftFoot = skeleton.GetBoneGlobalPose(_leftFootBoneIndex);
        Transform3D rightFoot = skeleton.GetBoneGlobalPose(_rightFootBoneIndex);
        LastPreIKDiagnostic = new(
            true,
            Engine.GetPhysicsFrames(),
            GetIndex(),
            skeleton.GetBoneGlobalPose(_rootBoneIndex),
            skeleton.GetBoneGlobalPose(_hipsBoneIndex),
            leftFoot,
            rightFoot,
            _leftPointDefinition.Evaluate(leftFoot, skeleton.GetBoneGlobalPose(_leftToeBoneIndex)),
            _rightPointDefinition.Evaluate(rightFoot, skeleton.GetBoneGlobalPose(_rightToeBoneIndex)),
            _leftPointDefinition,
            _rightPointDefinition,
            skeleton.GlobalTransform);
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

    private bool TryResolveDiagnosticDependencies(Skeleton3D skeleton)
    {
        if (_leftToeBoneIndex >= 0 && _rightToeBoneIndex >= 0 && _pointDefinitionsAvailable)
        {
            return true;
        }

        _leftToeBoneIndex = skeleton.FindBone("LeftToes");
        _rightToeBoneIndex = skeleton.FindBone("RightToes");
        _pointDefinitionsAvailable = _leftToeBoneIndex >= 0 && _rightToeBoneIndex >= 0
            && FootPosePointDefinition.TryCreate(skeleton, Rigging.LimbSide.Left, out _leftPointDefinition)
            && FootPosePointDefinition.TryCreate(skeleton, Rigging.LimbSide.Right, out _rightPointDefinition);
        return _pointDefinitionsAvailable;
    }

    private void SyncFootTarget(Node3D footTarget, int footBoneIndex)
    {
        Transform3D footGlobalPose = _skeleton!.GlobalTransform * _skeleton.GetBoneGlobalPose(footBoneIndex);
        footTarget.GlobalPosition = footGlobalPose.Origin;
        footTarget.GlobalBasis = footGlobalPose.Basis.Orthonormalized();
    }
}
