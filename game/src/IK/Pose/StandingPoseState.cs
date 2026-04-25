using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing the standing-to-crouching continuum.
/// </summary>
/// <remarks>
/// The standing pose family owns the full standing-to-crouching range through a single framework-
/// level state. The accompanying <see cref="StandingCrouchingSeekAnimationBinding"/> scrubs the
/// shared <c>StandingCrouching</c> AnimationTree state across that continuum, while
/// <see cref="HeadTrackingHipProfile"/> provides the matching hip reconciliation behaviour.
/// </remarks>
[GlobalClass]
public partial class StandingPoseState : PoseState
{
    /// <summary>
    /// Canonical identifier used by <see cref="StandingPoseState"/>.
    /// </summary>
    public static readonly StringName DefaultId = new("Standing");

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public StandingPoseState()
    {
        Id = DefaultId;
    }
}
