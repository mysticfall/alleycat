using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Scene-wireable provider for a world-space IK target transform and matching modifier influence.
/// </summary>
[GlobalClass]
public abstract partial class IKTargetIntentProvider : Node
{
    /// <summary>
    /// Whether <see cref="CharacterIK" /> should apply this provider's transform to the target node.
    /// </summary>
    public virtual bool ShouldApplyTargetTransform => true;

    /// <summary>
    /// Tries to get the current applicable world-space IK target resolution.
    /// </summary>
    /// <param name="resolution">The target intent and target-transform application instruction.</param>
    /// <returns><see langword="true" /> when this provider supplies a resolution for the current frame.</returns>
    /// <remarks>
    /// The default preserves legacy <see cref="GetTargetIntent" /> and
    /// <see cref="ShouldApplyTargetTransform" /> semantics. In particular, a zero-influence legacy intent
    /// remains an applicable, terminal resolution.
    /// </remarks>
    public virtual bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
    {
        resolution = new IKTargetIntentResolution(GetTargetIntent(), ShouldApplyTargetTransform);
        return true;
    }

    /// <summary>
    /// Gets the current world-space IK target intent.
    /// </summary>
    /// <returns>The target transform and desired modifier influence for this frame.</returns>
    public abstract IKTargetIntent GetTargetIntent();
}
