using AlleyCat.IK;
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
            follower =>
            {
                follower.DynamicImpactApproachSpeedThreshold = 0.05f;
                follower.DynamicImpactImpulsePerSpeed = 10.0f;
                follower.DynamicImpactImpulseCap = 0.50f;
                follower.DynamicSustainedForcePerSpeed = 0.0f;
                follower.DynamicSustainedForceCap = 0.0f;
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
            follower =>
            {
                follower.DynamicImpactImpulsePerSpeed = 0.0f;
                follower.DynamicImpactImpulseCap = 0.0f;
                follower.DynamicSustainedPushSpeedThreshold = 0.01f;
                follower.DynamicSustainedForcePerSpeed = 1000.0f;
                follower.DynamicSustainedForceCap = 6.0f;
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

    private static void AssertVectorNearZero(Vector3 vector, float tolerance)
    {
        Assert.InRange(Mathf.Abs(vector.X), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(vector.Y), 0.0f, tolerance);
        Assert.InRange(Mathf.Abs(vector.Z), 0.0f, tolerance);
    }

    private sealed class RuntimeFixture(Node3D root, AnimatableBody3D handBody, RigidBody3D dynamicBody, IKTargetAnimatableFollower follower, TargetPoseSource targetPoseSource)
    {
        public Node3D Root { get; } = root;

        public AnimatableBody3D HandBody { get; } = handBody;

        public RigidBody3D DynamicBody { get; } = dynamicBody;

        public IKTargetAnimatableFollower Follower { get; } = follower;

        public TargetPoseSource TargetPoseSource { get; } = targetPoseSource;

        public static async Task<RuntimeFixture> CreateAsync(SceneTree sceneTree, Action<IKTargetAnimatableFollower>? configureFollower = null)
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
            handBody.AddChild(new CollisionShape3D
            {
                Name = "CollisionShape3D",
                Shape = new BoxShape3D { Size = _boxSize },
            });

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

            root.AddChild(handBody);
            root.AddChild(dynamicBody);

            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);
            await WaitForFramesAsync(sceneTree, 1);
            await WaitForPhysicsFramesAsync(sceneTree, 3);

            TargetPoseSource targetPoseSource = new();

            IKTargetAnimatableFollower follower = new(handBody, targetPoseSource.GetTransform)
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
            configureFollower?.Invoke(follower);

            dynamicBody.LinearVelocity = Vector3.Zero;
            dynamicBody.AngularVelocity = Vector3.Zero;
            dynamicBody.Sleeping = false;

            return new RuntimeFixture(root, handBody, dynamicBody, follower, targetPoseSource);
        }

        public async Task PrimeAsync(Vector3 handPosition)
        {
            SetHandPose(handPosition);
            TargetPoseSource.Transform = HandBody.GlobalTransform;
            Follower.Follow(PhysicsStepSeconds);
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
            ResetDynamicBodyMotion();
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
        }

        public async Task UpdateAsync(Vector3 handPosition)
        {
            TargetPoseSource.Transform = new Transform3D(Basis.Identity, ToWorldPosition(handPosition));
            Follower.Follow(PhysicsStepSeconds);
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
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
    }

    private sealed class TargetPoseSource
    {
        public Transform3D Transform { get; set; } = Transform3D.Identity;

        public Transform3D GetTransform()
            => Transform;
    }
}
