using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Concrete pose state representing a kneeling posture.
/// </summary>
[GlobalClass]
public partial class KneelingPoseState : PoseState
{
    /// <summary>
    /// Canonical identifier used by <see cref="KneelingPoseState"/>.
    /// </summary>
    public static readonly StringName DefaultId = new("Kneeling");

    /// <summary>
    /// Default steady-state AnimationTree node used by the kneeling posture.
    /// </summary>
    public static readonly StringName DefaultAnimationStateName = new("Kneeling");

    /// <summary>
    /// Initialises the state and seeds <see cref="PoseState.Id"/> with <see cref="DefaultId"/>.
    /// </summary>
    public KneelingPoseState()
    {
        Id = DefaultId;
        AnimationStateName = DefaultAnimationStateName;
    }
}
