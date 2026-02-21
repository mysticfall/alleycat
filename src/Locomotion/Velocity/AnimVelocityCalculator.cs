using AlleyCat.Animation;
using AlleyCat.Common;
using Godot;
using LanguageExt;

namespace AlleyCat.Locomotion.Velocity;

public class AnimVelocityCalculator(
    AnimationTree animationTree,
    AnimationNodeStateMachinePlayback playback,
    AnimationParam parameter,
    AnimationStateName idleState,
    AnimationStateName movingState
) : IVelocityCalculator
{
    public IO<Vector3> CalculateVelocity(Vector2 input, Duration duration) => IO.lift(() =>
    {
        var currentState = playback.GetCurrentNode();

        var hasInput = !Mathf.IsEqualApprox(input.Y, 0);

        switch (hasInput)
        {
            case true when currentState == idleState:
                playback.Travel(movingState);
                break;
            case false when currentState == movingState:
                playback.Travel(idleState);
                break;
        }

        animationTree.Set(parameter, input.Y);

        return -animationTree.GetRootMotionPosition() / (float)duration.Seconds;
    });
}