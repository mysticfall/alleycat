using AlleyCat.Contact;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Ground-receiver coverage of the contact detection chain (INTR-005 plan step 5, UR5, TR10, TR11): an
/// opted-in world floor — no skeleton, no bone anchor, no authored regions — receives a scripted footstep
/// driven by a generic <see cref="ContactSource" /> on a kinematic foot, through the same
/// <see cref="ContactMonitor.ContactEventEmitted" /> delivery path as character body receivers. The event
/// carries the foot's owner attribution in the counterpart party, the direction in the floor's body-local
/// frame (the non-bone-anchored receiver fallback), and the raw layer shows the static floor sampling zero
/// receiver velocity against the descending foot (TR18f).
/// </summary>
public sealed class GroundReceiverFootstepIntegrationTests
{
    private const float FootHalfHeight = 0.04f;
    private const float FloorHalfHeight = 0.10f;

    // A gentle step: the descent stays below the impact threshold, then the character weights onto the foot.
    private const float TouchOffsetY = FloorHalfHeight + FootHalfHeight - 0.005f;
    private const float PressOffsetY = FloorHalfHeight + FootHalfHeight - 0.028f;
    private const float StartOffsetY = 0.45f;

    private static readonly Vector3 _fixtureOrigin = new(80.0f, 12.0f, 0.0f);

