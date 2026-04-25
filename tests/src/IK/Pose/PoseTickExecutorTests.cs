using AlleyCat.IK.Pose;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for the pose-state lifecycle driver that underpins
/// <see cref="PoseStateMachine.Tick"/>.
/// </summary>
/// <remarks>
/// Tests cover the contract exposed by <see cref="PoseTickExecutor.Execute"/>: lifecycle
/// ordering on a transition (<see cref="IPoseState.OnExit"/> of the old state before
/// <see cref="IPoseState.OnEnter"/> of the new state), the <c>StateChanged</c> observer firing
/// once per switch, and no-op behaviour when no transition matches.
/// </remarks>
public sealed class PoseTickExecutorTests
{
    /// <summary>
    /// Verifies the lifecycle ordering and observer contract on a successful transition.
    /// </summary>
    [Fact]
    public void Execute_TransitionFires_InvokesExitEnterInOrderAndRaisesStateChanged()
    {
        List<string> events = [];
        RecordingState standing = new("Standing", events);
        RecordingState kneeling = new("Kneeling", events);

        RecordingTransition transition = new(
            standing.Id,
            kneeling.Id,
            shouldTransition: true,
            events);

        (IPoseState old, IPoseState next)? observed = null;

        IPoseState active = PoseTickExecutor.Execute(
            standing,
            [transition],
            new PoseStateContext(),
            resolveState: id => id == kneeling.Id ? kneeling : null,
            onStateChanged: (previous, current) => observed = (previous, current));

        Assert.Same(kneeling, active);
        Assert.True(observed.HasValue, "StateChanged observer must fire exactly once per switch.");
        Assert.Same(standing, observed!.Value.old);
        Assert.Same(kneeling, observed.Value.next);

        // Lifecycle order matches the contract: transition-enter, exit old, enter new,
        // transition-exit, then update on the new state.
        Assert.Equal(
            new[]
            {
                "TransitionEnter:Standing->Kneeling",
                "Exit:Standing",
                "Enter:Kneeling",
                "TransitionExit:Standing->Kneeling",
                "Update:Kneeling",
            },
            events);
    }

    /// <summary>
    /// Verifies no state change occurs when no transition predicate fires.
    /// </summary>
    [Fact]
    public void Execute_NoTransitionMatches_LeavesCurrentStateAndStillUpdatesIt()
    {
        List<string> events = [];
        RecordingState standing = new("Standing", events);
        RecordingState kneeling = new("Kneeling", events);

        RecordingTransition transition = new(
            standing.Id,
            kneeling.Id,
            shouldTransition: false,
            events);

        bool stateChanged = false;

        IPoseState active = PoseTickExecutor.Execute(
            standing,
            [transition],
            new PoseStateContext(),
            resolveState: _ => throw new Xunit.Sdk.XunitException(
                "Resolver must not be called when no transition matches."),
            onStateChanged: (_, _) => stateChanged = true);

        Assert.Same(standing, active);
        Assert.False(stateChanged);
        Assert.Equal(new[] { "Update:Standing" }, events);
    }

    /// <summary>
    /// Verifies transitions whose <see cref="IPoseTransition.From"/> does not match the current
    /// state are skipped and no lifecycle hooks fire.
    /// </summary>
    [Fact]
    public void Execute_TransitionWithMismatchedFrom_IsIgnored()
    {
        List<string> events = [];
        RecordingState standing = new("Standing", events);
        RecordingState kneeling = new("Kneeling", events);

        RecordingTransition mismatched = new(
            "Unused",
            kneeling.Id,
            shouldTransition: true,
            events);

        IPoseState active = PoseTickExecutor.Execute(
            standing,
            [mismatched],
            new PoseStateContext(),
            resolveState: _ => throw new Xunit.Sdk.XunitException(
                "Resolver must not be called when transition is filtered out."),
            onStateChanged: (_, _) => throw new Xunit.Sdk.XunitException(
                "StateChanged must not fire when no transition applies."));

        Assert.Same(standing, active);
        Assert.Equal(new[] { "Update:Standing" }, events);
    }

    private sealed class RecordingState(string id, List<string> log) : IPoseState
    {
        public string Id => id;

        public void OnEnter(PoseStateContext context) => log.Add($"Enter:{id}");

        public void OnExit(PoseStateContext context) => log.Add($"Exit:{id}");

        public void OnUpdate(PoseStateContext context) => log.Add($"Update:{id}");
    }

    private sealed class RecordingTransition(
        string from,
        string to,
        bool shouldTransition,
        List<string> log) : IPoseTransition
    {
        public string From => from;

        public string To => to;

        public bool ShouldTransition(PoseStateContext context) => shouldTransition;

        public void OnTransitionEnter(PoseStateContext context) =>
            log.Add($"TransitionEnter:{from}->{to}");

        public void OnTransitionExit(PoseStateContext context) =>
            log.Add($"TransitionExit:{from}->{to}");
    }
}
