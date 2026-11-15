using AlleyCat.Contact;
using AlleyCat.IK;
using AlleyCat.Interaction.Physical;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Legacy migration coverage (INTR-005 TR15, AC-T9): the authored dynamic props that previously opted into
/// the superseded hand-interaction eligibility keep their INTR-004 impact receipt and impulse behaviour
/// under the generic <c>contact_receiver</c> group, on any collision layer, and are discovered by the
/// INTR-005 <see cref="ContactSource" /> sweeper path through the same eligibility.
/// </summary>
public sealed class PropImpactMigrationIntegrationTests
{
    private const float PhysicsStepSeconds = 1.0f / 60.0f;
    private const string TestBallScenePath = "res://assets/items/test_ball.tscn";
    private const string TestStickScenePath = "res://assets/items/test_stick.tscn";

    private static readonly Vector3 _fixtureOrigin = new(70.0f, 10.0f, 0.0f);
    private static readonly Vector3 _ballPosition = _fixtureOrigin + new Vector3(0.30f, 0.0f, 0.0f);
    private static readonly Vector3 _boxSize = new(0.14f, 0.14f, 0.14f);

    /// <summary>
    /// Verifies the migrated authored props opt in through the contact-receiver group, keep their authored
    /// impact receiver wiring, and retain the collision layer arrangement that lets player hands collide
    /// with them physically.
    /// </summary>
    [Headless]
    [Fact]
    public void MigratedProps_UseContactReceiverEligibility_AndKeepImpactReceivers()
    {
        RigidBody3D ball = LoadPackedScene(TestBallScenePath).Instantiate<RigidBody3D>();
        RigidBody3D stick = LoadPackedScene(TestStickScenePath).Instantiate<RigidBody3D>();

        try
        {
            AssertMigratedPropContract(ball, "test_ball.tscn");
            AssertMigratedPropContract(stick, "test_stick.tscn");
        }
        finally
        {
            ball.Free();
            stick.Free();
        }
    }

    private static void AssertMigratedPropContract(RigidBody3D prop, string sceneName)
    {
        Assert.True(
            prop.IsInGroup(ContactReceiverGroup.Name),
            $"{sceneName} must opt in through the contact_receiver group after the legacy migration.");
        Assert.Equal(2U, prop.CollisionLayer);
        Assert.Equal(11U, prop.CollisionMask);
        RigidBodyImpactInteractionReceiver3D receiver =
            Assert.IsType<RigidBodyImpactInteractionReceiver3D>(prop.GetNode("ImpactInteractionReceiver"));
        Assert.Contains("ImpactReceiver", receiver.AuthoredTags);
    }

