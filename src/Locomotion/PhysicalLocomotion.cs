using AlleyCat.Locomotion.Rotation;
using AlleyCat.Locomotion.Velocity;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Locomotion;

public class PhysicalLocomotion(
    CharacterBody3D body,
    Vector3 gravity,
    IVelocityCalculator velocityCalculator,
    IRotationCalculator rotationCalculator,
    IObservable<Duration> onProcess,
    ILoggerFactory? loggerFactory = null
) : Locomotion(velocityCalculator, rotationCalculator, onProcess, loggerFactory)
{
    public override IO<bool> IsMoving { get; } = IO.lift(() => !body.Velocity.IsZeroApprox());

    protected override IO<Unit> Process(
        Vector3 velocity,
        Quaternion rotation,
        Duration duration
    ) => IO.lift(() =>
    {
        body.Basis = body.Basis.Rotated(rotation.GetAxis(), rotation.GetAngle());
        body.Velocity = body.GlobalBasis * velocity + gravity;

        body.MoveAndSlide();
    });
}