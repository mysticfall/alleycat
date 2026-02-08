using AlleyCat.Common;
using Godot;
using LanguageExt;

namespace AlleyCat.Physics;

public class PhysicalTracker(
    CharacterBody3D body,
    IO<Transform3D> target,
    float returnVelocityRatio = 0.2f
)
{
    public PhysicalTracker(
        CharacterBody3D body,
        Node3D target,
        float returnVelocityRatio = 0.2f
    ) : this(body, IO.lift(() => target.GlobalTransform), returnVelocityRatio)
    {
    }

    private readonly NormalisedRatio _returnVelocityRatio = NormalisedRatio.Coerce(returnVelocityRatio);

    private Vector3 CalculateVelocity(
        Vector3 bodyPos,
        Vector3 targetPos,
        Vector3 lastTargetPos,
        Duration duration
    )
    {
        var targetOffset = targetPos - lastTargetPos;

        var deltaInSeconds = (float)duration.Milliseconds / 1000;

        var speed = targetOffset.Length();

        var toTarget = (targetPos - bodyPos).Normalized();
        var toDest = targetOffset.Normalized();

        return (toDest * (1 - _returnVelocityRatio) + toTarget * _returnVelocityRatio) * speed / deltaInSeconds;
    }

    public IO<Transform3D> Initialise() =>
        from transform in target
        from _ in IO.lift(() =>
        {
            body.GlobalTransform = transform;
            body.Velocity = Vector3.Zero;
        })
        select transform;

    public IO<Vector3> Process(Vector3 lastTargetPos, Duration duration) =>
        from transform in target
        from position in IO.lift(() =>
        {
            var bodyTrans = body.GlobalTransform;

            body.Velocity = CalculateVelocity(
                bodyTrans.Origin,
                transform.Origin,
                lastTargetPos,
                duration
            );
            
            body.GlobalBasis = transform.Basis;
            body.MoveAndSlide();

            return transform.Origin;
        })
        select position;
}