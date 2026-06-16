using AlleyCat.IK;
using AlleyCat.Interaction.Physical;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Focused runtime coverage for explicit hand-to-dynamic-body force transfer.
/// </summary>
public sealed class HandDynamicBodyInteractionControllerIntegrationTests
{
    private const float PhysicsStepSeconds = 1.0f / 60.0f;
    private static readonly Vector3 _fixtureOrigin = new(50.0f, 10.0f, 0.0f);
    private static readonly Vector3 _dynamicBodyPosition = _fixtureOrigin + new Vector3(0.30f, 0.0f, 0.0f);
    private static readonly Vector3 _boxSize = new(0.14f, 0.14f, 0.14f);

    /// <summary>
    /// Verifies separated bodies do not receive explicit force transfer.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Update_SeparatedHandAndBody_AppliesNoForce()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.PrimeAsync(Vector3.Zero);

            await fixture.UpdateAsync(new Vector3(0.04f, 0.0f, 0.0f));

            AssertVectorNearZero(fixture.DynamicBody.LinearVelocity, 0.001f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies overlapping tangential motion does not count as pressing.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Update_OverlappingTangentialMotion_AppliesNoForce()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.PrimeAsync(new Vector3(0.20f, 0.0f, 0.0f));

            await fixture.UpdateAsync(new Vector3(0.20f, 0.04f, 0.0f));

            AssertVectorNearZero(fixture.DynamicBody.LinearVelocity, 0.001f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies retreating overlap does not count as a pressing contact.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Update_OverlappingRetreatingMotion_AppliesNoForce()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.PrimeAsync(new Vector3(0.20f, 0.0f, 0.0f));

            await fixture.UpdateAsync(new Vector3(0.18f, 0.0f, 0.0f));

            AssertVectorNearZero(fixture.DynamicBody.LinearVelocity, 0.001f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the first qualifying pressing contact is capped by the impact channel.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Update_FirstQualifyingPressingContact_ClampsImpactImpulse()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(
            sceneTree,
            actuator =>
            {
                actuator.DynamicImpactApproachSpeedThreshold = 0.05f;
                actuator.DynamicImpactImpulsePerSpeed = 10.0f;
                actuator.DynamicImpactImpulseCap = 0.50f;
                actuator.DynamicSustainedForcePerSpeed = 0.0f;
                actuator.DynamicSustainedForceCap = 0.0f;
            });

        try
        {
            await fixture.PrimeAsync(new Vector3(0.12f, 0.0f, 0.0f));

            await fixture.UpdateAsync(new Vector3(0.20f, 0.0f, 0.0f));

            Assert.InRange(fixture.DynamicBody.LinearVelocity.X, 0.40f, 0.60f);
            Assert.InRange(Mathf.Abs(fixture.DynamicBody.LinearVelocity.Y), 0.0f, 0.001f);
            Assert.InRange(Mathf.Abs(fixture.DynamicBody.LinearVelocity.Z), 0.0f, 0.001f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies sustained pushing stops once pressing speed reaches zero even if overlap remains.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Update_OverlapPersistsButPressingStops_SustainedForceCeases()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(
            sceneTree,
            actuator =>
            {
                actuator.DynamicImpactImpulsePerSpeed = 0.0f;
                actuator.DynamicImpactImpulseCap = 0.0f;
                actuator.DynamicSustainedPushSpeedThreshold = 0.01f;
                actuator.DynamicSustainedForcePerSpeed = 1000.0f;
                actuator.DynamicSustainedForceCap = 6.0f;
            });

        try
        {
            await fixture.PrimeAsync(new Vector3(0.18f, 0.0f, 0.0f));

            await fixture.UpdateAsync(new Vector3(0.20f, 0.0f, 0.0f));
            float velocityAfterPress = fixture.DynamicBody.LinearVelocity.X;

            await fixture.UpdateAsync(new Vector3(0.20f, 0.0f, 0.0f));
            float velocityAfterStoppedPress = fixture.DynamicBody.LinearVelocity.X;

            Assert.True(velocityAfterPress > 0.05f, $"Expected sustained force to accelerate the body, but velocity was {velocityAfterPress:F4}.");
            Assert.InRange(velocityAfterStoppedPress - velocityAfterPress, -0.005f, 0.02f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies profile-backed query shapes preserve force transfer when the hand target has no direct primitive shape.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Update_ProfileBackedShapeOnShapelessHand_AppliesForce()
    {
        SceneTree sceneTree = GetSceneTree();
        IReadOnlyList<HandDynamicInteractionShape> profileShapes = CreateProfileBackedHandShapes("RightHand");
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(
            sceneTree,
            actuator =>
            {
                actuator.DynamicImpactApproachSpeedThreshold = 0.05f;
                actuator.DynamicImpactImpulsePerSpeed = 10.0f;
                actuator.DynamicImpactImpulseCap = 0.50f;
                actuator.DynamicSustainedForcePerSpeed = 0.0f;
                actuator.DynamicSustainedForceCap = 0.0f;
            },
            addDirectHandShape: false,
            profileShapes);

        try
        {
            CollisionShape3D generatedShape = Assert.Single(GetGeneratedMovementCollisionShapes(fixture.HandBody));
            Assert.True(ReferenceEquals(profileShapes[0].Shape, generatedShape.Shape));
            AssertTransformApproximately(profileShapes[0].Transform, generatedShape.Transform, 0.001f);

            await fixture.PrimeAsync(new Vector3(0.12f, 0.0f, 0.0f));

            await fixture.UpdateAsync(new Vector3(0.20f, 0.0f, 0.0f));

            Assert.InRange(fixture.DynamicBody.LinearVelocity.X, 0.40f, 0.60f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the existing hand dynamic-body collision path delivers the pluggable impact source to receivers.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Update_FirstQualifyingPressingContact_InvokesPhysicalInteractionReceiver()
    {
        SceneTree sceneTree = GetSceneTree();
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(
            sceneTree,
            actuator =>
            {
                actuator.DynamicImpactApproachSpeedThreshold = 0.05f;
                actuator.DynamicImpactImpulsePerSpeed = 10.0f;
                actuator.DynamicImpactImpulseCap = 0.50f;
                actuator.DynamicSustainedForcePerSpeed = 0.0f;
                actuator.DynamicSustainedForceCap = 0.0f;
            },
            addImpactInteractionWiring: true);

        try
        {
            await fixture.PrimeAsync(new Vector3(0.12f, 0.0f, 0.0f));

            await fixture.UpdateAsync(new Vector3(0.20f, 0.0f, 0.0f));

            IImpactPhysicalInteraction interaction = Assert.IsAssignableFrom<IImpactPhysicalInteraction>(
                fixture.ImpactReceiver?.LastImpactInteraction);
            Assert.Same(fixture.DynamicBodyInteractionController, interaction.Source);
            Assert.Equal(["HandBody", "ImpactSource"], [.. fixture.DynamicBodyInteractionController!.Tags]);
            Assert.Equal(["DynamicBody", "ImpactReceiver"], [.. fixture.ImpactReceiver!.Tags]);
            Assert.InRange(interaction.Velocity.X, 4.0f, 5.0f);
            Assert.InRange(Mathf.Abs(interaction.ContactPoint.X - fixture.DynamicBody.GlobalPosition.X), 0.0f, 0.08f);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies profile-backed runtime movement shapes let a shapeless hand target collide with obstacles.
    /// </summary>
    [Headless]
    [Fact]
    public async Task Actuate_ProfileBackedShapeOnShapelessHand_CollidesDuringMovement()
    {
        SceneTree sceneTree = GetSceneTree();
        IReadOnlyList<HandDynamicInteractionShape> profileShapes = CreateProfileBackedHandShapes("RightHand");
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(
            sceneTree,
            addDirectHandShape: false,
            profileShapes: profileShapes);

        try
        {
            StaticBody3D obstacle = CreateStaticObstacle(RuntimeFixture.ToWorldPositionForTest(new Vector3(0.20f, 0.0f, 0.0f)));
            fixture.Root.AddChild(obstacle);
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            CollisionShape3D generatedShape = Assert.Single(GetGeneratedMovementCollisionShapes(fixture.HandBody));
            Assert.Equal(1, fixture.Actuator.GeneratedMovementCollisionShapeCount);
            Assert.True(ReferenceEquals(profileShapes[0].Shape, generatedShape.Shape));
            AssertTransformApproximately(profileShapes[0].Transform, generatedShape.Transform, 0.001f);
            Assert.True(
                fixture.HandBody.TestMove(fixture.HandBody.GlobalTransform, new Vector3(0.28f, 0.0f, 0.0f)),
                "Profile-backed runtime hand movement shapes should participate in TestMove queries.");

            KinematicCollision3D? collision = fixture.HandBody.MoveAndCollide(new Vector3(0.28f, 0.0f, 0.0f));
            Assert.NotNull(collision);
            Assert.True(
                fixture.HandBody.GlobalPosition.X < obstacle.GlobalPosition.X - 0.03f,
                "MoveAndCollide should stop the hand before it passes through the obstacle.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static void AssertVectorNearZero(Vector3 vector, float tolerance)
    {
        Assert.InRange(Mathf.Abs(vector.X), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(vector.Y), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(vector.Z), 0.0f, tolerance);
    }

    private sealed class RuntimeFixture(
        Node3D root,
        AnimatableBody3D handBody,
        RigidBody3D dynamicBody,
        IKTargetAnimatableActuator actuator,
        TargetPoseSource targetPoseSource,
        RigidBodyImpactInteractionReceiver3D? impactReceiver)
    {
        public Node3D Root { get; } = root;

        public AnimatableBody3D HandBody { get; } = handBody;

        public RigidBody3D DynamicBody { get; } = dynamicBody;

        public IKTargetAnimatableActuator Actuator { get; } = actuator;

        public TargetPoseSource TargetPoseSource { get; } = targetPoseSource;

        public HandDynamicBodyInteractionController? DynamicBodyInteractionController => Actuator.DynamicBodyInteractionControllerForTesting;

        public RigidBodyImpactInteractionReceiver3D? ImpactReceiver { get; } = impactReceiver;

        public static async Task<RuntimeFixture> CreateAsync(
            SceneTree sceneTree,
            Action<IKTargetAnimatableActuator>? configureActuator = null,
            bool addDirectHandShape = true,
            IReadOnlyList<HandDynamicInteractionShape>? profileShapes = null,
            bool addImpactInteractionWiring = false)
        {
            Node3D root = new()
            {
                Name = "HandDynamicBodyInteractionControllerTestRoot",
            };

            AnimatableBody3D handBody = new()
            {
                Name = "HandBody",
                TopLevel = true,
                SyncToPhysics = false,
                CollisionLayer = 8,
                CollisionMask = 5,
                GlobalPosition = _fixtureOrigin,
            };
            if (addDirectHandShape)
            {
                handBody.AddChild(new CollisionShape3D
                {
                    Name = "CollisionShape3D",
                    Shape = new BoxShape3D { Size = _boxSize },
                });
            }

            RigidBody3D dynamicBody = new()
            {
                Name = "DynamicBody",
                TopLevel = true,
                CollisionLayer = 2,
                CollisionMask = 0,
                GravityScale = 0.0f,
                LinearDamp = 0.0f,
                AngularDamp = 10.0f,
                GlobalPosition = _dynamicBodyPosition,
            };
            dynamicBody.AddToGroup(HandDynamicBodyInteractionController.DynamicInteractionGroupName);
            dynamicBody.AddChild(new CollisionShape3D
            {
                Name = "CollisionShape3D",
                Shape = new BoxShape3D { Size = _boxSize },
            });
            RigidBodyImpactInteractionReceiver3D? impactReceiver = null;
            if (addImpactInteractionWiring)
            {
                impactReceiver = new RigidBodyImpactInteractionReceiver3D
                {
                    Name = "ImpactInteractionReceiver",
                    AuthoredTags = ["DynamicBody", "ImpactReceiver"],
                    ImpulseScale = 0.0f,
                };
                dynamicBody.AddChild(impactReceiver);
            }

            root.AddChild(handBody);
            root.AddChild(dynamicBody);

            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);
            await WaitForFramesAsync(sceneTree, 1);
            await WaitForPhysicsFramesAsync(sceneTree, 3);

            TargetPoseSource targetPoseSource = new();

            IKTargetAnimatableActuator actuator = new(handBody, profileShapes)
            {
                MaximumSpeed = 100.0f,
                MaximumAcceleration = 400.0f,
                PositionResponsiveness = 100.0f,
                RotationResponsiveness = 100.0f,
                SnapDistance = 0.001f,
                DynamicBodyInteractionCollisionMask = 2,
                DynamicImpactApproachSpeedThreshold = 0.05f,
                DynamicImpactImpulsePerSpeed = 10.0f,
                DynamicImpactImpulseCap = 0.50f,
                DynamicSustainedPushSpeedThreshold = 0.05f,
                DynamicSustainedForcePerSpeed = 100.0f,
                DynamicSustainedForceCap = 2.0f,
            };
            configureActuator?.Invoke(actuator);

            dynamicBody.LinearVelocity = Vector3.Zero;
            dynamicBody.AngularVelocity = Vector3.Zero;
            dynamicBody.Sleeping = false;

            return new RuntimeFixture(root, handBody, dynamicBody, actuator, targetPoseSource, impactReceiver);
        }

        public async Task PrimeAsync(Vector3 handPosition)
        {
            SetHandPose(handPosition);
            TargetPoseSource.Transform = HandBody.GlobalTransform;
            ActuateTarget();
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
            ResetDynamicBodyMotion();
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
        }

        public async Task UpdateAsync(Vector3 handPosition)
        {
            TargetPoseSource.Transform = new Transform3D(Basis.Identity, ToWorldPosition(handPosition));
            ActuateTarget();
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
        }

        private void ActuateTarget()
        {
            IKTargetFollowState followState = new(TargetPoseSource.Transform, active: true);
            _ = Actuator.Actuate(new IKTargetPipelineRequest(followState, followState), PhysicsStepSeconds);
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        private void SetHandPose(Vector3 handPosition)
        {
            HandBody.GlobalTransform = new Transform3D(Basis.Identity, ToWorldPosition(handPosition));
            HandBody.ForceUpdateTransform();
        }

        private void ResetDynamicBodyMotion()
        {
            DynamicBody.GlobalPosition = _dynamicBodyPosition;
            DynamicBody.LinearVelocity = Vector3.Zero;
            DynamicBody.AngularVelocity = Vector3.Zero;
            DynamicBody.Sleeping = false;
            DynamicBody.ForceUpdateTransform();
        }

        private static Vector3 ToWorldPosition(Vector3 localPosition)
            => _fixtureOrigin + localPosition;

        public static Vector3 ToWorldPositionForTest(Vector3 localPosition)
            => ToWorldPosition(localPosition);
    }

    private static IEnumerable<CollisionShape3D> GetGeneratedMovementCollisionShapes(Node handBody)
    {
        foreach (Node child in handBody.GetChildren())
        {
            if (child is CollisionShape3D collisionShape
                && collisionShape.HasMeta(IKTargetAnimatableActuator.GeneratedMovementCollisionShapeMetaKey))
            {
                yield return collisionShape;
            }
        }
    }

    private static StaticBody3D CreateStaticObstacle(Vector3 position)
    {
        StaticBody3D obstacle = new()
        {
            Name = "StaticObstacle",
            CollisionLayer = 4,
            CollisionMask = 8,
            GlobalPosition = position,
        };
        obstacle.AddChild(new CollisionShape3D
        {
            Name = "CollisionShape3D",
            Shape = new BoxShape3D { Size = _boxSize },
        });
        return obstacle;
    }

    private static void AssertTransformApproximately(Transform3D expected, Transform3D actual, float tolerance)
    {
        AssertVectorApproximately(expected.Origin, actual.Origin, tolerance);
        AssertVectorApproximately(expected.Basis.X, actual.Basis.X, tolerance);
        AssertVectorApproximately(expected.Basis.Y, actual.Basis.Y, tolerance);
        AssertVectorApproximately(expected.Basis.Z, actual.Basis.Z, tolerance);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(Mathf.Abs(expected.X - actual.X), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(expected.Y - actual.Y), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(expected.Z - actual.Z), 0.0f, tolerance);
    }

    private sealed class TargetPoseSource
    {
        public Transform3D Transform { get; set; } = Transform3D.Identity;

        public Transform3D GetTransform()
            => Transform;
    }

    private static IReadOnlyList<HandDynamicInteractionShape> CreateProfileBackedHandShapes(StringName boneName)
    {
        BodyColliderProfile profile = new()
        {
            SourceScene = CreatePackedHandProfileSourceScene(boneName),
        };
        IReadOnlyList<BodyColliderShapeDescriptor> descriptors = profile.QueryShapeDescriptorsForBone(boneName);
        var shapes = new HandDynamicInteractionShape[descriptors.Count];

        for (int index = 0; index < descriptors.Count; index += 1)
        {
            BodyColliderShapeDescriptor descriptor = descriptors[index];
            shapes[index] = new HandDynamicInteractionShape(descriptor.Shape, descriptor.LocalTransform, descriptor.Disabled);
        }

        return shapes;
    }

    private static PackedScene CreatePackedHandProfileSourceScene(StringName boneName)
    {
        Node root = new()
        {
            Name = "CollidersRoot",
        };
        BoneAttachment3D attachment = new()
        {
            Name = "HandAttachment",
            BoneName = boneName,
        };
        AnimatableBody3D sourceBody = new()
        {
            Name = "HandSourceBody",
        };
        CollisionShape3D sourceShape = new()
        {
            Name = "ProfileHandShape",
            Shape = new BoxShape3D { Size = _boxSize },
        };

        root.AddChild(attachment);
        attachment.Owner = root;
        attachment.AddChild(sourceBody);
        sourceBody.Owner = root;
        sourceBody.AddChild(sourceShape);
        sourceShape.Owner = root;

        PackedScene sourceScene = new();
        Error packResult = sourceScene.Pack(root);
        root.Free();

        Assert.Equal(Error.Ok, packResult);
        return sourceScene;
    }
}
