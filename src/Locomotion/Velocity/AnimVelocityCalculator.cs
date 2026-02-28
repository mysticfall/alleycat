using System.Reactive.Linq;
using AlleyCat.Animation;
using AlleyCat.Common;
using Godot;

namespace AlleyCat.Locomotion.Velocity;

public class AnimVelocityCalculator(
    AnimationTree animationTree,
    AnimationNodeStateMachinePlayback playback,
    AnimationParam parameter,
    AnimationStateName idleState,
    AnimationStateName movingState
) : IVelocityCalculator
{
    public IObservable<Vector3> ObserveRequests(IObservable<MoveRequest> request) =>
        request.Select(x =>
        {
            var currentState = playback.GetCurrentNode();

            var hasInput = !Mathf.IsEqualApprox(x.Input.Y, 0);

            switch (hasInput)
            {
                case true when currentState == idleState:
                    playback.Travel(movingState);
                    break;
                case false when currentState == movingState:
                    playback.Travel(idleState);
                    break;
            }

            animationTree.Set(parameter, x.Input.Y);

            var timeDelta = (float)x.TimeDelta.Seconds;

            return -animationTree.GetRootMotionPosition() / timeDelta;
        });
}