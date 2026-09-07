using AlleyCat.Control.Locomotion;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Control;

/// <summary>CTRL-001 hard-replacement coverage against the declared synthetic ember locomotion package.</summary>
public sealed class PlayerLocomotionHardReplacementIntegrationTests
{
    [Fact]
    public async Task SyntheticPlayer_ReleaseWithoutACurrentOccurrence_LeavesTravelActive()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerLocomotionScenarioRunner runner = await PlayerLocomotionScenarioRunner.CreateAsync(sceneTree);
        try
        {
            PlayerLocomotionBoutRecord bout = runner.RunBout(new(
                "expired-occurrence",
                new Vector2(0f, 1f),
                PlayerLocomotionReleaseSchedule.OneTickAfterEvaluatedTravelWrap(),
                PostReleaseObservationSeconds: 0.25f));

            Assert.DoesNotContain(bout.NodeTimeline, segment => SyntheticPlayerLocomotionRuntime.FiniteExitStates.Contains(segment.NodeName));
            Assert.Null(runner.LastTravelReleaseOccurrence);
        }
        finally
        {
            runner.Dispose();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    [Fact]
    public async Task SyntheticPlayer_MissingDeclaredCatalogue_FailsClosedWithoutStaleExit()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerLocomotionScenarioRunner runner = await PlayerLocomotionScenarioRunner.CreateAsync(sceneTree);
        try
        {
            runner.ConfigureCataloguePath("user://synthetic-locomotion-runtime/missing-catalogue.json");
            PlayerLocomotionBoutRecord bout = runner.RunBout(new(
                "missing-declaration",
                new Vector2(0f, 1f),
                PlayerLocomotionReleaseSchedule.AtTravelPhase(0.25f),
                PostReleaseObservationSeconds: 1f));

            Assert.Null(runner.LastTravelReleaseOccurrence);
            Assert.DoesNotContain(bout.NodeTimeline, segment => SyntheticPlayerLocomotionRuntime.FiniteExitStates.Contains(segment.NodeName));
        }
        finally
        {
            runner.Dispose();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    [Fact]
    public async Task SyntheticPlayer_DeclaredStateTopology_RoutesLifecycleAndSchedulesTracks()
    {
        SceneTree sceneTree = GetSceneTree();
        using var fixture = SyntheticLocomotionRuntimeFixture.Create("hard-replacement-topology");
        SyntheticPlayerLocomotionRuntime runtime = fixture.CreatePlayerLocomotionRuntime();
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, fixture.Root);
        await WaitForFramesAsync(sceneTree, 2);

        AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(runtime.AnimationTree.TreeRoot, exactMatch: false);
        AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(root.GetNode("States"), exactMatch: false);
        try
        {
            Assert.All(SyntheticPlayerLocomotionRuntime.FiniteExitStates, state => Assert.NotNull(states.GetNode(state)));
            AssertTransition(states, SyntheticPlayerLocomotionRuntime.EntryForwardLeftState, SyntheticPlayerLocomotionRuntime.TravelState);
            AssertTransition(states, SyntheticPlayerLocomotionRuntime.TravelState, SyntheticPlayerLocomotionRuntime.ExitForwardLeftState);
            AssertTransition(states, SyntheticPlayerLocomotionRuntime.ExitForwardLeftState, SyntheticPlayerLocomotionRuntime.IdleState);

            AnimationNodeBlendTree travel = Assert.IsType<AnimationNodeBlendTree>(
                states.GetNode(SyntheticPlayerLocomotionRuntime.TravelState), exactMatch: false);
            AnimationNodeBlendSpace2D forward = Assert.IsType<AnimationNodeBlendSpace2D>(travel.GetNode("ForwardArcL"), exactMatch: false);
            AnimationNodeAnimation stride = Assert.IsType<AnimationNodeAnimation>(forward.GetBlendPointNode(0), exactMatch: false);
            Assert.Equal(new StringName("locomotion/ember_token_0b"), stride.Animation);
            Assert.Equal(fixture.Declaration.NavigationManifest["wf"].DurationSeconds, (float)forward.CyclicLength);
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    [Fact]
    public async Task SyntheticPlayer_RootAndResidualMotionAreConsumedOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        using var fixture = SyntheticLocomotionRuntimeFixture.Create("root-consumption");
        SyntheticPlayerLocomotionRuntime runtime = fixture.CreatePlayerLocomotionRuntime();
        var locomotion = new RootProbeLocomotion
        {
            Name = "RootProbe",
            TargetCharacterBodyNode = runtime.Actor,
            AnimationTree = runtime.AnimationTree,
            RootMotionReference = runtime.RootMotionReference,
            RootDelta = new Vector3(0f, 0f, -0.01f),
            RootYaw = 0.1f,
            ResidualSmoothingRate = 100f,
            ResidualEntryRampReference = 0.01f,
        };
        runtime.Locomotion.SetPhysicsProcess(false);
        runtime.Actor.AddChild(locomotion);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, fixture.Root);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            locomotion.SetPhysicsProcess(false);
            runtime.Locomotion.SetPhysicsProcess(false);
            int yawApplicationsBeforeStep = locomotion.YawApplicationCount;
            float yawBeforeStep = locomotion.TotalYaw;
            AnimationNodeStateMachinePlayback playback = runtime.AnimationTree.Get("parameters/States/playback")
                .As<AnimationNodeStateMachinePlayback>()!;
            playback.Start(SyntheticPlayerLocomotionRuntime.TravelState, reset: true);
            runtime.AnimationTree.Advance(0d);
            locomotion.Move(new Vector2(0f, 1f));
            locomotion.Rotate(new Vector2(1f, 0f));
            locomotion._PhysicsProcess(0.1d);

            Assert.InRange(runtime.Actor.Velocity.Z, -0.276f, -0.274f);
            Assert.Equal(yawApplicationsBeforeStep + 1, locomotion.YawApplicationCount);
            Assert.InRange(locomotion.TotalYaw - yawBeforeStep, 0.064f, 0.066f);
        }
        finally
        {
            fixture.Root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static void AssertTransition(AnimationNodeStateMachine states, string from, string to)
    {
        for (int index = 0; index < states.GetTransitionCount(); index++)
        {
            if (states.GetTransitionFrom(index) == from && states.GetTransitionTo(index) == to)
            {
                AnimationNodeStateMachineTransition transition = states.GetTransition(index);
                Assert.Equal(AnimationNodeStateMachineTransition.AdvanceModeEnum.Enabled, transition.AdvanceMode);
                Assert.Equal(0.05f, transition.XfadeTime);
                return;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected declared transition '{from}' → '{to}'.");
    }

    private sealed partial class RootProbeLocomotion : CharacterLocomotion
    {
        public Vector3 RootDelta
        {
            get; set;
        }
        public float RootYaw
        {
            get; set;
        }
        public int YawApplicationCount
        {
            get; private set;
        }
        public float TotalYaw
        {
            get; private set;
        }

        protected override Vector3 GetRootMotionPositionDelta() => RootDelta;
        protected override float GetRootMotionYawDelta() => RootYaw;

        protected override void ApplyYawRotation(float yawDelta)
        {
            YawApplicationCount++;
            TotalYaw += yawDelta;
        }
    }
}