    /// <summary>
    /// Verifies a striking hand delivers the INTR-004 impact receipt and impulse to the authored test ball
    /// through the generic contact-receiver eligibility — on the authored collision layer and on an
    /// unrelated one — while a <see cref="ContactSource" /> sweeper discovers the same prop through the
    /// same eligibility.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StrikingHand_MigratedBall_KeepsImpactReceiptsUnderGenericEligibility()
    {
        SceneTree sceneTree = GetSceneTree();
        MigrationFixture fixture = await MigrationFixture.CreateAsync(sceneTree);

        try
        {
            int authoredLayerReceipts = await fixture.StrikeOnceAsync();
            AssertImpactDelivered(fixture, authoredLayerReceipts, "authored collision layer");

            // TR15: eligibility is the group, not any collision-layer arrangement — moving the ball to a
            // layer with no hand-interaction history must not change the delivered behaviour.
            fixture.Ball.CollisionLayer = 4;
            int unrelatedLayerReceipts = await fixture.StrikeOnceAsync();
            AssertImpactDelivered(fixture, unrelatedLayerReceipts, "unrelated collision layer");

            Assert.True(
                fixture.HandSweeper.LastSweepReceiverCountForTesting == 1,
                "The INTR-005 hand sweeper must discover the migrated prop through the same contact_receiver " +
                $"eligibility, observed {fixture.HandSweeper.LastSweepReceiverCountForTesting} receivers.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static void AssertImpactDelivered(MigrationFixture fixture, int receiptCount, string layerContext)
    {
        Assert.True(
            receiptCount > 0,
            $"Expected the authored ball impact receiver to emit an impact receipt on the {layerContext}.");
        IImpactPhysicalInteraction interaction =
            Assert.IsAssignableFrom<IImpactPhysicalInteraction>(fixture.LatestReceipt);
        Assert.Same(fixture.Channel, interaction.Source);
        Assert.Contains("ImpactSource", fixture.Channel.Tags);
        Assert.Contains("HandBody", fixture.Channel.Tags);
        Assert.True(
            interaction.Velocity.X > 1.0f,
            $"Expected the strike approach velocity on the receipt, observed {interaction.Velocity}.");
        Assert.InRange(Mathf.Abs(interaction.ContactPoint.X - fixture.Ball.GlobalPosition.X), 0.0f, 0.10f);
        Assert.True(
            fixture.Ball.LinearVelocity.X > 0.10f,
            $"Expected the channel impulse to move the ball on the {layerContext}, observed velocity {fixture.Ball.LinearVelocity}.");
    }

    private sealed class MigrationFixture(
        Node3D root,
        AnimatableBody3D handBody,
        RigidBody3D ball,
        IKTargetAnimatableActuator actuator,
        ContactSource handSweeper)
    {
        private const float PrimeHandX = 0.12f;

        private readonly List<IImpactPhysicalInteraction> _receipts = [];

        public AnimatableBody3D HandBody { get; } = handBody;

        public RigidBody3D Ball { get; } = ball;

        public HandPhysicalInteractionChannel Channel { get; } = actuator.PhysicalInteractionChannelForTesting;

        public ContactSource HandSweeper { get; } = handSweeper;

        public IImpactPhysicalInteraction? LatestReceipt
        {
            get; private set;
        }

        public static async Task<MigrationFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "PropImpactMigrationTestRoot",
            };

            // The hand body sits on a collision layer outside the ball's mask so only the explicit
            // interaction channel and sweeper queries touch the prop.
            AnimatableBody3D handBody = new()
            {
                Name = "HandBody",
                TopLevel = true,
                SyncToPhysics = false,
                CollisionLayer = 16,
                CollisionMask = 1,
                GlobalPosition = _fixtureOrigin,
            };
            handBody.AddChild(new CollisionShape3D
            {
                Name = "CollisionShape3D",
                Shape = new BoxShape3D { Size = _boxSize },
            });

            RigidBody3D ball = LoadPackedScene(TestBallScenePath).Instantiate<RigidBody3D>();
            ball.TopLevel = true;
            ball.GlobalPosition = _ballPosition;
            ball.GravityScale = 0.0f;

            root.AddChild(handBody);
            root.AddChild(ball);

            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);
            await WaitForFramesAsync(sceneTree, 1);

            // The INTR-005 player-hand sweeper rides the same hand body; it shares the contact_receiver
            // eligibility with the channel.
            ContactSource handSweeper = new()
            {
                Name = "RightHandContactSource",
                PartyDisplayName = "HandBody",
            };
            handBody.AddChild(handSweeper);

            IKTargetAnimatableActuator actuator = new(handBody)
            {
                MaximumSpeed = 100.0f,
                MaximumAcceleration = 400.0f,
                PositionResponsiveness = 100.0f,
                RotationResponsiveness = 100.0f,
                SnapDistance = 0.001f,
                ImpactApproachSpeedThreshold = 0.05f,
                ImpactImpulsePerSpeed = 10.0f,
                ImpactImpulseCap = 0.50f,
                SustainedForcePerSpeed = 0.0f,
                SustainedForceCap = 0.0f,
            };

            RigidBodyImpactInteractionReceiver3D receiver =
                ball.GetNode<RigidBodyImpactInteractionReceiver3D>("ImpactInteractionReceiver");
            MigrationFixture fixture = new(root, handBody, ball, actuator, handSweeper);
            receiver.PhysicalInteractionReceived += fixture.OnReceiptReceived;

            await WaitForPhysicsFramesAsync(sceneTree, 3);
            ResetBall(ball);

            return fixture;
        }

        /// <summary>Returns the number of impact receipts emitted during the strike.</summary>
        public async Task<int> StrikeOnceAsync()
        {
            // Prime by teleport, mirroring the legacy fixture: retreating through the actuator would leave
            // the hand overlapping the prop while it decelerates, marking the contact continuous.
            HandBody.GlobalTransform = new Transform3D(Basis.Identity, ToWorld(PrimeHandX));
            HandBody.ForceUpdateTransform();
            IKTargetFollowState primeState = new(new Transform3D(Basis.Identity, ToWorld(PrimeHandX)), active: true);
            _ = actuator.Actuate(new IKTargetPipelineRequest(primeState, primeState), PhysicsStepSeconds);
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
            ResetBall(Ball);
            _receipts.Clear();
            LatestReceipt = null;
            // One physics frame lets the reset teleport reach the physics server before the strike queries it.
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);

            Actuate(ToWorld(0.20f));
            await WaitForPhysicsFramesAsync(GetSceneTree(), 1);
            return _receipts.Count;
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(root) && root.IsInsideTree())
            {
                root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        private void OnReceiptReceived(PhysicalInteractionReceipt receipt, string[] tags)
        {
            if (Assert.IsAssignableFrom<IImpactPhysicalInteraction>(receipt.Interaction) is { } impact)
            {
                _receipts.Add(impact);
                LatestReceipt = impact;
            }
        }

        private void Actuate(Vector3 worldPosition)
        {
            IKTargetFollowState followState = new(new Transform3D(Basis.Identity, worldPosition), active: true);
            _ = actuator.Actuate(new IKTargetPipelineRequest(followState, followState), PhysicsStepSeconds);
        }

        private static void ResetBall(RigidBody3D ball)
        {
            ball.GlobalPosition = _ballPosition;
            ball.LinearVelocity = Vector3.Zero;
            ball.AngularVelocity = Vector3.Zero;
            ball.Sleeping = false;
            ball.ForceUpdateTransform();
        }

        private static Vector3 ToWorld(float localX)
            => _fixtureOrigin + new Vector3(localX, 0.0f, 0.0f);
    }
}
