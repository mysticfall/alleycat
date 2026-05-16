using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Drives IK target <see cref="CharacterBody3D"/> nodes using velocity + <see cref="CharacterBody3D.MoveAndSlide"/>.
/// </summary>
public sealed class IKTargetBodyActuator(CharacterBody3D body) : IIKTargetActuator
{
    private const float DeltaEpsilon = 1e-6f;
    private const float DefaultRotationSnapAngleRadians = 0.01f;

    /// <summary>
    /// When enabled, uses the tuned damped/smoothed hand-follow path. When disabled, preserves the pre-tuning baseline
    /// snap/catch-up behaviour.
    /// </summary>
    public bool UseDampedFollow
    {
        get;
        set;
    }

    /// <summary>
    /// Maximum translation speed in metres per second.
    /// </summary>
    public float MaximumSpeed
    {
        get;
        set;
    } = 24.0f;

    /// <summary>
    /// Position-error gain used to convert displacement into target velocity.
    /// </summary>
    public float PositionResponsiveness
    {
        get;
        set;
    } = 60.0f;

    /// <summary>
    /// Maximum rate of velocity change in metres per second squared.
    /// </summary>
    public float MaximumAcceleration
    {
        get;
        set;
    } = 4096.0f;

    /// <summary>
    /// Rotation-error gain used to ease the actuator basis towards the target basis.
    /// </summary>
    public float RotationResponsiveness
    {
        get;
        set;
    } = 1000.0f;

    /// <summary>
    /// Angular threshold below which the actuator snaps directly to the target basis.
    /// </summary>
    public float RotationSnapAngleRadians
    {
        get;
        set;
    } = DefaultRotationSnapAngleRadians;

    /// <summary>
    /// Distance below which the actuator switches to a fine physics-driven settle step instead of a higher-speed catch-up
    /// move.
    /// </summary>
    public float SnapDistance
    {
        get;
        set;
    } = 0.0025f;

    /// <inheritdoc />
    public IKTargetActuationResult Actuate(IKTargetPipelineRequest request, double delta)
    {
        float deltaSeconds = (float)delta;
        if (deltaSeconds <= DeltaEpsilon)
        {
            return IKTargetActuationResult.Inactive(body.GlobalTransform, "InvalidDelta");
        }

        IKTargetFollowState targetState = request.RequestedFollowState;
        Transform3D targetTransform = targetState.WorldTransform;
        if (!targetState.Active)
        {
            body.Velocity = Vector3.Zero;
            return new IKTargetActuationResult(
                targetTransform,
                body.GlobalTransform,
                IKTargetPipelineFeedback.FromTargets(targetTransform, body.GlobalTransform, "Inactive"));
        }

        return UseDampedFollow
            ? FollowDamped(deltaSeconds, targetTransform)
            : FollowBaseline(deltaSeconds, targetTransform);
    }

    private IKTargetActuationResult FollowBaseline(float deltaSeconds, Transform3D targetTransform)
    {
        Vector3 displacement = targetTransform.Origin - body.GlobalPosition;
        float snapDistanceSquared = SnapDistance * SnapDistance;
        if (displacement.LengthSquared() <= snapDistanceSquared)
        {
            SetWorldTransform(body, targetTransform);
            body.Velocity = Vector3.Zero;
            return new IKTargetActuationResult(targetTransform, body.GlobalTransform, IKTargetPipelineFeedback.None);
        }

        Vector3 desiredVelocity = displacement / deltaSeconds;
        float maxSpeed = Mathf.Max(MaximumSpeed, 0.0f);
        body.Velocity = maxSpeed <= DeltaEpsilon
            ? Vector3.Zero
            : desiredVelocity.LengthSquared() > (maxSpeed * maxSpeed)
                ? desiredVelocity.Normalized() * maxSpeed
                : desiredVelocity;

        _ = body.MoveAndSlide();
        SetWorldTransform(body, new Transform3D(targetTransform.Basis, body.GlobalPosition));
        return new IKTargetActuationResult(
            targetTransform,
            body.GlobalTransform,
            IKTargetPipelineFeedback.FromTargets(targetTransform, body.GlobalTransform, "None"));
    }

    private static void SetWorldTransform(Node3D node, Transform3D worldTransform)
    {
        Transform3D orthonormalWorldTransform = new(worldTransform.Basis.Orthonormalized(), worldTransform.Origin);
        node.GlobalTransform = orthonormalWorldTransform;
        node.Transform = node.GetParent() is Node3D parent
            ? parent.GlobalTransform.AffineInverse() * orthonormalWorldTransform
            : orthonormalWorldTransform;
        if (node.IsInsideTree())
        {
            node.ForceUpdateTransform();
        }
    }

