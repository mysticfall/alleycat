using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for the pose-transition selection helper.
/// </summary>
/// <remarks>
/// Exercises the transition evaluator via <see cref="IPoseTransition"/> test doubles so Godot
/// <see cref="Resource"/> subclasses do not need to be instantiated outside the engine.
/// </remarks>
public sealed class PoseTransitionEvaluatorTests
{
    /// <summary>
    /// The first candidate whose <see cref="IPoseTransition.From"/> matches the current state
    /// and whose predicate is true must be selected, even when later candidates also match.
    /// </summary>
    [Fact]
    public void SelectTransition_FirstMatchingCandidateWins()
    {
        const string fromId = "A";
        const string firstTarget = "B";
        const string secondTarget = "C";

        RecordingTransition first = new(fromId, firstTarget, shouldTransition: true);
        RecordingTransition second = new(fromId, secondTarget, shouldTransition: true);

        IPoseTransition? selected = PoseTransitionEvaluator.SelectTransition(
            fromId,
            [first, second],
            new PoseStateContext());

        Assert.Same(first, selected);
        Assert.Equal(1, first.EvaluationCount);
        Assert.Equal(0, second.EvaluationCount);
    }

    /// <summary>
    /// Transitions whose <see cref="IPoseTransition.From"/> does not match the current state
    /// must be skipped without evaluating their predicate.
    /// </summary>
    [Fact]
    public void SelectTransition_IgnoresTransitionsWhoseFromDoesNotMatchCurrentState()
    {
        const string currentId = "A";
        const string otherId = "Unused";
        const string targetId = "B";

        RecordingTransition mismatch = new(otherId, targetId, shouldTransition: true);
        RecordingTransition match = new(currentId, targetId, shouldTransition: true);

        IPoseTransition? selected = PoseTransitionEvaluator.SelectTransition(
            currentId,
            [mismatch, match],
            new PoseStateContext());

        Assert.Same(match, selected);
        Assert.Equal(0, mismatch.EvaluationCount);
        Assert.Equal(1, match.EvaluationCount);
    }

    /// <summary>
    /// When no transition matches the current state or no predicate fires, the helper must
    /// return <c>null</c>.
    /// </summary>
    [Fact]
    public void SelectTransition_ReturnsNull_WhenNoCandidateMatches()
    {
        const string currentId = "A";
        RecordingTransition inactive = new(currentId, "B", shouldTransition: false);

        IPoseTransition? selected = PoseTransitionEvaluator.SelectTransition(
            currentId,
            [inactive],
            new PoseStateContext());

        Assert.Null(selected);
        Assert.Equal(1, inactive.EvaluationCount);
    }

    /// <summary>
    /// An empty transition list must return <c>null</c> without throwing.
    /// </summary>
    [Fact]
    public void SelectTransition_EmptyList_ReturnsNull()
    {
        IPoseTransition? selected = PoseTransitionEvaluator.SelectTransition(
            "A",
            [],
            new PoseStateContext());

        Assert.Null(selected);
    }

    private sealed class RecordingTransition(string from, string to, bool shouldTransition)
        : IPoseTransition
    {
        public string From => from;

        public string To => to;

        public int EvaluationCount
        {
            get;
            private set;
        }

        public bool ShouldTransition(PoseStateContext context)
        {
            EvaluationCount++;
            return shouldTransition;
        }

        public void OnTransitionEnter(PoseStateContext context)
        {
        }

        public void OnTransitionExit(PoseStateContext context)
        {
        }
    }
}
