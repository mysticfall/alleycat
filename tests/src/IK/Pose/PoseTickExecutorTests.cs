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

        PoseTickExecutor.Result result = PoseTickExecutor.Execute(
            standing,
            [transition],
            new PoseStateContext(),
            resolveState: id => id == kneeling.Id ? kneeling : null,
            onStateChanged: (previous, current) => observed = (previous, current));

        Assert.Same(kneeling, result.ActiveState);
        Assert.Same(transition, result.SelectedTransition);
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

        PoseTickExecutor.Result result = PoseTickExecutor.Execute(
            standing,
            [transition],
            new PoseStateContext(),
            resolveState: _ => throw new Xunit.Sdk.XunitException(
                "Resolver must not be called when no transition matches."),
            onStateChanged: (_, _) => stateChanged = true);

        Assert.Same(standing, result.ActiveState);
        Assert.Null(result.SelectedTransition);
        Assert.False(stateChanged);
        Assert.Equal(new[] { "Update:Standing" }, events);
    }

    /// <summary>
    /// After a transition fires, every non-selected transition must receive an
    /// <see cref="IPoseTransition.OnAnotherTransitionFired"/> notification so they can reset
    /// cross-transition gating state. The selected transition itself must not receive it.
    /// </summary>
    [Fact]
    public void Execute_TransitionFires_NotifiesOtherTransitionsOfFire()
    {
        List<string> events = [];
        RecordingState standing = new("Standing", events);
        RecordingState kneeling = new("Kneeling", events);

        RecordingTransition selected = new(
            standing.Id,
            kneeling.Id,
            shouldTransition: true,
            events);
        RecordingTransition sibling = new(
            kneeling.Id,
            standing.Id,
            shouldTransition: false,
            events);

        _ = PoseTickExecutor.Execute(
            standing,
            [selected, sibling],
            new PoseStateContext(),
            resolveState: id => id == kneeling.Id ? kneeling : null,
            onStateChanged: (_, _) => { });

        // The selected transition fires its enter/exit hooks, the sibling receives
        // OnAnotherTransitionFired, and the selected transition itself is not notified.
        Assert.Contains("AnotherTransitionFired:Kneeling->Standing", events);
        Assert.DoesNotContain("AnotherTransitionFired:Standing->Kneeling", events);

        int notificationIndex = events.IndexOf("AnotherTransitionFired:Kneeling->Standing");
        int selectedTransitionExitIndex = events.IndexOf("TransitionExit:Standing->Kneeling");
        int updateIndex = events.IndexOf("Update:Kneeling");

        Assert.True(selectedTransitionExitIndex < notificationIndex);
        Assert.True(notificationIndex < updateIndex);
    }

    /// <summary>
    /// When no transition fires, no sibling transition must receive the
    /// <see cref="IPoseTransition.OnAnotherTransitionFired"/> notification.
    /// </summary>
    [Fact]
    public void Execute_NoTransitionFires_DoesNotNotifySiblings()
    {
        List<string> events = [];
        RecordingState standing = new("Standing", events);
        RecordingState kneeling = new("Kneeling", events);

        RecordingTransition first = new(
            standing.Id,
            kneeling.Id,
            shouldTransition: false,
            events);
        RecordingTransition second = new(
            kneeling.Id,
            standing.Id,
            shouldTransition: false,
            events);

        _ = PoseTickExecutor.Execute(
            standing,
            [first, second],
            new PoseStateContext(),
            resolveState: _ => throw new Xunit.Sdk.XunitException("Resolver must not be called."),
            onStateChanged: (_, _) => throw new Xunit.Sdk.XunitException("StateChanged must not fire."));

        Assert.DoesNotContain("AnotherTransitionFired:Standing->Kneeling", events);
        Assert.DoesNotContain("AnotherTransitionFired:Kneeling->Standing", events);
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

        PoseTickExecutor.Result result = PoseTickExecutor.Execute(
            standing,
            [mismatched],
            new PoseStateContext(),
            resolveState: _ => throw new Xunit.Sdk.XunitException(
                "Resolver must not be called when transition is filtered out."),
            onStateChanged: (_, _) => throw new Xunit.Sdk.XunitException(
                "StateChanged must not fire when no transition applies."));

        Assert.Same(standing, result.ActiveState);
        Assert.Null(result.SelectedTransition);
        Assert.Equal(new[] { "Update:Standing" }, events);
    }

    /// <summary>
    /// Verifies executor-driven lifecycle hooks observe the same context instance supplied to the
    /// tick, so runtime data such as the current animation tree flows through the interface
    /// contract rather than machine-side concrete callbacks.
    /// </summary>
    [Fact]
    public void Execute_TransitionFires_PassesOriginalContextThroughLifecycleHooks()
    {
        PoseStateContext context = new()
        {
            Delta = 1.0 / 60.0,
        };

        ContextCapturingState standing = new("Standing");
        ContextCapturingState kneeling = new("Kneeling");
        ContextCapturingTransition transition = new(standing.Id, kneeling.Id, shouldTransition: true);

        _ = PoseTickExecutor.Execute(
            standing,
            [transition],
            context,
            resolveState: id => id == kneeling.Id ? kneeling : null,
            onStateChanged: (_, _) => { });

        Assert.Same(context, transition.TransitionEnterContext);
        Assert.Same(context, standing.ExitContext);
        Assert.Same(context, kneeling.EnterContext);
        Assert.Same(context, transition.TransitionExitContext);
        Assert.Same(context, kneeling.UpdateContext);
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

        public void OnAnotherTransitionFired(PoseStateContext context) =>
            log.Add($"AnotherTransitionFired:{from}->{to}");
    }

    private sealed class ContextCapturingState(string id) : IPoseState
    {
        public string Id => id;

        public PoseStateContext? EnterContext
        {
            get;
            private set;
        }

        public PoseStateContext? ExitContext
        {
            get;
            private set;
        }

        public PoseStateContext? UpdateContext
        {
            get;
            private set;
        }

        public void OnEnter(PoseStateContext context) => EnterContext = context;

        public void OnExit(PoseStateContext context) => ExitContext = context;

        public void OnUpdate(PoseStateContext context) => UpdateContext = context;
    }

    private sealed class ContextCapturingTransition(string from, string to, bool shouldTransition)
        : IPoseTransition
    {
        public string From => from;

        public string To => to;

        public PoseStateContext? TransitionEnterContext
        {
            get;
            private set;
        }

        public PoseStateContext? TransitionExitContext
        {
            get;
            private set;
        }

        public bool ShouldTransition(PoseStateContext context) => shouldTransition;

        public void OnTransitionEnter(PoseStateContext context) => TransitionEnterContext = context;

        public void OnTransitionExit(PoseStateContext context) => TransitionExitContext = context;

        public void OnAnotherTransitionFired(PoseStateContext context)
        {
        }
    }
}
