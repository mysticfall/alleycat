using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Abstract Resource base describing a transition edge between two pose states.
/// </summary>
/// <remarks>
/// Subclasses implement <see cref="ShouldTransition"/> to express state-specific predicates.
/// The default predicate returns <c>false</c> so empty subclasses never fire by accident.
/// </remarks>
[GlobalClass]
public abstract partial class PoseTransition : Resource, IPoseTransition
{
    /// <summary>
    /// Identifier of the source state this transition applies to.
    /// </summary>
    [Export]
    public StringName From
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Identifier of the destination state this transition targets.
    /// </summary>
    [Export]
    public StringName To
    {
        get;
        set;
    } = new();

    string IPoseTransition.From => From.ToString();

    string IPoseTransition.To => To.ToString();

    /// <summary>
    /// Evaluates whether this transition should fire for the given context.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    /// <returns><c>true</c> when the transition must fire; otherwise <c>false</c>.</returns>
    public virtual bool ShouldTransition(PoseStateContext context) => false;

    /// <summary>
    /// Invoked when the transition is selected and about to take effect.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnTransitionEnter(PoseStateContext context)
    {
        // No-op by default.
    }

    /// <summary>
    /// Invoked after the transition has applied and the new state has been entered.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnTransitionExit(PoseStateContext context)
    {
        // No-op by default.
    }
}
