using System.Reflection;
using AlleyCat.Control;
using AlleyCat.IK.Pose;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for pose-state-machine locomotion animation-source delegation.
/// </summary>
public sealed partial class PoseStateMachineLocomotionSourceIntegrationTests
{
    /// <summary>
    /// Verifies the state machine delegates the optional locomotion animation target from the active pose state.
    /// </summary>
    [Headless]
    [Fact]
    public void PoseStateMachine_ActivePose_DelegatesLocomotionStateTarget()
    {
        StubLocomotionAnimationPoseState state = new();
        PoseStateMachine stateMachine = new()
        {
            States = [state],
            InitialStateId = state.Id,
            Active = true,
        };

        stateMachine.EnsureInitialStateResolved();
        Assert.Null(((ILocomotionAnimationSource)stateMachine).LocomotionStateTarget);

        _ = stateMachine.Tick(new PoseStateContext
        {
            Delta = 1.0,
        });

        LocomotionStateTarget? target =
            ((ILocomotionAnimationSource)stateMachine).LocomotionStateTarget;

        Assert.True(target.HasValue);
        Assert.Equal("AllFours", target.Value.IdleStateName.ToString());
        Assert.Equal("AllFoursForward", target.Value.MovementStateName.ToString());
    }

    /// <summary>
    /// Verifies the all-fours crawl hold exposes locomotion, while the transitioning phase keeps it disabled.
    /// </summary>
    [Headless]
    [Fact]
    public void AllFoursPoseState_Phases_ExposeLocomotionTargetOnlyWhileCrawling()
    {
        AllFoursPoseState state = new();
        PoseStateContext context = new();

        SetCurrentPhase(state, AllFoursPoseState.Phase.Transitioning);
        Assert.Null(state.GetLocomotionStateTarget(context));
        Assert.Equal(LocomotionPermissions.RotationOnly, state.GetLocomotionPermissions(context));

        SetCurrentPhase(state, AllFoursPoseState.Phase.Crawling);
        LocomotionStateTarget? target = state.GetLocomotionStateTarget(context);

        Assert.True(target.HasValue);
        Assert.Equal("AllFours", target.Value.IdleStateName.ToString());
        Assert.Equal("AllFoursForward", target.Value.MovementStateName.ToString());
        Assert.Equal(LocomotionPermissions.Allowed, state.GetLocomotionPermissions(context));
    }

