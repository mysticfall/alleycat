using AlleyCat.Animations;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using Frame = AlleyCat.IntegrationTests.Animations.NativeIdleTransitionHarness.Frame;

namespace AlleyCat.IntegrationTests.Animations;

/// <summary>
/// ANIM-003 TR15, TR20–TR21 with AI-015: the agent's <c>walk_to</c> and <c>stop</c> tool functions, bound through the
/// production <see cref="CoordinateNavigationController" /> to the installed NPC's real MotionBricks navigation, leave a
/// native idle or an idle→idle bridge without a pose pop, and a stop while walking settles into the preferred idle
/// through an arrival bridge. Seam bounds are those of <see cref="NativeIdleTransitionTests" />.
/// </summary>
[IsolatedProcess]
public sealed class AgentNavigationIdleTests
{
    private const float WalkMetres = 3f;

    /// <summary>
    /// From a native idle, <c>stop</c> is a no-op; <c>walk_to</c> bridges into locomotion; <c>stop</c> during ordinary
    /// walking (after the departure bridge) ends the move as stopped and arrives into the preferred idle with an
    /// ease-out bridge and no velocity blend.
    /// </summary>
    [Fact]
    public async Task WalkToFromNativeIdle_ThenStopWhileWalking_SettlesIntoPreferredIdle()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        using AgentTools tools = new(harness);
        await harness.PhysicsFramesAsync(30);
        harness.StartRecording();
        await harness.PhysicsFramesAsync(20);

        Assert.False(await tools.StopAsync(), "stop from a native idle must be a no-op.");
        await harness.PhysicsFramesAsync(10);
        Assert.True(harness.Navigation.NativeIdleOwnsBody);
        Assert.Empty(harness.Transitions);

