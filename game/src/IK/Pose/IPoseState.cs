using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Read-only view over a pose-state entry used by transition evaluation and the state machine.
/// </summary>
/// <remarks>
/// Defined as an interface so the transition evaluator and lifecycle driver can be exercised
/// without instantiating Godot <see cref="Resource"/>-backed subclasses. Concrete state content
/// (animation binding, hip profile) is carried by the <see cref="PoseState"/> Resource subclass.
/// </remarks>
public interface IPoseState
{
    /// <summary>
    /// Gets the unique identifier for this state as a plain string.
    /// </summary>
    /// <remarks>
    /// Exposed as <see cref="string"/> rather than <see cref="StringName"/> so the lifecycle
    /// driver and transition evaluator can be covered by unit tests without constructing Godot
    /// reference-type identifiers (which require engine initialisation).
    /// </remarks>
    string Id
    {
        get;
    }

    /// <summary>
    /// Invoked once when the state becomes active.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    void OnEnter(PoseStateContext context);

    /// <summary>
    /// Invoked once when the state is exiting in favour of a different state.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    void OnExit(PoseStateContext context);

    /// <summary>
    /// Invoked each tick while the state is active.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    void OnUpdate(PoseStateContext context);
}
