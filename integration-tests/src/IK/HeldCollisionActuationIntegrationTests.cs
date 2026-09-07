using AlleyCat.IK;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Controlled coverage for held geometry at the requested-to-realised hand-actuation boundary.
/// </summary>
public sealed class HeldCollisionActuationIntegrationTests
{
    private const double PhysicsStepSeconds = 1.0d / 60.0d;
    private static readonly Vector3 _heldShapeOffset = new(0.22f, 0.0f, 0.0f);
    private static readonly Vector3 _requestedHandPosition = new(0.30f, 0.0f, 0.0f);

    /// <summary>
    /// A/B comparison proving that an attachment follower can overlap a static wall without affecting hand realisation,
    /// while the same held geometry on the actuator blocks before the otherwise-clear hand reaches its request.
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldProxyOwnership_StaticWall_OnlyActuatorOwnedGeometryConstrainsRealisedHand()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "HeldProxyOwnershipABRoot",
            Position = new Vector3(80.0f, 10.0f, 0.0f)
        };
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);

        try
        {
            ProxyArrangement attachmentOwned = CreateArrangement(root, "AttachmentOwned", zOffset: -1.0f, proxyOnActuator: false);
            ProxyArrangement actuatorOwned = CreateArrangement(root, "ActuatorOwned", zOffset: 1.0f, proxyOnActuator: true);
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            IKTargetActuationResult attachmentResult = await DriveToRequestAsync(sceneTree, attachmentOwned);
            IKTargetActuationResult actuatorResult = await DriveToRequestAsync(sceneTree, actuatorOwned);

            float attachmentPenetration = ComputeWallPenetration(attachmentOwned);
            float actuatorPenetration = ComputeWallPenetration(actuatorOwned);

            Assert.Equal("None", attachmentResult.Feedback.Reason);
            Assert.True(attachmentResult.Feedback.ErrorDistance < 0.002f);
            Assert.InRange(attachmentOwned.Body.Position.X, 0.298f, 0.302f);
            Assert.InRange(attachmentPenetration, 0.213f, 0.217f);

            Assert.Equal("Collision", actuatorResult.Feedback.Reason);
            Assert.InRange(actuatorOwned.Body.Position.X, 0.083f, 0.087f);
            Assert.InRange(actuatorResult.Feedback.ErrorDistance, 0.213f, 0.217f);
            Assert.True(actuatorPenetration <= 0.002f, "Actuator-owned held geometry must not be driven through the static wall.");
            Assert.True(
                actuatorOwned.Body.GlobalPosition.X + 0.03f < actuatorOwned.Wall.GlobalPosition.X - 0.025f,
                "The hand's own collider must remain clear when the held geometry causes obstruction.");
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a held runtime shape owner added after actuator construction participates in explicit dynamic impact.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ActuatorOwnedHeldStick_RuntimeShapeOwner_StrikesDynamicProp()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "HeldStickDynamicImpactRoot",
            Position = new Vector3(90.0f, 10.0f, 0.0f)
        };
        AnimatableBody3D handBody = CreateHandBody("HandActuator", Vector3.Zero);
        // Isolate the explicit dynamic-interaction channel from passive rigid-body contact response in this fixture.
        handBody.CollisionLayer = 0;
        handBody.CollisionMask = 1;
        RigidBody3D prop = new()
        {
            Name = "DynamicProp",
            Position = new Vector3(0.55f, 0.0f, 0.0f),
            CollisionLayer = 2,
            CollisionMask = 1,
            GravityScale = 0.0f,
            Mass = 1.0f,
        };
        prop.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.06f } });
        prop.AddToGroup(HandDynamicBodyInteractionController.DynamicInteractionGroupName);
        root.AddChild(handBody);
        root.AddChild(prop);
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);

        try
        {
            IKTargetAnimatableActuator actuator = CreateActuator(handBody);
            actuator.MaximumAcceleration = 48.0f;
            uint ownerId = AddRuntimeShapeOwner(
                handBody,
                new BoxShape3D { Size = new Vector3(0.36f, 0.04f, 0.04f) },
                new Transform3D(Basis.Identity, new Vector3(0.22f, 0.0f, 0.0f)));
            HandDynamicBodyInteractionController.NotifyRuntimeShapeOwnersChanged(handBody);
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            _ = actuator.Actuate(CreateRequest(handBody.GlobalTransform), PhysicsStepSeconds);
            Vector3 startingPosition = handBody.GlobalPosition;
            Transform3D requested = handBody.GlobalTransform;
            for (int step = 1; step <= 6; step += 1)
            {
                requested = new Transform3D(Basis.Identity, startingPosition + new Vector3(step * 0.03f, 0.0f, 0.0f));
                _ = actuator.Actuate(CreateRequest(requested), PhysicsStepSeconds);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            Assert.InRange(prop.LinearVelocity.X, 0.10f, 0.60f);
            Assert.InRange(prop.GlobalPosition.X - (root.GlobalPosition.X + 0.55f), 0.001f, 0.05f);

            handBody.RemoveShapeOwner(ownerId);
            HandDynamicBodyInteractionController.NotifyRuntimeShapeOwnersChanged(handBody);
            _ = actuator.Actuate(CreateRequest(requested), PhysicsStepSeconds);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies held geometry remains collision-reachable against the opposite hand for mirrored left/right motion.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ActuatorOwnedHeldGeometry_LeftAndRight_CollidesWithOppositeHand()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "HeldGeometryOppositeHandRoot",
            Position = new Vector3(100.0f, 10.0f, 0.0f),
        };
        List<(AnimatableBody3D Body, IKTargetAnimatableActuator Actuator, float Direction)> sides = [];
        for (int sideIndex = 0; sideIndex < 2; sideIndex += 1)
        {
            float direction = sideIndex == 0 ? -1.0f : 1.0f;
            Node3D sideRoot = new()
            {
                Position = new Vector3(0.0f, 0.0f, sideIndex * 1.0f)
            };
            AnimatableBody3D body = CreateHandBody(sideIndex == 0 ? "LeftHandActuator" : "RightHandActuator", Vector3.Zero);
            AnimatableBody3D oppositeHand = CreateHandBody("OppositeHand", new Vector3(0.48f * direction, 0.0f, 0.0f));
            _ = AddRuntimeShapeOwner(
                body,
                new BoxShape3D { Size = new Vector3(0.30f, 0.04f, 0.04f) },
                new Transform3D(Basis.Identity, _heldShapeOffset * direction));
            sideRoot.AddChild(body);
            sideRoot.AddChild(oppositeHand);
            root.AddChild(sideRoot);
            sides.Add((body, CreateActuator(body), direction));
        }
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, root);

        try
        {
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            foreach ((AnimatableBody3D body, IKTargetAnimatableActuator actuator, float direction) in sides)
            {
                Transform3D requested = new(Basis.Identity, body.GlobalPosition + (_requestedHandPosition * direction));
                IKTargetActuationResult result = default;
                for (int step = 0; step < 30; step += 1)
                {
                    result = actuator.Actuate(CreateRequest(requested), PhysicsStepSeconds);
                    await WaitForPhysicsFramesAsync(sceneTree, 1);
                }

                Assert.Equal("Collision", result.Feedback.Reason);
                Assert.InRange(result.Feedback.ErrorDistance, 0.20f, 0.23f);
            }
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static ProxyArrangement CreateArrangement(Node3D root, string name, float zOffset, bool proxyOnActuator)
    {
        Node3D arrangementRoot = new()
        {
            Name = name,
            Position = new Vector3(0.0f, 0.0f, zOffset)
        };
        AnimatableBody3D body = CreateHandBody($"{name}HandActuator", Vector3.Zero);
        AnimatableBody3D proxyTarget = proxyOnActuator
            ? body
            : new AnimatableBody3D { Name = $"{name}AttachmentFollower", CollisionLayer = 1, CollisionMask = 1 };
        StaticBody3D wall = new()
        {
            Name = $"{name}Wall",
            Position = new Vector3(0.48f, 0.0f, 0.0f),
            CollisionLayer = 1,
            CollisionMask = 1,
        };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.05f, 0.5f, 0.5f) } });
        arrangementRoot.AddChild(body);
        if (!proxyOnActuator)
        {
            arrangementRoot.AddChild(proxyTarget);
        }
        arrangementRoot.AddChild(wall);
        root.AddChild(arrangementRoot);

        _ = AddRuntimeShapeOwner(
            proxyTarget,
            new BoxShape3D { Size = new Vector3(0.30f, 0.04f, 0.04f) },
            new Transform3D(Basis.Identity, _heldShapeOffset));
        return new ProxyArrangement(body, proxyTarget, wall, CreateActuator(body));
    }

    private static AnimatableBody3D CreateHandBody(string name, Vector3 position)
    {
        AnimatableBody3D body = new()
        {
            Name = name,
            Position = position,
            CollisionLayer = 1,
            CollisionMask = 1,
            SyncToPhysics = false,
        };
        body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.03f } });
        return body;
    }

    private static IKTargetAnimatableActuator CreateActuator(AnimatableBody3D body)
        => new(body)
        {
            MaximumSpeed = 30.0f,
            PositionResponsiveness = 100.0f,
            MaximumAcceleration = 1000.0f,
            SnapDistance = 0.001f,
            DynamicBodyInteractionCollisionMask = 2,
        };

    private static uint AddRuntimeShapeOwner(CollisionObject3D target, Shape3D shape, Transform3D transform)
    {
        uint ownerId = target.CreateShapeOwner(target);
        target.ShapeOwnerAddShape(ownerId, shape);
        target.ShapeOwnerSetTransform(ownerId, transform);
        target.ShapeOwnerSetDisabled(ownerId, false);
        return ownerId;
    }

    private static async Task<IKTargetActuationResult> DriveToRequestAsync(SceneTree sceneTree, ProxyArrangement arrangement)
    {
        Transform3D requested = new(Basis.Identity, arrangement.Body.GlobalPosition + _requestedHandPosition);
        IKTargetActuationResult result = default;
        for (int step = 0; step < 30; step += 1)
        {
            result = arrangement.Actuator.Actuate(CreateRequest(requested), PhysicsStepSeconds);
            if (!ReferenceEquals(arrangement.ProxyTarget, arrangement.Body))
            {
                arrangement.ProxyTarget.GlobalTransform = arrangement.Body.GlobalTransform;
            }
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        return result;
    }

    private static IKTargetPipelineRequest CreateRequest(Transform3D target)
    {
        IKTargetFollowState state = new(target, active: true);
        return new IKTargetPipelineRequest(state, state);
    }

    private static float ComputeWallPenetration(ProxyArrangement arrangement)
    {
        float heldRightEdge = arrangement.ProxyTarget.GlobalPosition.X + _heldShapeOffset.X + 0.15f;
        float wallLeftEdge = arrangement.Wall.GlobalPosition.X - 0.025f;
        return Mathf.Max(0.0f, heldRightEdge - wallLeftEdge);
    }

    private sealed record ProxyArrangement(
        AnimatableBody3D Body,
        AnimatableBody3D ProxyTarget,
        StaticBody3D Wall,
        IKTargetAnimatableActuator Actuator);
}
