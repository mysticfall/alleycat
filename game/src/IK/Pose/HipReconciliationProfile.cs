using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Abstract Resource base for hip reconciliation behaviour attached to a <see cref="PoseState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Subclasses produce an absolute hip bone position expressed in the skeleton's local basis
/// (the same space <see cref="Skeleton3D.SetBonePosePosition"/> expects). Returning
/// <see langword="null"/> means "do not override the animated hip", which is the default
/// behaviour provided by this base class.
/// </para>
/// <para>
/// This API is deliberately absolute (target position) rather than delta-style. A delta against
/// the currently animated hip bone creates a feedback loop because the animation sample itself
/// is being modulated each tick through the <c>TimeSeek</c> subgraph; mixing both would cause
/// spine flicker during crouch descent. Clamp constraints and collision-aware behaviour remain
/// deferred per the Hip Reconciliation Contract.
/// </para>
/// </remarks>
[GlobalClass]
public abstract partial class HipReconciliationProfile : Resource
{
    /// <summary>
    /// Computes the absolute hip bone position in skeleton-local space for the given tick
    /// context.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    /// <returns>
    /// The target hip bone position in skeleton-local space, or <see langword="null"/> to keep
    /// the animated hip position unchanged. The default implementation returns
    /// <see langword="null"/>.
    /// </returns>
    public virtual Vector3? ComputeHipLocalPosition(PoseStateContext context) => null;
}
