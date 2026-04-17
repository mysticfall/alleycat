using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Read-only view over a pose transition edge used by transition evaluation.
/// </summary>
/// <remarks>
/// Defined as an interface so the transition evaluator can be exercised without instantiating
/// Godot <see cref="Resource"/>-backed subclasses. Concrete transition content is carried by the
/// <see cref="PoseTransition"/> Resource subclass.
/// </remarks>
public interface IPoseTransition
{
    /// <summary>
    /// Gets the identifier of the source state this transition applies to.
    /// </summary>
    /// <remarks>
    /// Exposed as <see cref="string"/> rather than <see cref="StringName"/> so the transition
    /// evaluator can be covered by unit tests without constructing Godot reference-type
    /// identifiers (which require engine initialisation).
    /// </remarks>
    string From
    {
        get;
    }

    /// <summary>
    /// Gets the identifier of the destination state this transition targets.
    /// </summary>
    /// <remarks>
    /// Exposed as <see cref="string"/> rather than <see cref="StringName"/> for the same reason
    /// as <see cref="From"/>.
    /// </remarks>
    string To
    {
        get;
    }

    /// <summary>
    /// Evaluates whether this transition should fire for the given context.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    /// <returns><c>true</c> when the transition must fire; otherwise <c>false</c>.</returns>
    bool ShouldTransition(PoseStateContext context);

    /// <summary>
    /// Invoked when the transition is selected and about to take effect.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    void OnTransitionEnter(PoseStateContext context);

    /// <summary>
    /// Invoked after the transition has applied and the new state has been entered.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    void OnTransitionExit(PoseStateContext context);
}
