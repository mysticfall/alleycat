using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Exposes the effective crouching-continuum values at the moment a transition fires,
/// so receiving states can smoothly blend from the source's current parameters to their
/// own without hard-coding TransitionSource* properties per state pair.
/// </summary>
public interface ICrouchingPoseTransitionSource
{
    /// <summary>
    /// Returns the effective rotation-compensation scale at the transition moment.
    /// </summary>
    float GetEffectiveRotationCompensationScale(PoseStateContext context);

    /// <summary>
    /// Returns the effective hip-offset envelope at the transition moment, or null when
    /// no limits are configured.
    /// </summary>
    HipLimitEnvelope? GetEffectiveHipOffsetEnvelope(PoseStateContext context);

    /// <summary>
    /// Returns the effective hip-reference position in skeleton-local space at the
    /// transition moment. Receiving states blend from this position to their own
    /// target reference to ensure position continuity across the transition.
    /// </summary>
    Vector3 GetEffectiveReferenceHipLocalPosition(PoseStateContext context);
}
