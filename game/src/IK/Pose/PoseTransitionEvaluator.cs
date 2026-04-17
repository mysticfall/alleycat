using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Pure helper that selects the first matching transition for the current state.
/// </summary>
/// <remarks>
/// Isolated from <see cref="PoseStateMachine"/> so the transition-selection contract can be
/// covered by unit tests without instantiating Godot <see cref="Resource"/> subclasses.
/// </remarks>
public static class PoseTransitionEvaluator
{
    /// <summary>
    /// Returns the first transition in <paramref name="transitions"/> whose
    /// <see cref="IPoseTransition.From"/> matches <paramref name="currentStateId"/> and whose
    /// <see cref="IPoseTransition.ShouldTransition"/> returns <c>true</c>.
    /// </summary>
    /// <param name="currentStateId">Identifier of the currently active state.</param>
    /// <param name="transitions">Ordered transition candidates to evaluate.</param>
    /// <param name="context">Context passed to every candidate's predicate.</param>
    /// <returns>The selected transition, or <c>null</c> when none match.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="currentStateId"/> or <paramref name="transitions"/> is <c>null</c>.
    /// </exception>
    public static IPoseTransition? SelectTransition(
        string currentStateId,
        IReadOnlyList<IPoseTransition> transitions,
        PoseStateContext context)
    {
        ArgumentNullException.ThrowIfNull(currentStateId);
        ArgumentNullException.ThrowIfNull(transitions);

        for (int i = 0; i < transitions.Count; i++)
        {
            IPoseTransition candidate = transitions[i];
            if (!string.Equals(candidate.From, currentStateId, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.ShouldTransition(context))
            {
                return candidate;
            }
        }

        return null;
    }
}
