using AlleyCat.Animation;
using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier.Pose;

public readonly record struct StandingPoseContext(Vector3 HipsOrigin);

public class CrouchingPoseIk(
    IRig<HumanBone> rig,
    AnimationTree animationTree,
    AnimationNodeStateMachinePlayback rootPlayback,
    AnimationNodeStateMachinePlayback idlePlayback,
    AnimationParam seekParameter,
    AnimationStateName idleState,
    AnimationStateName standingState,
    AnimationStateName crouchingState,
    IObservable<Duration> onIkProcess,
    ILoggerFactory? loggerFactory = null
) : ContextAwareIkModifier<HumanBone, StandingPoseContext>(rig, onIkProcess, loggerFactory)
{
    private const float StandingThreshold = 0.9f;

    protected override Eff<IEnv, StandingPoseContext> CreateContext() =>
        from hips in Rig.GetRest(HumanBone.Hips)
        select new StandingPoseContext(hips.Origin);

    private bool IsValidState(string state) => state == standingState || state == crouchingState;

    protected override Eff<IEnv, Unit> Process(StandingPoseContext context, Duration delta) =>
        from rootState in IO.lift(rootPlayback.GetCurrentNode)
        from state in IO.lift(idlePlayback.GetCurrentNode)
        from _ in rootState == idleState &&
                  IsValidState(state)
            ? ProcessIdle(state, context, delta)
            : unitEff
        select unit;

    private Eff<IEnv, Unit> ProcessIdle(
        string animState,
        StandingPoseContext context,
        Duration delta
    ) =>
        from toSkeleton in Rig.GlobalTransform
        let fromSkeleton = toSkeleton.Inverse()
        from hipsOrigin in Rig.GetPose(HumanBone.Hips).Map(x => x.Origin)
        let minY = 0.4f
        let ratio = (hipsOrigin.Y - minY) / (context.HipsOrigin.Y - minY)
        let param = Mathf.Clamp(ratio, 0.0f, 1.0f)
        from _ in IO.lift(() =>
        {
            if (param >= StandingThreshold)
            {
                if (animState != standingState) idlePlayback.Travel(standingState);
            }
            else
            {
                if (animState != crouchingState) idlePlayback.Travel(crouchingState);
            }

            var seek = 1 - param;

            animationTree.Set(seekParameter, seek);
        })
        select unit;
}