using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Pure helper that runs the pose-state lifecycle and transition sequence for one tick.
/// </summary>
/// <remarks>
/// Extracted from <see cref="PoseStateMachine"/> so the transition-driven lifecycle ordering
/// contract (Exit old → Enter new, change event firing) is covered by unit tests without
/// instantiating Godot <see cref="Resource"/>-backed subclasses.
/// </remarks>
public static class PoseTickExecutor
{
    /// <summary>
    /// Callback invoked after the state machine switches from <paramref name="oldState"/> to
    /// <paramref name="newState"/>.
    /// </summary>
    /// <param name="oldState">State that was active before the transition.</param>
    /// <param name="newState">State that is active after the transition.</param>
    public delegate void StateChangedHandler(IPoseState oldState, IPoseState newState);

    /// <summary>
    /// Evaluates transitions and drives the lifecycle hooks for one tick.
    /// </summary>
    /// <param name="currentState">Currently active state; must not be <c>null</c>.</param>
    /// <param name="transitions">Transitions to evaluate in order.</param>
    /// <param name="context">Current context snapshot.</param>
    /// <param name="resolveState">
    /// Resolver that maps a destination identifier from a selected transition back to the state
    /// instance tracked by the state machine.
    /// </param>
    /// <param name="onStateChanged">Optional observer fired after a switch completes.</param>
    /// <returns>
    /// The state that is active at the end of this tick. The return value equals
    /// <paramref name="currentState"/> when no transition fired.
    /// </returns>
    public static IPoseState Execute(
        IPoseState currentState,
        IReadOnlyList<IPoseTransition> transitions,
        PoseStateContext context,
        Func<string, IPoseState?> resolveState,
        StateChangedHandler? onStateChanged)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolveState);

        IPoseTransition? selected = PoseTransitionEvaluator.SelectTransition(
            currentState.Id,
            transitions,
            context);

        IPoseState activeState = currentState;
        if (selected is not null)
        {
            IPoseState nextState = resolveState(selected.To)
                ?? throw new InvalidOperationException(
                    $"Transition from '{currentState.Id}' targets unknown state '{selected.To}'.");

            selected.OnTransitionEnter(context);
            currentState.OnExit(context);
            nextState.OnEnter(context);
            selected.OnTransitionExit(context);
            onStateChanged?.Invoke(currentState, nextState);

            activeState = nextState;
        }

        activeState.OnUpdate(context);
        return activeState;
    }
}
