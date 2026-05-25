using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Drives hand IK target <see cref="AnimatableBody3D"/> nodes using collision-aware kinematic motion.
/// </summary>
public sealed class IKTargetAnimatableActuator(
    AnimatableBody3D body,
    IReadOnlyList<HandDynamicInteractionShape>? dynamicInteractionShapes = null)
    : IIKTargetActuator
{
    private const float DeltaEpsilon = 1e-6f;
    private const int MaximumSlideIterations = 3;

    /// <summary>
    /// Metadata marker applied to runtime hand movement collision shapes generated from profile descriptors.
    /// </summary>
    public const string GeneratedMovementCollisionShapeMetaKey = "alleycat_generated_hand_movement_collision_shape";

    private Vector3 _velocity = Vector3.Zero;
    private IKTargetActuationResult _lastActuation = IKTargetActuationResult.Inactive(body.GlobalTransform, "NotRun");

    /// <summary>
    /// Number of runtime movement-collision shapes generated from profile descriptors for this actuator.
    /// </summary>
    public int GeneratedMovementCollisionShapeCount { get; } = EnsureProfileMovementCollisionShapes(body, dynamicInteractionShapes);

    private HandDynamicBodyInteractionController DynamicBodyInteraction { get; } = new(body, dynamicInteractionShapes);

    /// <summary>
    /// Gets the dynamic-body interaction controller for integration tests.
    /// </summary>
    public HandDynamicBodyInteractionController DynamicBodyInteractionControllerForTesting => DynamicBodyInteraction;

    /// <summary>
    /// Maximum translation speed in metres per second.
    /// </summary>
    public float MaximumSpeed
    {
        get;
        set;
    } = 28.0f;

    /// <summary>
    /// Position-error gain used to convert displacement into target velocity.
    /// </summary>
    public float PositionResponsiveness
    {
        get;
        set;
    } = 14.0f;

    /// <summary>
    /// Maximum rate of velocity change in metres per second squared.
    /// </summary>
    public float MaximumAcceleration
    {
        get;
        set;
    } = 48.0f;

    /// <summary>
    /// Rotation-error gain used to ease the actuator basis towards the target basis.
    /// </summary>
    public float RotationResponsiveness
    {
        get;
        set;
    } = 24.0f;

    /// <summary>
    /// Angular threshold below which the actuator snaps directly to the target basis.
    /// </summary>
    public float RotationSnapAngleRadians
    {
        get;
        set;
    } = 0.01f;

    /// <summary>
    /// Distance within which the actuator may snap directly to an unobstructed target pose.
    /// </summary>
    public float SnapDistance
    {
        get;
        set;
    } = 0.03f;

    /// <summary>
    /// Collision mask queried for explicit dynamic rigid-body hand interaction.
    /// </summary>
    public uint DynamicBodyInteractionCollisionMask
    {
        get => DynamicBodyInteraction.CollisionMask;
        set => DynamicBodyInteraction.CollisionMask = value;
    }

    /// <summary>
    /// Minimum approach speed before the impact channel may fire.
    /// </summary>
    public float DynamicImpactApproachSpeedThreshold
    {
        get => DynamicBodyInteraction.ImpactApproachSpeedThreshold;
        set => DynamicBodyInteraction.ImpactApproachSpeedThreshold = value;
    }

    /// <summary>
    /// Impact impulse gain applied per metre-per-second of approach speed.
    /// </summary>
    public float DynamicImpactImpulsePerSpeed
    {
        get => DynamicBodyInteraction.ImpactImpulsePerSpeed;
        set => DynamicBodyInteraction.ImpactImpulsePerSpeed = value;
    }

    /// <summary>
    /// Maximum impact impulse magnitude.
    /// </summary>
    public float DynamicImpactImpulseCap
    {
        get => DynamicBodyInteraction.ImpactImpulseCap;
        set => DynamicBodyInteraction.ImpactImpulseCap = value;
    }

    /// <summary>
    /// Minimum pressing speed before the sustained push channel may fire.
    /// </summary>
    public float DynamicSustainedPushSpeedThreshold
    {
        get => DynamicBodyInteraction.SustainedPushSpeedThreshold;
        set => DynamicBodyInteraction.SustainedPushSpeedThreshold = value;
    }

    /// <summary>
    /// Sustained push-force gain applied per metre-per-second of pressing speed.
    /// </summary>
    public float DynamicSustainedForcePerSpeed
    {
        get => DynamicBodyInteraction.SustainedForcePerSpeed;
        set => DynamicBodyInteraction.SustainedForcePerSpeed = value;
    }

    /// <summary>
    /// Maximum sustained push-force magnitude.
    /// </summary>
    public float DynamicSustainedForceCap
    {
        get => DynamicBodyInteraction.SustainedForceCap;
        set => DynamicBodyInteraction.SustainedForceCap = value;
    }

    /// <inheritdoc />
    public IKTargetActuationResult Actuate(IKTargetPipelineRequest request, double delta)
    {
        ApplyActuation(request, delta);
        return _lastActuation;
    }

    private void ApplyActuation(IKTargetPipelineRequest request, double delta)
    {
        float deltaSeconds = (float)delta;
        if (deltaSeconds <= DeltaEpsilon)
        {
            _lastActuation = IKTargetActuationResult.Inactive(body.GlobalTransform, "InvalidDelta");
            return;
        }

        IKTargetFollowState targetState = request.RequestedFollowState;
        Transform3D targetTransform = targetState.WorldTransform;
        if (!targetState.Active)
        {
            _velocity = Vector3.Zero;
            _lastActuation = new IKTargetActuationResult(
                targetTransform,
                body.GlobalTransform,
                IKTargetPipelineFeedback.FromTargets(targetTransform, body.GlobalTransform, "Inactive"));
            return;
        }

        body.SyncToPhysics = false;

        Vector3 displacement = targetTransform.Origin - body.GlobalPosition;
        float snapDistanceSquared = SnapDistance * SnapDistance;
        bool snapped = displacement.LengthSquared() <= snapDistanceSquared && !body.TestMove(body.GlobalTransform, displacement);
        bool collided = false;
        if (snapped)
        {
            SetWorldTransform(body, targetTransform);
            _velocity = Vector3.Zero;
        }
        else
        {
            Vector3 desiredVelocity = IKTargetBodyActuatorMath.ComputeDesiredVelocity(
                displacement,
                MaximumSpeed,
                PositionResponsiveness,
                SnapDistance);

            _velocity = IKTargetBodyActuatorMath.ComputeFollowVelocity(
                _velocity,
                desiredVelocity,
                deltaSeconds,
                MaximumAcceleration);

            collided = MoveWithCollisionSlide(_velocity * deltaSeconds);
        }

        if (!snapped)
        {
            body.GlobalBasis = IKTargetBodyActuatorMath.ComputeFollowBasis(
                body.GlobalBasis,
                targetTransform.Basis,
                deltaSeconds,
                RotationResponsiveness,
                RotationSnapAngleRadians);
        }

        DynamicBodyInteraction.Update(targetTransform, delta);
        Transform3D realisedTarget = body.GlobalTransform;
        _lastActuation = new IKTargetActuationResult(
            targetTransform,
            realisedTarget,
            IKTargetPipelineFeedback.FromTargets(
                targetTransform,
                realisedTarget,
                collided ? "Collision" : "None"));
    }

    private static void SetWorldTransform(Node3D node, Transform3D worldTransform)
    {
        Transform3D orthonormalWorldTransform = new(worldTransform.Basis.Orthonormalized(), worldTransform.Origin);
        node.Transform = node.GetParent() is Node3D parent
            ? parent.GlobalTransform.AffineInverse() * orthonormalWorldTransform
            : orthonormalWorldTransform;
        if (node.IsInsideTree())
        {
            node.ForceUpdateTransform();
        }
    }

    private static int EnsureProfileMovementCollisionShapes(
        AnimatableBody3D handBody,
        IReadOnlyList<HandDynamicInteractionShape>? profileShapes)
    {
        if (profileShapes is null || profileShapes.Count == 0 || HasEnabledCollisionShape(handBody))
        {
            return 0;
        }

        int generatedShapeCount = 0;
        for (int index = 0; index < profileShapes.Count; index += 1)
        {
            HandDynamicInteractionShape profileShape = profileShapes[index];
            if (profileShape.Disabled)
            {
                continue;
            }

            CollisionShape3D movementShape = new()
            {
                Name = $"GeneratedHandMovementCollisionShape_{index:D2}",
                Shape = profileShape.Shape,
                Disabled = profileShape.Disabled,
                Transform = profileShape.Transform,
            };
            movementShape.SetMeta(GeneratedMovementCollisionShapeMetaKey, true);
            handBody.AddChild(movementShape);
            generatedShapeCount += 1;
        }

        return generatedShapeCount;
    }

    private static bool HasEnabledCollisionShape(AnimatableBody3D handBody)
    {
        foreach (Node child in handBody.GetChildren())
        {
            if (child is CollisionShape3D { Disabled: false, Shape: not null })
            {
                return true;
            }
        }

        return false;
    }

    private bool MoveWithCollisionSlide(Vector3 motion)
    {
        Vector3 remainingMotion = motion;
        bool collided = false;

        for (int iteration = 0; iteration < MaximumSlideIterations; iteration += 1)
        {
            if (remainingMotion.LengthSquared() <= DeltaEpsilon * DeltaEpsilon)
            {
                return collided;
            }

            KinematicCollision3D? collision = body.MoveAndCollide(remainingMotion);
            if (collision is null)
            {
                return collided;
            }

            collided = true;
            Vector3 collisionNormal = collision.GetNormal();
            _velocity = _velocity.Slide(collisionNormal);

            Vector3 remainder = collision.GetRemainder();
            remainingMotion = remainder.Slide(collisionNormal);
        }

        return collided;
    }
}
