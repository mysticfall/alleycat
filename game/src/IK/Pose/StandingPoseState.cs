using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Minimal concrete pose state representing a standing/upright posture.
/// </summary>
/// <remarks>
/// Increment 1 ships this state to exercise the framework end-to-end. No additional lifecycle
/// behaviour is required over the base class; animation binding and hip reconciliation profile
/// are supplied via inspector-exported fields on <see cref="PoseState"/>.
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
