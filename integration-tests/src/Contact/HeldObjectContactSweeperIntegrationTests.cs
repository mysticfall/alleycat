using AlleyCat.Contact;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Held-object sweeper coverage (INTR-005 plan step 6, TR13, AC-T8): a carried object — a frozen kinematic
/// rigid body shaped like a stick, driven by direct transforms the way a held movable prop is — hosts a
/// generic <see cref="ContactSource" /> sweeper on its own body and produces classified receiver events
/// with unowned-object counterpart attribution. The object's sweep follows shape changes at a constant
/// shape count — a resource swap resolves live, a shape-node replacement rebuilds the query set — closing
/// the staleness heuristic gap flagged in the step-5 review.
/// </summary>
public sealed class HeldObjectContactSweeperIntegrationTests
{
    private const float ChestHalfDepth = 0.09f;

    // The stick is carried horizontally; its long axis is X, its carrying thickness Z.
    private static readonly Vector3 _carriedStickSize = new(0.30f, 0.05f, 0.05f);
    private static readonly Vector3 _bulkyStickSize = new(0.30f, 0.12f, 0.12f);

    private static readonly Vector3 _fixtureOrigin = new(120.0f, 14.0f, 0.0f);

    /// <summary>
    /// Verifies a carried stick sweeping a character receiver classifies Touch at Begin with the object's
    /// unowned counterpart attribution — no owner, the bare object name as descriptor — and the receiver's
    /// body part and bone-local direction (INTR-005 TR13, TR8, AC-T8).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CarriedStick_OnReceiverChest_DeliversUnownedObjectAttributedContact()
    {
        SceneTree sceneTree = GetSceneTree();
        HeldObjectFixture fixture = await HeldObjectFixture.CreateAsync(sceneTree, _carriedStickSize);

        try
        {
            // Carry the stick toward the chest in ≤ 0.02 m physics steps: ~1.2 m/s stays below the impact
            // threshold, so contact classifies Touch at Begin on the chest front face.
            float touchZ = ChestHalfDepth + (_carriedStickSize.Z * 0.5f) - 0.005f;
            await fixture.MoveStickAsync(sceneTree, 0.20f);
            await fixture.MoveStickAsync(sceneTree, 0.18f);
            await fixture.MoveStickAsync(sceneTree, 0.16f);
            await fixture.MoveStickAsync(sceneTree, 0.14f);
            await fixture.MoveStickAsync(sceneTree, 0.12f);
            await fixture.MoveStickAsync(sceneTree, touchZ);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            ContactEvent touch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Null(touch.PreviousClass);
            Assert.Equal("Chest", touch.Receiver.DisplayName);
            Assert.Equal("Sky", touch.Receiver.OwnerName);
            Assert.Equal("Sky's Chest", touch.Receiver.Descriptor);

            // Unowned-object attribution: the stick belongs to no character, so the owner is null and the
            // descriptor is the bare object name.
            Assert.Equal("Walking Stick", touch.Counterpart.DisplayName);
            Assert.Null(touch.Counterpart.OwnerName);
            Assert.Equal("Walking Stick", touch.Counterpart.Descriptor);

            Assert.True(
                touch.Direction.Z >= 0.8f,
                $"The chest contact direction should resolve to the chest bone-local +Z axis; was " +
                $"{touch.Direction}.");

            // The sweeper swept the object's own body shape and coalesced the single chest receiver.
            Assert.Equal(1, fixture.StickSource.QueryShapeCountForTesting);
            Assert.Equal(1, fixture.StickSource.LastSweepReceiverCountForTesting);
            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal("Chest", active.Receiver.DisplayName);
            Assert.Equal("Walking Stick", active.Source.DisplayName);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies shape changes at a constant shape count are not missed: swapping the
    /// <see cref="Shape3D" /> resource on the stick's one shape node resolves live — the sweep stops
    /// touching when the geometry shrinks — and replacing the shape node itself rebuilds the query set —
    /// the sweep touches again with the new node's geometry (INTR-005 TR13; step-5 review preamble).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ShapeChanges_AtConstantShapeCount_AreNotMissed()
    {
        SceneTree sceneTree = GetSceneTree();
        HeldObjectFixture fixture = await HeldObjectFixture.CreateAsync(sceneTree, _bulkyStickSize);

        try
        {
            // Phase A — the bulky stick overlaps the chest front face: one Touch, one active raw contact.
            float bulkyTouchZ = ChestHalfDepth + (_bulkyStickSize.Z * 0.5f) - 0.005f;
            await fixture.MoveStickAsync(sceneTree, 0.20f);
            await fixture.MoveStickAsync(sceneTree, 0.18f);
            await fixture.MoveStickAsync(sceneTree, 0.16f);
            await fixture.MoveStickAsync(sceneTree, 0.14f);
            await fixture.MoveStickAsync(sceneTree, bulkyTouchZ);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            ContactEvent firstTouch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, firstTouch.Class);
            Assert.Equal(1, fixture.StickSource.QueryShapeCountForTesting);
            Assert.Equal(1, fixture.StickSource.LastSweepReceiverCountForTesting);

            // Phase B — a pure resource swap on the same node, shape count unchanged: the thin geometry no
            // longer reaches the chest, so the contact ends with no further classified emission.
            CollisionShape3D stickShape = fixture.StickShape;
            stickShape.Shape = new BoxShape3D
            {
                Size = _carriedStickSize,
            };
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Equal(1, fixture.StickSource.QueryShapeCountForTesting);
            Assert.Equal(0, fixture.StickSource.LastSweepReceiverCountForTesting);
            _ = Assert.Single(fixture.Events);
            Assert.Empty(fixture.Monitor.GetActiveRawContacts());

            // Phase C — replace the shape node itself at a constant shape count: the sweeper rebuilds its
            // query set for the new node and the bulky geometry touches the chest again as a new contact.
            stickShape.Free();
            CollisionShape3D replacementShape = new()
            {
                Name = "ReplacementStickShape",
                Shape = new BoxShape3D
                {
                    Size = _bulkyStickSize,
                },
            };
            fixture.Stick.AddChild(replacementShape);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Equal(1, fixture.StickSource.QueryShapeCountForTesting);
            Assert.Equal(1, fixture.StickSource.LastSweepReceiverCountForTesting);

            Assert.Equal(2, fixture.Events.Count);
            ContactEvent secondTouch = fixture.Events[1];
            Assert.Equal(ContactClass.Touch, secondTouch.Class);
            Assert.Null(secondTouch.PreviousClass);
            Assert.NotEqual(firstTouch.Contact, secondTouch.Contact);
            RawContactSnapshot reactivated = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal("Chest", reactivated.Receiver.DisplayName);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Builds the held-object wiring: a character receiver — generated chest and head proxies with a
    /// monitor — and the carried stick: a frozen kinematic <see cref="RigidBody3D" /> hosting a
    /// <see cref="ContactSource" /> sweeper on its own body, standing in for a movable prop while held.
    /// </summary>
    private sealed class HeldObjectFixture(
        Node3D root,
        DynamicPhysicalRig rig,
        ContactMonitor monitor,
        List<ContactEvent> events,
        RigidBody3D stick,
        CollisionShape3D stickShape,
        ContactSource stickSource)
    {
        internal static readonly (string Name, string? Parent, Vector3 LocalPosition)[] BoneDefinitions =
        [
            ("Hips", null, new Vector3(0.0f, 0.98f, 0.0f)),
            ("Spine", "Hips", new Vector3(0.0f, 0.12f, 0.0f)),
            ("Chest", "Spine", new Vector3(0.0f, 0.18f, 0.0f)),
            ("Neck", "Chest", new Vector3(0.0f, 0.18f, 0.0f)),
            ("Head", "Neck", new Vector3(0.0f, 0.16f, 0.0f)),
        ];

        public DynamicPhysicalRig Rig { get; } = rig;

        public ContactMonitor Monitor { get; } = monitor;

        public IReadOnlyList<ContactEvent> Events { get; } = events;

        public RigidBody3D Stick { get; } = stick;

        public CollisionShape3D StickShape { get; } = stickShape;

        public ContactSource StickSource { get; } = stickSource;

        private Node3D Root { get; } = root;

        public static async Task<HeldObjectFixture> CreateAsync(SceneTree sceneTree, Vector3 stickSize)
        {
            Node3D root = new()
            {
                Name = "HeldObjectTestRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            Node3D receiverRoot = new()
            {
                Name = "Receiver",
            };
            root.AddChild(receiverRoot);

            Skeleton3D skeleton = CreateReceiverSkeleton();
            receiverRoot.AddChild(skeleton);

            DynamicPhysicalRig rig = new()
            {
                Name = "ReceiverPhysicalRig",
                TargetSkeleton = skeleton,
                ColliderProfile = new BodyColliderProfile
                {
                    SourceScene = CreateColliderSourceScene(),
                },
            };
            skeleton.AddChild(rig);

            ContactMonitor monitor = new()
            {
                Name = "ReceiverContactMonitor",
                OwnerName = "Sky",
            };
            receiverRoot.AddChild(monitor);

            List<ContactEvent> events = [];
            monitor.ContactEventEmitted += events.Add;

            // The carried object: a frozen kinematic rigid body driven by direct transforms — the state a
            // held movable prop is driven in — hosting the sweeper on its own body.
            RigidBody3D stick = new()
            {
                Name = "WalkingStick",
                CollisionLayer = 2,
                CollisionMask = 0,
                Freeze = true,
                FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic,
            };
            CollisionShape3D stickShape = new()
            {
                Name = "StickShape",
                Shape = new BoxShape3D
                {
                    Size = stickSize,
                },
            };
            stick.AddChild(stickShape);
            ContactSource stickSource = new()
            {
                Name = "WalkingStickContactSource",
                PartyDisplayName = "Walking Stick",
            };
            stick.AddChild(stickSource);
            root.AddChild(stick);
            stick.GlobalPosition = _fixtureOrigin + new Vector3(0.0f, 1.28f, 0.40f);

            // One process frame lets the rig's deferred generation run; the physics frames prime the
            // sweeper's finite-difference velocity sampling with a still object.
            await WaitForFramesAsync(sceneTree, 1);
            Assert.True(rig.GeneratedProxyCount >= 2, "The receiver rig should generate Chest and Head receiver proxies.");
            PlaceAttachmentsAtBoneRests(rig);
            await WaitForPhysicsFramesAsync(sceneTree, 5);

            return new HeldObjectFixture(root, rig, monitor, events, stick, stickShape, stickSource);
        }

        public async Task MoveStickAsync(SceneTree sceneTree, float approachZ)
        {
            Stick.GlobalPosition = _fixtureOrigin + new Vector3(0.0f, 1.28f, approachZ);
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        private static void PlaceAttachmentsAtBoneRests(DynamicPhysicalRig rig)
        {
            Skeleton3D skeleton = rig.TargetSkeleton
                ?? throw new InvalidOperationException("Expected the rig to have resolved its target skeleton.");

            foreach ((string boneName, _, _) in BoneDefinitions)
            {
                int boneIndex = skeleton.FindBone(boneName);
                if (boneIndex < 0)
                {
                    continue;
                }

                foreach (PhysicsBody3D proxy in rig.GetGeneratedProxyBodiesForBone(boneName))
                {
                    if (proxy.GetParent() is BoneAttachment3D attachment)
                    {
                        attachment.Transform = skeleton.GetBoneGlobalRest(boneIndex);
                    }
                }
            }
        }

        private static Skeleton3D CreateReceiverSkeleton()
        {
            Skeleton3D skeleton = new()
            {
                Name = "ReceiverSkeleton",
            };

            Dictionary<string, int> boneIndices = [];
            foreach ((string name, _, _) in BoneDefinitions)
            {
                boneIndices[name] = skeleton.AddBone(name);
            }

            foreach ((string name, string? parent, Vector3 position) in BoneDefinitions)
            {
                int boneIndex = boneIndices[name];
                skeleton.SetBoneRest(boneIndex, new Transform3D(Basis.Identity, position));
                if (parent is not null)
                {
                    skeleton.SetBoneParent(boneIndex, boneIndices[parent]);
                }
            }

            for (int boneIndex = 0; boneIndex < skeleton.GetBoneCount(); boneIndex += 1)
            {
                skeleton.SetBonePose(boneIndex, skeleton.GetBoneRest(boneIndex));
            }

            return skeleton;
        }

        private static PackedScene CreateColliderSourceScene()
        {
            Node colliderRoot = new()
            {
                Name = "CollidersRoot",
            };

            CreateSourceAttachment(colliderRoot, "Chest", new Vector3(0.24f, 0.44f, 0.18f));
            CreateSourceAttachment(colliderRoot, "Head", new Vector3(0.18f, 0.20f, 0.18f));

            PackedScene sourceScene = new();
            Error packResult = sourceScene.Pack(colliderRoot);
            colliderRoot.Free();

            Assert.Equal(Error.Ok, packResult);
            return sourceScene;
        }

        private static void CreateSourceAttachment(Node colliderRoot, string boneName, Vector3 boxSize)
        {
            BoneAttachment3D sourceBoneAttachment = new()
            {
                Name = $"{boneName}Attachment",
                BoneName = boneName,
            };
            AnimatableBody3D sourceBody = new()
            {
                Name = $"{boneName}SourceBody",
            };
            CollisionShape3D sourceShape = new()
            {
                Name = $"{boneName}SourceShape",
                Shape = new BoxShape3D
                {
                    Size = boxSize,
                },
            };

            colliderRoot.AddChild(sourceBoneAttachment);
            sourceBoneAttachment.Owner = colliderRoot;
            sourceBoneAttachment.AddChild(sourceBody);
            sourceBody.Owner = colliderRoot;
            sourceBody.AddChild(sourceShape);
            sourceShape.Owner = colliderRoot;
        }
    }
}
