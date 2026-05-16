using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Describes a resolved IK target pose and whether a physical target actuator may move towards it.
/// </summary>
public readonly struct IKTargetFollowState(Transform3D worldTransform, bool active)
{
    /// <summary>
    /// World-space target pose for the actuator when <see cref="Active" /> is true.
    /// </summary>
    public Transform3D WorldTransform { get; } = worldTransform;

    /// <summary>
    /// Whether the associated actuator may perform physical movement towards <see cref="WorldTransform" />.
    /// </summary>
    public bool Active { get; } = active;
}
