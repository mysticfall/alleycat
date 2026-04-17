using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Abstract Resource base describing how a <see cref="PoseState"/> drives an
/// <see cref="AnimationTree"/> while active.
/// </summary>
/// <remarks>
/// Subclasses set or maintain AnimationTree parameters (for example blend weights or
/// <c>TimeSeek</c> nodes). The default implementation is a no-op so states without animation
/// requirements can omit a binding entirely.
/// </remarks>
[GlobalClass]
public abstract partial class AnimationBinding : Resource
{
    /// <summary>
    /// Applies this binding to <paramref name="tree"/> for the given tick.
    /// </summary>
    /// <param name="tree">Target AnimationTree.</param>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void Apply(AnimationTree tree, PoseStateContext context)
    {
        // No-op by default.
    }
}
