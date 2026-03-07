using AlleyCat.Animation;
using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier.Pose;

public readonly record struct StandingPoseContext(Vector3 HeadOrigin);

public class CrouchingPoseIk(
    ILocatable3d headTarget,
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
        from head in Rig.GetRest(HumanBone.Head)
        select new StandingPoseContext(head.Origin);

    private bool IsValidState(string state) => state == standingState || state == crouchingState;

    protected override Eff<IEnv, Unit> Process(StandingPoseContext context, Duration delta) =>
        from rootState in IO.lift(rootPlayback.GetCurrentNode)
        from state in IO.lift(idlePlayback.GetCurrentNode)
        from _ in rootState == idleState &&
                  IsValidState(state)
            ? ProcessIdle(state, context, delta)
            : unitEff
        select unit;

    protected Eff<IEnv, Unit> ProcessIdle(
        string animState,
        StandingPoseContext context,
        Duration delta
    ) =>
        from toSkeleton in Rig.GlobalTransform
        let fromSkeleton = toSkeleton.Inverse()
        from headTarget in headTarget.GlobalTransform
        let targetOrigin = (fromSkeleton * headTarget).Origin
        let maxV = 1.0f
        let minV = 0.5f
        let ratio = targetOrigin.Y / context.HeadOrigin.Y
        let param = Mathf.Clamp((ratio - minV) / (maxV - minV), 0.0f, 1.0f)
        from _ in IO.lift(() =>
        {
            if (param >= StandingThreshold && animState != standingState)
            {
                idlePlayback.Travel(standingState);
            }

            if (param < StandingThreshold)
            {
                if (animState != crouchingState)
                {
                    idlePlayback.Travel(crouchingState);
                }

                var seek = (1 - param) * 1.6f;

                animationTree.Set(seekParameter, seek);
            }
        })
        select unit;
}