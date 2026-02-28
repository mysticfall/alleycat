using AlleyCat.Common;
using Godot;
using LanguageExt;

namespace AlleyCat.Locomotion.Rotation;

public class SmoothTurnCalculator(
    AngularVelocity maxTurnRate
) : IRotationCalculator
{
    public IO<Quaternion> CalculateRotation(Vector2 input, Duration duration)
    {
        var angle = -maxTurnRate.Radians * input.X * (float)duration.Seconds;
        var rotation = new Vector3(0, angle, 0);

        return IO.pure(Quaternion.FromEuler(rotation));
    }
}