using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Scene-wireable provider for a world-space IK target transform and matching modifier influence.
/// </summary>
[GlobalClass]
public abstract partial class IKTargetStateProvider : Node
{
    /// <summary>
    /// Whether <see cref="CharacterIK" /> should apply this provider's transform to the target node.
    /// </summary>
    public virtual bool ShouldApplyTargetTransform => true;

    /// <summary>
    /// Gets the current world-space IK target state.
    /// </summary>
    /// <returns>The target transform and desired modifier influence for this frame.</returns>
    public abstract IKTargetState GetTargetState();
}
