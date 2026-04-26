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
    private bool _warnedMissingPlayback;

    /// <summary>
    /// Optional AnimationTree playback parameter used when this transition also owns animation
    /// state-machine travel.
    /// </summary>
    [Export]
    public StringName PlaybackParameter
    {
        get;
        set;
    } = new("parameters/playback");

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
        ArgumentNullException.ThrowIfNull(context);

        AnimationTree? tree = context.AnimationTree;
        if (tree is null)
        {
            return;
        }

        bool requiresPlayback = !TransitionAnimationStateName.IsEmpty;
        AnimationNodeStateMachinePlayback? playback = ResolvePlayback(tree);
        if (requiresPlayback && playback is null)
        {
            WarnMissingPlayback();
            return;
        }

        if (playback is not null && !TransitionAnimationStateName.IsEmpty)
        {
            playback.Travel(TransitionAnimationStateName);
        }
    }

    /// <summary>
    /// Invoked on every non-selected transition immediately after another transition has fired
    /// in the same tick.
    /// </summary>
    /// <remarks>
    /// Default implementation is a no-op. Subclasses that maintain armed/trigger state shared
    /// with an opposite-direction transition should override this hook to reset their internal
    /// state when any sibling transition fires, preventing same-tick ping-pong across an
    /// overlapping trigger region.
    /// </remarks>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnAnotherTransitionFired(PoseStateContext context)
    {
        // No-op by default.
    }

    /// <summary>
    /// Gets the AnimationTree state-machine node played immediately when this transition fires.
    /// </summary>
    protected virtual StringName TransitionAnimationStateName => new();

    /// <summary>
    /// Resolves the configured AnimationTree state-machine playback object.
    /// </summary>
    protected AnimationNodeStateMachinePlayback? ResolvePlayback(AnimationTree tree) =>
        PlaybackParameter.IsEmpty
            ? null
            : tree.Get(PlaybackParameter).As<AnimationNodeStateMachinePlayback>();

    private void WarnMissingPlayback()
    {
        if (_warnedMissingPlayback)
        {
            return;
        }

        GD.PushWarning(
            $"{GetType().Name} could not resolve playback object at '{PlaybackParameter}'.");
        _warnedMissingPlayback = true;
    }
}
