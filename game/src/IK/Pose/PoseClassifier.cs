using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Abstract Resource base that scores how strongly a given state should be considered active
/// for a given context.
/// </summary>
/// <remarks>
/// <para>
/// Classifiers provide the extension surface called out by the Pose State Machine Contract
/// for resolving overlapping poses (for example stoop versus crouch) using auxiliary XR signals.
/// </para>
/// <para>
/// Increment 1 exposes the type only; the <see cref="PoseStateMachine"/> driver does not yet
/// consume classifier scores as part of its transition selection. Wiring classifiers into the
/// state-machine loop is deferred to a subsequent increment when concrete non-standing states
/// are added.
/// </para>
/// </remarks>
[GlobalClass]
public abstract partial class PoseClassifier : Resource
{
    /// <summary>
    /// Scores how strongly <paramref name="stateId"/> should be considered active given
    /// <paramref name="context"/>.
    /// </summary>
    /// <param name="stateId">State identifier being evaluated.</param>
    /// <param name="context">Current pose-state context snapshot.</param>
    /// <returns>A non-negative confidence score; higher means more confidence.</returns>
    public virtual float Score(StringName stateId, PoseStateContext context) => 0f;
}