    /// <summary>
    /// Verifies the crawl-entry gate is driven by forward travel even when the runtime head height
    /// remains above the vertical-return threshold used for crawl exit.
    /// </summary>
    [Headless]
    [Fact]
    public void AllFoursPoseState_RuntimeLikeHeadHeight_StillEntersCrawling()
    {
        AllFoursPoseState state = new();
        Skeleton3D skeleton = new()
        {
            GlobalTransform = Transform3D.Identity,
        };

        PoseStateContext enterContext = new();
        state.OnEnter(enterContext);

        PoseStateContext updateContext = new()
        {
            Skeleton = skeleton,
            HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0.95f, 0.80f)),
            RestHeadHeight = 1.0f,
            Delta = 0.016,
        };

        state.OnUpdate(updateContext);

        Assert.Equal(AllFoursPoseState.Phase.Crawling, state.CurrentPhase);
        Assert.Equal(LocomotionPermissions.Allowed, state.GetLocomotionPermissions(updateContext));
        Assert.True(state.GetLocomotionStateTarget(updateContext).HasValue);
    }

    /// <summary>
    /// Verifies crawl-hold locomotion stays active across repeated identical runtime-like ticks.
    /// </summary>
    [Headless]
    [Fact]
    public void AllFoursPoseState_RuntimeLikeHeadHeight_RemainsCrawlingAcrossIdenticalUpdates()
    {
        AllFoursPoseState state = new();
        Skeleton3D skeleton = new()
        {
            GlobalTransform = Transform3D.Identity,
        };

        PoseStateContext updateContext = new()
        {
            Skeleton = skeleton,
            HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0.95f, 0.80f)),
            RestHeadHeight = 1.0f,
            Delta = 0.016,
        };

        state.OnEnter(new PoseStateContext());
        state.OnUpdate(updateContext);
        Assert.Equal(AllFoursPoseState.Phase.Crawling, state.CurrentPhase);

        state.OnUpdate(updateContext);

        Assert.Equal(AllFoursPoseState.Phase.Crawling, state.CurrentPhase);
        Assert.Equal(LocomotionPermissions.Allowed, state.GetLocomotionPermissions(updateContext));

        LocomotionStateTarget? target = state.GetLocomotionStateTarget(updateContext);
        Assert.True(target.HasValue);
        Assert.Equal("AllFours", target.Value.IdleStateName.ToString());
        Assert.Equal("AllFoursForward", target.Value.MovementStateName.ToString());
    }

    /// <summary>
    /// Verifies the animation debug message exposes the crawl gate and whether locomotion remains blocked.
    /// </summary>
    [Headless]
    [Fact]
    public void AllFoursPoseState_DebugMessage_ReportsThresholdAndMovementGate()
    {
        AllFoursPoseState state = new();
        PoseStateContext context = new()
        {
            Skeleton = new Skeleton3D
            {
                GlobalTransform = Transform3D.Identity,
            },
            HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0.95f, 0.60f)),
            RestHeadHeight = 1.0f,
        };

        state.OnEnter(new PoseStateContext());

        string? message = state.BuildAnimationDebugMessage(context);

        Assert.NotNull(message);
        Assert.Contains("AllFours: Transitioning", message, StringComparison.Ordinal);
        Assert.Contains("crawl_threshold=0.730", message, StringComparison.Ordinal);
        Assert.Contains("movement=blocked", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the pose-state-machine keeps delegating crawl locomotion permissions and targets on
    /// repeated identical runtime-like ticks.
    /// </summary>
    [Headless]
    [Fact]
    public void PoseStateMachine_AllFoursRuntimeLikePose_KeepsCrawlLocomotionActiveAcrossTicks()
    {
        AllFoursPoseState state = new();
        PoseStateMachine stateMachine = new()
        {
            States = [state],
            InitialStateId = state.Id,
            Active = true,
        };

        stateMachine.EnsureInitialStateResolved();

        PoseStateContext context = new()
        {
            Skeleton = new Skeleton3D
            {
                GlobalTransform = Transform3D.Identity,
            },
            HeadTargetTransform = new Transform3D(Basis.Identity, new Vector3(0f, 0.95f, 0.80f)),
            RestHeadHeight = 1.0f,
            Delta = 0.016,
        };

        _ = stateMachine.Tick(context);
        _ = stateMachine.Tick(context);

        Assert.Equal(LocomotionPermissions.Allowed, stateMachine.LocomotionPermissions);

        LocomotionStateTarget? target = ((ILocomotionAnimationSource)stateMachine).LocomotionStateTarget;
        Assert.True(target.HasValue);
        Assert.Equal("AllFours", target.Value.IdleStateName.ToString());
        Assert.Equal("AllFoursForward", target.Value.MovementStateName.ToString());
    }

    private sealed partial class StubLocomotionAnimationPoseState : PoseState
    {
        public StubLocomotionAnimationPoseState()
        {
            Id = new StringName("Stub");
        }

        public override LocomotionPermissions GetLocomotionPermissions(PoseStateContext context)
            => LocomotionPermissions.Allowed;

        public override LocomotionStateTarget? GetLocomotionStateTarget(PoseStateContext context)
            => context.Delta > 0.5
                ? new LocomotionStateTarget(
                    new StringName("AllFours"),
                    new StringName("AllFoursForward"))
                : null;
    }

    private static void SetCurrentPhase(AllFoursPoseState state, AllFoursPoseState.Phase phase)
    {
        PropertyInfo phaseProperty = typeof(AllFoursPoseState).GetProperty(nameof(AllFoursPoseState.CurrentPhase))
            ?? throw new Xunit.Sdk.XunitException("Expected CurrentPhase property to exist.");
        phaseProperty.SetValue(state, phase);
    }
}