        Assert.True(await tools.WalkAsync(WalkMetres), tools.LastMessage);
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 240, "departure");
        IdleBridgePlan departure = Assert.NotNull(harness.Navigation.ActiveBridgePlan);
        // Stop during ordinary walking: after the departure bridge has ended, so the arrival interrupts no bridge.
        // (Stopping while the departure bridge still plays is a TR20 interruption and blends velocity.)
        await harness.WaitUntilAsync(() => harness.Navigation.Phase == "sustained-walk" && harness.Navigation.ActiveBridgePlan is null,
            600, "walking after the departure bridge");
        await harness.PhysicsFramesAsync(15);
        Assert.Null(harness.Navigation.ActiveBridgePlan);
        Vector3 stoppedAt = harness.Actor.GlobalPosition;

        Assert.True(await tools.StopAsync(), tools.LastMessage);
        IdleBridgePlan? arrival = null;
        await harness.WaitUntilAsync(() =>
        {
            arrival ??= harness.Transitions.Count == 2 ? harness.Navigation.ActiveBridgePlan : null;
            return harness.Navigation.NativeIdleOwnsBody && harness.Navigation.Phase == "settled-idle";
        }, 900, "stop into idle");
        await harness.PhysicsFramesAsync(60);
        harness.StopRecording();
        harness.Dump("agent_walk_stop");

        Assert.Equal("", harness.Navigation.Failure);
        Assert.Equal(new IdleBridgePlan(true, false, false, true, AnimationTransitionKind.IdleToLocomotion), departure);
        Assert.Equal(new IdleBridgePlan(false, true, false, false, AnimationTransitionKind.LocomotionToIdle), arrival);
        Assert.Equal([(AnimationTransitionKind.IdleToLocomotion, "feminine"), (AnimationTransitionKind.LocomotionToIdle, "sad")],
            harness.Transitions.Select(t => (t.Kind, t.Entry.ID)));
        Assert.Equal("sad", harness.Navigation.CurrentIdle?.ID);
        ObservedNavigationOutcome outcome = Assert.Single(tools.Outcomes);
        Assert.Equal(NavigationOutcomeKind.Stopped, outcome.Kind);
        float overrun = harness.Actor.GlobalPosition.DistanceTo(stoppedAt);
        float remaining = harness.Actor.GlobalPosition.DistanceTo(tools.Destination);
        NativeIdleTransitionTests.Note($"agent stop: settled {overrun:F3} m past the stop point, {remaining:F3} m short of the destination");
        Assert.True(remaining > .5f, $"The stopped walk still reached its destination ({remaining:F3} m away).");
        NativeIdleTransitionTests.AssertSeams(harness.Frames, expected: 2);
        NativeIdleTransitionTests.AssertUnmappedBlend(harness.Frames);
    }

    /// <summary>
    /// TR20–TR21: <c>stop</c> while the departure (idle→locomotion) bridge still plays is an interruption. The arrival
    /// bridge replaces the playing bridge from the realised pose with a velocity blend, without a pose pop or an
    /// acceleration kink, and settles into the preferred idle.
    /// </summary>
    [Fact]
    public async Task StopDuringDepartureBridge_ArrivalReplacesItWithVelocityBlend()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        using AgentTools tools = new(harness);
        await harness.PhysicsFramesAsync(30);
        harness.StartRecording();
        await harness.PhysicsFramesAsync(20);

        Assert.True(await tools.WalkAsync(WalkMetres), tools.LastMessage);
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 240, "departure");
        await harness.PhysicsFramesAsync(6);
        Assert.Equal(AnimationTransitionKind.IdleToLocomotion, harness.Navigation.ActiveBridgePlan?.Kind);

        long stopTick = (long)Engine.GetPhysicsFrames();
        Assert.True(await tools.StopAsync(), tools.LastMessage);
        IdleBridgePlan? arrival = null;
        await harness.WaitUntilAsync(() =>
        {
            arrival ??= harness.Transitions.Count == 2 ? harness.Navigation.ActiveBridgePlan : null;
            return harness.Navigation.NativeIdleOwnsBody && harness.Navigation.Phase == "settled-idle";
        }, 900, "stop into idle");
        await harness.PhysicsFramesAsync(60);
        harness.StopRecording();
        harness.Dump("agent_stop_during_departure");

        Assert.Equal("", harness.Navigation.Failure);
        Assert.Equal(new IdleBridgePlan(false, true, false, true, AnimationTransitionKind.LocomotionToIdle), arrival);
        Assert.Equal([(AnimationTransitionKind.IdleToLocomotion, "feminine"), (AnimationTransitionKind.LocomotionToIdle, "sad")],
            harness.Transitions.Select(t => (t.Kind, t.Entry.ID)));
        Assert.Equal("sad", harness.Navigation.CurrentIdle?.ID);
        ObservedNavigationOutcome outcome = Assert.Single(tools.Outcomes);
        Assert.Equal(NavigationOutcomeKind.Stopped, outcome.Kind);
        NativeIdleTransitionTests.AssertSeams(harness.Frames, expected: 2);

        IReadOnlyList<Frame> frames = harness.Frames;
        int install = frames.ToList().FindIndex(f => f.Tick > stopTick && f.Blend);
        Assert.True(install > 4, "No blended arrival install was observed.");
        double acceleration = Enumerable.Range(install, 8).Where(i => i < frames.Count)
            .Max(i => NativeIdleTransitionHarness.MaxAcceleration(frames[i - 4], frames[i - 2], frames[i]));
        NativeIdleTransitionTests.Note($"agent stop during departure: arrival install acceleration {acceleration:F3}°/frame²");
        Assert.True(acceleration <= NativeIdleTransitionTests.SeamAccelerationBound,
            $"The arrival replacing the departure bridge kinked by {acceleration:F3}°/frame².");
        NativeIdleTransitionTests.AssertUnmappedBlend(frames);
    }

    /// <summary>
    /// Mid idle→idle bridge, <c>stop</c> is a no-op and the bridge plays on; a <c>walk_to</c> then restarts from the
    /// realised bridge pose with a velocity blend into locomotion, without an acceleration kink, and the walk arrives
    /// into the preferred idle.
    /// </summary>
    [Fact]
    public async Task WalkToMidIdleBridge_RestartsFromRealisedPoseWithoutPop()
    {
        await using NativeIdleTransitionHarness harness = await NativeIdleTransitionHarness.StartAsync();
        await harness.WarmUpAsync();
        using AgentTools tools = new(harness);
        await harness.PhysicsFramesAsync(30);
        harness.StartRecording();
        await harness.PhysicsFramesAsync(20);

        Assert.True(harness.Navigation.SetIdlePreference("slump"));
        await harness.WaitUntilAsync(() => !harness.Navigation.NativeIdleOwnsBody, 120, "slump bridge");
        await harness.PhysicsFramesAsync(8);
        Assert.False(await tools.StopAsync(), "stop during an idle bridge must be a no-op.");
        await harness.PhysicsFramesAsync(8);
        Assert.False(harness.Navigation.NativeIdleOwnsBody, "The idle bridge ended before the walk request.");
        _ = Assert.Single(harness.Transitions);

        long walkTick = (long)Engine.GetPhysicsFrames();
        Assert.True(await tools.WalkAsync(1.6f), tools.LastMessage);
        await harness.WaitUntilAsync(() => harness.Transitions.Count == 2, 240, "departure from the bridge");
        IdleBridgePlan departure = Assert.NotNull(harness.Navigation.ActiveBridgePlan);
        await harness.WaitUntilAsync(() => harness.Navigation.NativeIdleOwnsBody && harness.Navigation.Phase == "settled-idle", 900, "arrival into idle");
        await harness.PhysicsFramesAsync(60);
        harness.StopRecording();
        harness.Dump("agent_walk_mid_bridge");

        Assert.Equal("", harness.Navigation.Failure);
        Assert.Equal(new IdleBridgePlan(false, false, false, true, AnimationTransitionKind.IdleToLocomotion), departure);
        Assert.Equal([(AnimationTransitionKind.IdleToIdle, "slump"), (AnimationTransitionKind.IdleToLocomotion, "feminine"),
            (AnimationTransitionKind.LocomotionToIdle, "slump")], harness.Transitions.Select(t => (t.Kind, t.Entry.ID)));
        Assert.Equal("slump", harness.Navigation.CurrentIdle?.ID);
        ObservedNavigationOutcome outcome = Assert.Single(tools.Outcomes);
        Assert.Equal(NavigationOutcomeKind.Arrived, outcome.Kind);
        NativeIdleTransitionTests.AssertSeams(harness.Frames, expected: 2);

        IReadOnlyList<Frame> frames = harness.Frames;
        int install = frames.ToList().FindIndex(f => f.Tick > walkTick && f.Blend);
        Assert.True(install > 4, "No blended departure install was observed.");
        double acceleration = Enumerable.Range(install, 8).Where(i => i < frames.Count)
            .Max(i => NativeIdleTransitionHarness.MaxAcceleration(frames[i - 4], frames[i - 2], frames[i]));
        NativeIdleTransitionTests.Note($"agent walk mid-bridge: departure install acceleration {acceleration:F3}°/frame²");
        Assert.True(acceleration <= NativeIdleTransitionTests.SeamAccelerationBound,
            $"Departure from the bridge kinked by {acceleration:F3}°/frame².");
        NativeIdleTransitionTests.AssertUnmappedBlend(frames);
    }

    /// <summary>The production tool functions and controller bound to the harness NPC's navigation.</summary>
    private sealed class AgentTools : IDisposable
    {
        private readonly NativeIdleTransitionHarness _harness;
        private readonly CoordinateNavigationController _controller;
        private readonly CoordinateNavigationToolFunctions _functions;

        public AgentTools(NativeIdleTransitionHarness harness)
        {
            _harness = harness;
            _controller = new CoordinateNavigationController(
                harness.Navigation,
                new GodotCoordinateNavigationEnvironment(harness.Actor, harness.Navigation),
                Outcomes.Add);
            _functions = new CoordinateNavigationToolFunctions(_controller);
            // AgenticMind polls from its frame processing; poll every physics tick here.
            harness.Skeleton.SkeletonUpdated += _controller.Poll;
        }

        public List<ObservedNavigationOutcome> Outcomes { get; } = [];

        public string LastMessage { get; private set; } = "";

        public Vector3 Destination
        {
            get; private set;
        }

        /// <summary>Calls <c>walk_to</c> for a spot straight ahead (−Z) of the actor.</summary>
        public async Task<bool> WalkAsync(float metres)
        {
            Vector3 start = _harness.Actor.GlobalPosition;
            Destination = start + new Vector3(0, 0, -metres);
            int pending = _controller.PendingOutcomeCount;
            LastMessage = (await _functions.WalkTo(Destination.X, Destination.Z)).Message ?? "";
            return _controller.PendingOutcomeCount > pending;
        }

        /// <summary>Calls <c>stop</c>; true when it stopped a move in progress.</summary>
        public async Task<bool> StopAsync()
        {
            LastMessage = (await _functions.Stop()).Message ?? "";
            return LastMessage.StartsWith("Stopped", StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (GodotObject.IsInstanceValid(_harness.Skeleton))
            {
                _harness.Skeleton.SkeletonUpdated -= _controller.Poll;
            }
        }
    }
}
