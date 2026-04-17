using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Skeleton modifier that applies the pose state machine's pending hip target position to the
/// hip bone inside the modifier pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The modifier is intentionally a consumer only: it reads
/// <see cref="PoseStateMachine.PendingHipLocalPosition"/> each tick and writes the resulting
/// pose. The state machine's <c>Tick</c> method is invoked by a higher-level driver
/// (<c>PlayerVRIK</c>) so the context snapshot comes from the same producer that drives
/// XR-to-IK.
/// </para>
/// <para>
/// The pending value is an <em>absolute</em> hip target in skeleton-local space: it replaces
/// the animated hip position rather than adding to it. This is deliberate — mixing a delta
/// with the currently animated hip bone would create a feedback loop with the <c>TimeSeek</c>
/// crouch scrubbing and manifest as visible spine flicker. A <see langword="null"/> value
/// means "no override for this tick" and the modifier leaves the animated hip pose alone.
/// </para>
/// <para>
/// Place this modifier as a direct child of the target <see cref="Skeleton3D"/> ordered after
/// the animation player so bone transforms at pass entry reflect the current animation sample,
/// per the Hip Reconciliation Contract (AC-HR-07).
/// </para>
/// </remarks>
[GlobalClass]
public partial class HipReconciliationModifier : SkeletonModifier3D
{
    /// <summary>
    /// State machine providing <see cref="PoseStateMachine.PendingHipLocalPosition"/>.
    /// </summary>
    [Export]
    public PoseStateMachine? StateMachine
    {
        get;
        set;
    }

    /// <summary>
    /// Name of the hip bone whose pose receives the target position.
    /// </summary>
    [Export]
    public StringName HipBoneName
    {
        get;
        set;
    } = new("Hips");

    private int _hipBoneIndex = -1;
    private bool _hipBoneResolved;

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        _ = delta;

        PoseStateMachine? stateMachine = StateMachine;
        if (stateMachine is null)
        {
            return;
        }

        Vector3? target = stateMachine.PendingHipLocalPosition;
        if (target is null)
        {
            return;
        }

        Skeleton3D? skeleton = GetSkeleton();
        if (skeleton is null)
        {
            return;
        }

        if (!_hipBoneResolved)
        {
            _hipBoneIndex = skeleton.FindBone(HipBoneName);
            if (_hipBoneIndex < 0)
            {
                return;
            }

            _hipBoneResolved = true;
        }

        skeleton.SetBonePosePosition(_hipBoneIndex, target.Value);
    }
}