    private IKTargetActuationResult FollowDamped(float deltaSeconds, Transform3D targetTransform)
    {
        Vector3 displacement = targetTransform.Origin - body.GlobalPosition;
        Vector3 desiredVelocity = IKTargetBodyActuatorMath.ComputeDesiredVelocity(
            displacement,
            MaximumSpeed,
            PositionResponsiveness,
            SnapDistance);

        body.Velocity = IKTargetBodyActuatorMath.ComputeFollowVelocity(
            body.Velocity,
            desiredVelocity,
            deltaSeconds,
            MaximumAcceleration);

        _ = body.MoveAndSlide();
        Basis followBasis = IKTargetBodyActuatorMath.ComputeFollowBasis(
            body.GlobalBasis,
            targetTransform.Basis,
            deltaSeconds,
            RotationResponsiveness,
            RotationSnapAngleRadians);
        SetWorldTransform(body, new Transform3D(followBasis, body.GlobalPosition));
        return new IKTargetActuationResult(
            targetTransform,
            body.GlobalTransform,
            IKTargetPipelineFeedback.FromTargets(targetTransform, body.GlobalTransform, "None"));
    }
}

/// <summary>
/// Shared math helpers for damped IK-target actuator motion.
/// </summary>
public static class IKTargetBodyActuatorMath
{
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// Converts displacement into a capped desired velocity with reduced settle gain near the target.
    /// </summary>
    public static Vector3 ComputeDesiredVelocity(
        Vector3 displacement,
        float maximumSpeed,
        float positionResponsiveness,
        float settleDistance)
    {
        float gain = Mathf.Max(positionResponsiveness, 0.0f);
        float maxSpeed = Mathf.Max(maximumSpeed, 0.0f);
        if (gain <= Epsilon || maxSpeed <= Epsilon)
        {
            return Vector3.Zero;
        }

        float distance = displacement.Length();
        if (distance <= Epsilon)
        {
            return Vector3.Zero;
        }

        float settleFactor = 1.0f;
        float safeSettleDistance = Mathf.Max(settleDistance, 0.0f);
        if (safeSettleDistance > Epsilon && distance < safeSettleDistance)
        {
            settleFactor = distance / safeSettleDistance;
        }

        Vector3 desiredVelocity = displacement * gain * settleFactor;
        return ClampMagnitude(desiredVelocity, maxSpeed);
    }

    /// <summary>
    /// Applies acceleration limiting while steering the current velocity towards the desired velocity.
    /// </summary>
    public static Vector3 ComputeFollowVelocity(
        Vector3 currentVelocity,
        Vector3 desiredVelocity,
        float deltaSeconds,
        float maximumAcceleration)
    {
        float safeDeltaSeconds = Mathf.Max(deltaSeconds, 0.0f);
        if (safeDeltaSeconds <= Epsilon)
        {
            return currentVelocity;
        }

        float maxAcceleration = Mathf.Max(maximumAcceleration, 0.0f);
        if (maxAcceleration <= Epsilon)
        {
            return desiredVelocity;
        }

        float maxSpeedDelta = maxAcceleration * safeDeltaSeconds;
        return MoveTowards(currentVelocity, desiredVelocity, maxSpeedDelta);
    }

    /// <summary>
    /// Smooths actuator rotation towards the target basis while preserving orthonormality.
    /// </summary>
    public static Basis ComputeFollowBasis(
        Basis currentBasis,
        Basis targetBasis,
        float deltaSeconds,
        float rotationResponsiveness,
        float rotationSnapAngleRadians)
    {
        Basis currentOrthonormal = currentBasis.Orthonormalized();
        Basis targetOrthonormal = targetBasis.Orthonormalized();

        float safeDeltaSeconds = Mathf.Max(deltaSeconds, 0.0f);
        float responsiveness = Mathf.Max(rotationResponsiveness, 0.0f);
        if (safeDeltaSeconds <= Epsilon || responsiveness <= Epsilon)
        {
            return targetOrthonormal;
        }

        Quaternion currentRotation = new(currentOrthonormal);
        Quaternion targetRotation = new(targetOrthonormal);
        float snapAngle = Mathf.Max(rotationSnapAngleRadians, 0.0f);
        float angle = currentRotation.AngleTo(targetRotation);
        if (angle <= snapAngle)
        {
            return targetOrthonormal;
        }

        float weight = 1.0f - Mathf.Exp(-responsiveness * safeDeltaSeconds);
        Quaternion blendedRotation = currentRotation.Slerp(targetRotation, weight);
        return new Basis(blendedRotation).Orthonormalized();
    }

    private static Vector3 MoveTowards(Vector3 from, Vector3 to, float delta)
    {
        Vector3 difference = to - from;
        float distance = difference.Length();
        float maximumStepDistance = Mathf.Max(delta, Epsilon);
        if (distance <= maximumStepDistance)
        {
            return to;
        }

        Vector3 step = difference / distance * delta;
        return from + step;
    }

    private static Vector3 ClampMagnitude(Vector3 value, float maximumLength)
    {
        float maxLengthSquared = maximumLength * maximumLength;
        return value.LengthSquared() > maxLengthSquared
            ? value.Normalized() * maximumLength
            : value;
    }
}
