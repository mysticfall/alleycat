using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing a crouched posture.
/// </summary>
/// <remarks>
/// <para>
/// Crouching shares hip reconciliation behaviour with <see cref="StandingPoseState"/>: both
/// states use the 1:1 <see cref="HeadTrackingHipProfile"/>, which computes an absolute hip
/// target in skeleton-local space from the head viewpoint offset. Vertical descent is owned
/// by the hip profile rather than the animation, because the Standing↔Crouching AnimationTree
/// state scrubs a single clip through <c>TimeSeek</c> and cannot distinguish upper-body posture
/// by itself.
/// </para>
/// <para>
/// Ticking of this state is driven by <see cref="PoseStateMachine.Tick"/> and transitions are
/// normally authored as <see cref="HeadOffsetPoseTransition"/> edges keyed on head movement.
/// </para>
/// </remarks>
[GlobalClass]
public partial class CrouchingPoseState : PoseState
{
    /// <summary>
    /// Canonical identifier used by <see cref="CrouchingPoseState"/>.
    /// </summary>
    public static readonly StringName DefaultId = new("Crouching");

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public CrouchingPoseState()
    {
        Id = DefaultId;
    }
}
