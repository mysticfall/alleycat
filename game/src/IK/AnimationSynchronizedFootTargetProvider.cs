using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Foot fallback provider that keeps animation-synchronised foot targets active without taking transform ownership.
/// </summary>
[GlobalClass]
public partial class AnimationSynchronizedFootTargetProvider : IKTargetStateProvider
{
    /// <summary>
    /// Foot target currently owned by the animation synchronisation stage.
    /// </summary>
    [Export]
    public Node3D? FootTarget
    {
        get; set;
    }

    /// <inheritdoc />
    public override bool ShouldApplyTargetTransform => false;

    /// <inheritdoc />
    public override IKTargetState GetTargetState()
    {
        Node3D? footTarget = FootTarget;
        return footTarget is not null && IsInstanceValid(footTarget)
            ? new IKTargetState(footTarget.GlobalTransform, 1.0f)
            : new IKTargetState(Transform3D.Identity, 0.0f);
    }
}
