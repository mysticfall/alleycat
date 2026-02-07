using AlleyCat.Common;
using Godot;
using LanguageExt;

namespace AlleyCat.Physics;

public class PhysicalTracker(
    CharacterBody3D body,
    Node3D target,
    float returnVelocityRatio = 0.2f
)
{
    public CharacterBody3D Body => body;

    public Node3D Target => target;

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

    public IO<Vector3> Process(Vector3 lastTargetPos, Duration duration) => IO.lift(() =>
    {
        var bodyTrans = body.GlobalTransform;
        var targetTrans = target.GlobalTransform;

        body.Velocity = CalculateVelocity(
            bodyTrans.Origin,
            targetTrans.Origin,
            lastTargetPos,
            duration
        );

        body.GlobalBasis = target.GlobalBasis;
        body.MoveAndSlide();

        return targetTrans.Origin;
    });
}