    /// <summary>
    /// Verifies a footstep onto an opted-in floor classifies Touch then Press — a representative footstep
    /// lifecycle — with foot owner attribution in the payload, the direction resolved in the floor's
    /// body-local frame, and meaningful relative velocity from the foot against a zero-velocity static
    /// receiver (INTR-005 UR5, UR4, TR10, TR11, TR18f).
    /// </summary>
    [Headless]
    [Fact]
    public async Task FootstepOnOptedInFloor_DeliversFootAttributedContactThroughSharedPath()
    {
        SceneTree sceneTree = GetSceneTree();
        FootstepFixture fixture = await FootstepFixture.CreateAsync(sceneTree);

        try
        {
            // Slow descent in air — each 0.02 m step at 60 Hz is ~1.2 m/s, below the 1.5 m/s impact threshold.
            await fixture.MoveFootAsync(sceneTree, WorldY(0.25f));
            await fixture.MoveFootAsync(sceneTree, WorldY(0.23f));
            await fixture.MoveFootAsync(sceneTree, WorldY(0.21f));
            await fixture.MoveFootAsync(sceneTree, WorldY(0.19f));
            await fixture.MoveFootAsync(sceneTree, WorldY(0.17f));
            await fixture.MoveFootAsync(sceneTree, WorldY(0.15f));
            await fixture.MoveFootAsync(sceneTree, WorldY(TouchOffsetY));

            // The Begin sample carries the descent itself: the static floor samples zero receiver velocity, so
            // the relative velocity is exactly the foot's −Y approach (TR18f).
            RawContactSnapshot beginContact = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal("Floor", beginContact.Receiver.DisplayName);
            System.Numerics.Vector3 approachVelocity = beginContact.LastSample.RelativeVelocity;
            Assert.True(
                approachVelocity.Y is <= -0.75f and >= -1.05f,
                $"The descending foot at ~0.9 m/s against a static floor should give a −Y relative velocity " +
                $"near 0.9; was {approachVelocity}.");
            Assert.True(
                MathF.Abs(approachVelocity.X) <= 0.1f && MathF.Abs(approachVelocity.Z) <= 0.1f,
                $"The approach should stay on the floor normal axis; was {approachVelocity}.");

            // Weight onto the foot: press in past the depth threshold and hold through the confirmation window.
            await fixture.MoveFootAsync(sceneTree, WorldY(0.125f));
            await fixture.MoveFootAsync(sceneTree, WorldY(0.118f));
            await fixture.MoveFootAsync(sceneTree, WorldY(PressOffsetY));
            await FootstepFixture.HoldFootAsync(sceneTree, 14);

            Assert.Equal(
            [
                ContactClass.Touch,
                ContactClass.Press,
            ], fixture.Events.Select(contactEvent => contactEvent.Class));

            ContactEvent press = fixture.Events[1];
            Assert.Equal(ContactClass.Touch, press.PreviousClass);
            Assert.True(
                press.Duration >= TimeSpan.FromSeconds(0.1),
                $"The sustained step should confirm after the 0.15 s window; duration was " +
                $"{press.Duration.TotalSeconds:F3} s.");
            Assert.True(
                press.Intensity >= 0.01f,
                $"The press intensity should report the sustained penetration depth; intensity was " +
                $"{press.Intensity:F4} m.");

            // Owner attribution rides in the event payload, never in the delivery mechanism (TR11): the floor
            // party is an unowned world surface, the foot party carries its character.
            Assert.Equal("Floor", press.Receiver.DisplayName);
            Assert.Null(press.Receiver.OwnerName);
            Assert.Equal("Floor", press.Receiver.Descriptor);
            Assert.Equal("Left Foot", press.Counterpart.DisplayName);
            Assert.Equal("Mochi", press.Counterpart.OwnerName);
            Assert.Equal("Mochi's Left Foot", press.Counterpart.Descriptor);

            // The floor body rolls +90° about X while its shape counter-rotates, so the world collider is a
            // plain flat box but the floor's own frame differs from the world frame: the world +Y surface
            // normal must resolve to the floor-local −Z. Resolving through the world frame would report +Y.
            Assert.True(
                press.Direction.Z <= -0.8f && MathF.Abs(press.Direction.X) <= 0.35f
                && MathF.Abs(press.Direction.Y) <= 0.35f,
                $"The body-local direction should resolve to −Z under the rolled floor frame; direction was " +
                $"{press.Direction}.");

            // A static foot on a static floor settles to zero relative velocity — the receiver contributed no
            // phantom motion of its own.
            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Assert.True(
                active.LastSample.RelativeVelocity.Length() <= 0.1f,
                $"A static foot on a static floor should report a settled zero relative velocity; was " +
                $"{active.LastSample.RelativeVelocity}.");

            Assert.Equal(RawContactPhase.Begin, fixture.Monitor.GetRecentRawEvents()[0].Phase);
            Assert.Equal(1, fixture.FootSource.QueryShapeCountForTesting);
            Assert.Equal(1, fixture.FootSource.LastSweepReceiverCountForTesting);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static float WorldY(float offsetFromOriginY) => _fixtureOrigin.Y + offsetFromOriginY;

    /// <summary>
    /// Builds the ground-receiver wiring: a world floor that opts into receiving through the
    /// <c>contact_receiver</c> group (TR10) and hosts a <see cref="ContactMonitor" /> as an unowned world
    /// surface, plus a kinematic foot body carrying a generic <see cref="ContactSource" /> sweeper with foot
    /// attribution. The floor body rolls +90° about X with its collision shape counter-rotated, so the world
    /// collider geometry is unchanged while the floor's body frame provably differs from the world frame the
    /// non-bone-anchored fallback must resolve through.
    /// </summary>
    private sealed class FootstepFixture(
        Node3D root,
        ContactMonitor monitor,
        AnimatableBody3D footBody,
        ContactSource footSource,
        List<ContactEvent> events)
    {
        public ContactMonitor Monitor { get; } = monitor;

        public ContactSource FootSource { get; } = footSource;

        public IReadOnlyList<ContactEvent> Events { get; } = events;

        public static async Task<FootstepFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "GroundReceiverTestRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            StaticBody3D floor = new()
            {
                Name = "Floor",
                Transform = new Transform3D(
                    Basis.FromEuler(new Vector3(Mathf.Pi * 0.5f, 0.0f, 0.0f)),
                    Vector3.Zero),
            };
            floor.AddChild(new CollisionShape3D
            {
                Name = "FloorShape",
                Shape = new BoxShape3D
                {
                    Size = new Vector3(4.0f, 0.2f, 4.0f),
                },
                Transform = new Transform3D(
                    Basis.FromEuler(new Vector3(-Mathf.Pi * 0.5f, 0.0f, 0.0f)),
                    Vector3.Zero),
            });
            // World opt-in (TR10): receiver capability hosted by an arbitrary physics body through the group —
            // the same eligibility contract generated character body parts join.
            floor.AddToGroup(ContactReceiverGroup.Name);
            root.AddChild(floor);

            ContactMonitor monitor = new()
            {
                Name = "FloorContactMonitor",
            };
            floor.AddChild(monitor);

            AnimatableBody3D footBody = new()
            {
                Name = "MochiLeftFoot",
                CollisionLayer = 8,
                CollisionMask = 0,
                SyncToPhysics = false,
            };
            footBody.AddChild(new CollisionShape3D
            {
                Name = "FootShape",
                Shape = new BoxShape3D
                {
                    Size = new Vector3(0.10f, 0.08f, 0.24f),
                },
            });
            root.AddChild(footBody);
            footBody.GlobalPosition = new Vector3(_fixtureOrigin.X, WorldY(StartOffsetY), _fixtureOrigin.Z);

            ContactSource footSource = new()
            {
                Name = "LeftFootContactSource",
                PartyDisplayName = "Left Foot",
                OwnerName = "Mochi",
            };
            footBody.AddChild(footSource);

            List<ContactEvent> events = [];
            monitor.ContactEventEmitted += events.Add;

            // The physics frames prime the sweeper's finite-difference velocity sampling with a still foot.
            await WaitForPhysicsFramesAsync(sceneTree, 5);

            return new FootstepFixture(root, monitor, footBody, footSource, events);
        }

        public async Task MoveFootAsync(SceneTree sceneTree, float worldY)
        {
            footBody.GlobalPosition = new Vector3(_fixtureOrigin.X, worldY, _fixtureOrigin.Z);
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        public static async Task HoldFootAsync(SceneTree sceneTree, int physicsFrames)
            => await WaitForPhysicsFramesAsync(sceneTree, physicsFrames);

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(root) && root.IsInsideTree())
            {
                root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }
}
