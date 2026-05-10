using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Drives hand IK target <see cref="AnimatableBody3D"/> nodes using collision-aware kinematic motion.
/// </summary>
public sealed class IKTargetAnimatableFollower(
    AnimatableBody3D body,
    Func<IKTargetFollowState> targetStateSource,
    IReadOnlyList<HandDynamicInteractionShape>? dynamicInteractionShapes = null)
{
    private const float DeltaEpsilon = 1e-6f;
    private const int MaximumSlideIterations = 3;

    /// <summary>
    /// Metadata marker applied to runtime hand movement collision shapes generated from profile descriptors.
    /// </summary>
    public const string GeneratedMovementCollisionShapeMetaKey = "alleycat_generated_hand_movement_collision_shape";

    private Vector3 _velocity = Vector3.Zero;

    /// <summary>
    /// Number of runtime movement-collision shapes generated from profile descriptors for this follower.
    /// </summary>
    public int GeneratedMovementCollisionShapeCount { get; } = EnsureProfileMovementCollisionShapes(body, dynamicInteractionShapes);

    private readonly HandDynamicBodyInteractionController _dynamicBodyInteraction = new(body, dynamicInteractionShapes);

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
    /// Rotation-error gain used to ease the follower basis towards the target basis.
    /// </summary>
    public float RotationResponsiveness
    {
        get;
        set;
    } = 24.0f;

    /// <summary>
    /// Angular threshold below which the follower snaps directly to the target basis.
    /// </summary>
    public float RotationSnapAngleRadians
    {
        get;
        set;
    } = 0.01f;

    /// <summary>
    /// Distance within which the follower may snap directly to an unobstructed target pose.
    /// </summary>
    public float SnapDistance
    {
        get;
        set;
    } = 0.03f;

    /// <summary>
    /// Creates an animatable follower that always treats the supplied target transform as active.
    /// </summary>
    public IKTargetAnimatableFollower(
        AnimatableBody3D body,
        Func<Transform3D> targetTransformSource,
        IReadOnlyList<HandDynamicInteractionShape>? dynamicInteractionShapes = null)
        : this(body, () => new IKTargetFollowState(targetTransformSource(), active: true), dynamicInteractionShapes)
    {
    }

    /// <summary>
    /// Collision mask queried for explicit dynamic rigid-body hand interaction.
    /// </summary>
    public uint DynamicBodyInteractionCollisionMask
    {
        get => _dynamicBodyInteraction.CollisionMask;
        set => _dynamicBodyInteraction.CollisionMask = value;
    }

    /// <summary>
    /// Minimum approach speed before the impact channel may fire.
    /// </summary>
    public float DynamicImpactApproachSpeedThreshold
    {
        get => _dynamicBodyInteraction.ImpactApproachSpeedThreshold;
        set => _dynamicBodyInteraction.ImpactApproachSpeedThreshold = value;
    }

    /// <summary>
    /// Impact impulse gain applied per metre-per-second of approach speed.
    /// </summary>
    public float DynamicImpactImpulsePerSpeed
    {
        get => _dynamicBodyInteraction.ImpactImpulsePerSpeed;
        set => _dynamicBodyInteraction.ImpactImpulsePerSpeed = value;
    }

    /// <summary>
    /// Maximum impact impulse magnitude.
    /// </summary>
    public float DynamicImpactImpulseCap
    {
        get => _dynamicBodyInteraction.ImpactImpulseCap;
        set => _dynamicBodyInteraction.ImpactImpulseCap = value;
    }

    /// <summary>
    /// Minimum pressing speed before the sustained push channel may fire.
    /// </summary>
    public float DynamicSustainedPushSpeedThreshold
    {
        get => _dynamicBodyInteraction.SustainedPushSpeedThreshold;
        set => _dynamicBodyInteraction.SustainedPushSpeedThreshold = value;
    }

    /// <summary>
    /// Sustained push-force gain applied per metre-per-second of pressing speed.
    /// </summary>
    public float DynamicSustainedForcePerSpeed
    {
        get => _dynamicBodyInteraction.SustainedForcePerSpeed;
        set => _dynamicBodyInteraction.SustainedForcePerSpeed = value;
    }

    /// <summary>
    /// Maximum sustained push-force magnitude.
    /// </summary>
    public float DynamicSustainedForceCap
    {
        get => _dynamicBodyInteraction.SustainedForceCap;
        set => _dynamicBodyInteraction.SustainedForceCap = value;
    }

    /// <summary>
    /// Moves the configured body towards its configured transform source.
    /// </summary>
    public void Follow(double delta)
    {
        float deltaSeconds = (float)delta;
        if (deltaSeconds <= DeltaEpsilon)
        {
            return;
        }

        IKTargetFollowState targetState = targetStateSource();
        if (!targetState.Active)
        {
            _velocity = Vector3.Zero;
            return;
        }

        body.SyncToPhysics = false;

        Transform3D targetTransform = targetState.WorldTransform;
        Vector3 displacement = targetTransform.Origin - body.GlobalPosition;
        float snapDistanceSquared = SnapDistance * SnapDistance;
        bool snapped = displacement.LengthSquared() <= snapDistanceSquared && !body.TestMove(body.GlobalTransform, displacement);
        if (snapped)
        {
            SetWorldTransform(body, targetTransform);
            _velocity = Vector3.Zero;
        }
        else
        {
            Vector3 desiredVelocity = IKTargetBodyFollowerMath.ComputeDesiredVelocity(
                displacement,
                MaximumSpeed,
                PositionResponsiveness,
                SnapDistance);

            _velocity = IKTargetBodyFollowerMath.ComputeFollowVelocity(
                _velocity,
                desiredVelocity,
                deltaSeconds,
                MaximumAcceleration);

            MoveWithCollisionSlide(_velocity * deltaSeconds);
        }

        if (!snapped)
        {
            body.GlobalBasis = IKTargetBodyFollowerMath.ComputeFollowBasis(
                body.GlobalBasis,
                targetTransform.Basis,
                deltaSeconds,
                RotationResponsiveness,
                RotationSnapAngleRadians);
        }

        _dynamicBodyInteraction.Update(targetTransform, delta);
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

    private void MoveWithCollisionSlide(Vector3 motion)
    {
        Vector3 remainingMotion = motion;

        for (int iteration = 0; iteration < MaximumSlideIterations; iteration += 1)
        {
            if (remainingMotion.LengthSquared() <= DeltaEpsilon * DeltaEpsilon)
            {
                return;
            }

            KinematicCollision3D? collision = body.MoveAndCollide(remainingMotion);
            if (collision is null)
            {
                return;
            }

            Vector3 collisionNormal = collision.GetNormal();
            _velocity = _velocity.Slide(collisionNormal);

            Vector3 remainder = collision.GetRemainder();
            remainingMotion = remainder.Slide(collisionNormal);
        }
    }
}
