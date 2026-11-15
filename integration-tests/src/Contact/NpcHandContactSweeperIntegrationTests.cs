using AlleyCat.Contact;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Production NPC hand sweeper coverage (INTR-005 plan step 6, TR13, TR18e, AC-T8): the
/// <see cref="HandContactSweeperController" /> hosts profile-backed query-shape sweepers on the NPC's
/// arm-IK hand target, wired into the NPC template scene shape. The swept query shapes come from the rig's
/// collider profile without registering any collider on the hand target, the RIG-001 hand-target exception
/// topology is wired before the sweeper can sweep and re-supplied after rig regeneration, and contacts
/// deliver through the shared receiver path with NPC owner attribution resolved from the character hub.
/// </summary>
public sealed class NpcHandContactSweeperIntegrationTests
{
    private const float HandSize = 0.12f;
    private const float ChestHalfDepth = 0.09f;
    private const float TouchZ = ChestHalfDepth + (HandSize * 0.5f) - 0.005f;

    // The folded right arm rests with its hand in front of the chest, clear of it.
    private static readonly Vector3 _handTargetRestLocal = new(0.10f, 1.16f, 0.27f);
    private static readonly Vector3 _lowerArmProxyLocal = new(0.22f, 1.16f, 0.17f);

    private static readonly Vector3 _fixtureOrigin = new(110.0f, 14.0f, 0.0f);

    // The player-role receiver stands facing the NPC, its chest front toward the NPC's hand.
    private static readonly Vector3 _receiverOffsetFromNpc = new(0.0f, 0.0f, 1.2f);

    /// <summary>
    /// Verifies the production NPC hand sweeper delivers a classified contact to another character's
    /// receiver: the NPC's right-hand IK target — moved kinematically, standing in for the arm-IK motion —
    /// approaches a player-role receiver's chest, and the profile-backed query shapes classify Touch at Begin
    /// with NPC owner attribution resolved from the character hub (INTR-005 TR13, TR18e, AC-T8).
    /// </summary>
    [Headless]
    [Fact]
    public async Task NpcHandSweeper_OnPlayerReceiver_DeliversNpcAttributedContact()
    {
        SceneTree sceneTree = GetSceneTree();
        NpcSweeperFixture fixture = await NpcSweeperFixture.CreateAsync(sceneTree, includePlayerReceiver: true);

        try
        {
            // The controller hosted exactly one sweeper per wired hand target; only the right is wired.
            ContactSource sweeper = Assert.Single(fixture.Controller.HandSweepers);
            Assert.Equal("RightHandContactSource", sweeper.Name);
            Assert.Equal(1, sweeper.QueryShapeCountForTesting);
            Assert.Empty(fixture.NpcEvents);

            // Straight-line approach in ≤ 0.02 m physics steps: ~1.2 m/s stays below the impact threshold,
            // so contact classifies Touch at Begin on the player receiver's chest front face.
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.PlayerChestTouchWorld(),
                maxStepMetres: 0.02f);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            ContactEvent touch = Assert.Single(fixture.PlayerEvents);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Null(touch.PreviousClass);
            Assert.Equal("Chest", touch.Receiver.DisplayName);
            Assert.Equal("Player", touch.Receiver.OwnerName);
            Assert.Equal("Player's Chest", touch.Receiver.Descriptor);
            Assert.Equal("Right Hand", touch.Counterpart.DisplayName);
            Assert.Equal("Sky", touch.Counterpart.OwnerName);
            Assert.Equal("Sky's Right Hand", touch.Counterpart.Descriptor);

            // The yaw-rotated receiver faces the NPC, so the chest bone-local +Z axis points toward the
            // approaching hand; the contact direction resolves onto it.
            Assert.True(
                touch.Direction.Z >= 0.8f,
                $"The chest contact direction should resolve to the chest bone-local +Z axis; was " +
                $"{touch.Direction}.");

            Assert.Equal(1, sweeper.LastSweepReceiverCountForTesting);
            Assert.Empty(fixture.NpcEvents);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the controller-wired RIG-001 exception topology: the NPC hand target resting fully on its
    /// own hand proxy and then sliding onto its own lower-arm proxy — both inside the hand-target exception
    /// set — discovers no receivers and emits nothing, although the geometry genuinely overlaps
    /// (INTR-005 TR12, TR18e).
    /// </summary>
    [Headless]
    [Fact]
    public async Task NpcHandSweeper_OwnHandAndForearmProxies_NeverReport()
    {
        SceneTree sceneTree = GetSceneTree();
        NpcSweeperFixture fixture = await NpcSweeperFixture.CreateAsync(sceneTree, includePlayerReceiver: false);

        try
        {
            ContactSource sweeper = Assert.Single(fixture.Controller.HandSweepers);

            // Independent oracle: the hand target fully coincides with its own hand proxy — identical
            // 0.12 m boxes at the same centre — so an unexcluded sweep would certainly discover it.
            PhysicsBody3D handProxy = Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Hand"));
            Assert.True(
                fixture.HandTarget.GlobalPosition.DistanceTo(handProxy.GlobalPosition) <= 0.005f,
                $"The hand target should fully overlap its own hand proxy; target was " +
                $"{fixture.HandTarget.GlobalPosition}, proxy was {handProxy.GlobalPosition}.");

            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Empty(fixture.NpcEvents);
            Assert.Empty(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal(0, sweeper.LastSweepReceiverCountForTesting);

            // Slide onto the forearm proxy — the adjacent-bone excluded pair (RIG-001 TR12).
            await fixture.MoveHandTargetTowardsAsync(sceneTree, fixture.NpcWorld(_lowerArmProxyLocal), 0.02f);
            PhysicsBody3D lowerArmProxy = Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Lower Arm"));
            Assert.True(
                fixture.HandTarget.GlobalPosition.DistanceTo(lowerArmProxy.GlobalPosition) <= 0.005f,
                $"The hand target should fully overlap its own lower-arm proxy; target was " +
                $"{fixture.HandTarget.GlobalPosition}, proxy was {lowerArmProxy.GlobalPosition}.");

            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Empty(fixture.NpcEvents);
            Assert.Empty(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal(0, sweeper.LastSweepReceiverCountForTesting);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the wiring-order contract honour after rig regeneration (step-6 preamble): rebuilding the
    /// rig replaces every proxy, and the controller re-supplies the sweeper's excluded receivers with the
    /// new instances before the next sweep — the excluded self pairs stay silent and a co-existing pair
    /// still delivers self-contact through the same path (INTR-005 TR12, TR18e).
    /// </summary>
    [Headless]
    [Fact]
    public async Task RigRegeneration_ReSuppliesExclusions_BeforeTheNextSweep()
    {
        SceneTree sceneTree = GetSceneTree();
        NpcSweeperFixture fixture = await NpcSweeperFixture.CreateAsync(sceneTree, includePlayerReceiver: false);

        try
        {
            ContactSource sweeper = Assert.Single(fixture.Controller.HandSweepers);

            PhysicsBody3D oldHandProxy = Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Hand"));
            PhysicsBody3D oldLowerArmProxy = Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Lower Arm"));
            ulong oldHandProxyId = oldHandProxy.GetInstanceId();
            ulong oldLowerArmProxyId = oldLowerArmProxy.GetInstanceId();

            // Park the hand target clear of the body while the rig rebuilds.
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(_handTargetRestLocal + new Vector3(0.0f, 0.0f, 0.40f)),
                0.02f);

            fixture.Rig.RegenerateNow();
            await WaitForFramesAsync(sceneTree, 1);
            NpcSweeperFixture.PlaceAttachmentsAtBoneRests(fixture.Rig);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            // The controller still hosts exactly one sweeper per hand — regeneration re-wires, it never
            // duplicates — and its exclusion set now references the regenerated proxies, every entry valid.
            _ = Assert.Single(fixture.Controller.HandSweepers);
            PhysicsBody3D newHandProxy = Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Hand"));
            PhysicsBody3D newLowerArmProxy = Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Lower Arm"));
            Assert.NotEqual(oldHandProxyId, newHandProxy.GetInstanceId());
            Assert.NotEqual(oldLowerArmProxyId, newLowerArmProxy.GetInstanceId());
            Assert.All(sweeper.ExcludedReceivers, excluded => Assert.True(GodotObject.IsInstanceValid(excluded)));
            Assert.Contains(newHandProxy, sweeper.ExcludedReceivers);
            Assert.Contains(newLowerArmProxy, sweeper.ExcludedReceivers);
            Assert.Equal(2, sweeper.ExcludedReceivers.Length);

            // The excluded pair stays silent against the regenerated proxies: the target returns onto its
            // own hand proxy and discovers no receiver.
            await fixture.MoveHandTargetTowardsAsync(sceneTree, fixture.NpcWorld(_handTargetRestLocal), 0.02f);
            await WaitForPhysicsFramesAsync(sceneTree, 4);
            Assert.Empty(fixture.NpcEvents);
            Assert.Equal(0, sweeper.LastSweepReceiverCountForTesting);

            // A co-existing pair still delivers: the target crosses onto its own chest front face and the
            // same monitor receives a self-attributed Touch.
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(new Vector3(_handTargetRestLocal.X, _handTargetRestLocal.Y, 0.25f)),
                0.02f);
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(new Vector3(_handTargetRestLocal.X, _handTargetRestLocal.Y, 0.23f)),
                0.02f);
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(new Vector3(_handTargetRestLocal.X, _handTargetRestLocal.Y, 0.21f)),
                0.02f);
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(new Vector3(_handTargetRestLocal.X, _handTargetRestLocal.Y, 0.19f)),
                0.02f);
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(new Vector3(_handTargetRestLocal.X, _handTargetRestLocal.Y, 0.17f)),
                0.02f);
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(new Vector3(_handTargetRestLocal.X, _handTargetRestLocal.Y, 0.16f)),
                0.02f);
            await fixture.MoveHandTargetTowardsAsync(
                sceneTree,
                fixture.NpcWorld(new Vector3(_handTargetRestLocal.X, _handTargetRestLocal.Y, TouchZ)),
                0.02f);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            ContactEvent selfTouch = Assert.Single(fixture.NpcEvents);
            Assert.Equal(ContactClass.Touch, selfTouch.Class);
            Assert.Equal("Sky's Chest", selfTouch.Receiver.Descriptor);
            Assert.Equal("Sky's Right Hand", selfTouch.Counterpart.Descriptor);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Builds the production wiring under test: an NPC character hub — a <see cref="CharacterHub" /> whose
    /// <see cref="CharacterHub.Id" /> supplies the sweeper's owner attribution — with a generated rig covering
    /// the chest, head, arm chain, a receiver monitor, and the <see cref="HandContactSweeperController" />
    /// hosting its right-hand sweeper on the character's IK hand target. The optional player-role receiver
    /// stands facing the NPC for cross-character delivery.
    /// </summary>
    private sealed class NpcSweeperFixture(
        Node3D root,
        CharacterHub npcRoot,
        DynamicPhysicalRig rig,
        ContactMonitor monitor,
        AnimatableBody3D handTarget,
        HandContactSweeperController controller,
        List<ContactEvent> npcEvents,
        List<ContactEvent> playerEvents)
    {
        internal static readonly (string Name, string? Parent, Vector3 LocalPosition)[] BoneDefinitions =
        [
            ("Hips", null, new Vector3(0.0f, 0.98f, 0.0f)),
            ("Spine", "Hips", new Vector3(0.0f, 0.12f, 0.0f)),
            ("Chest", "Spine", new Vector3(0.0f, 0.18f, 0.0f)),
            ("Neck", "Chest", new Vector3(0.0f, 0.18f, 0.0f)),
            ("Head", "Neck", new Vector3(0.0f, 0.16f, 0.0f)),
            ("Right Upper Arm", "Chest", new Vector3(0.22f, 0.0f, 0.0f)),
            ("Right Lower Arm", "Right Upper Arm", new Vector3(0.0f, -0.12f, 0.17f)),
            ("Right Hand", "Right Lower Arm", new Vector3(-0.12f, 0.0f, 0.10f)),
        ];

        public CharacterHub NpcRoot { get; } = npcRoot;

        public DynamicPhysicalRig Rig { get; } = rig;

        public ContactMonitor Monitor { get; } = monitor;

        public AnimatableBody3D HandTarget { get; } = handTarget;

        public HandContactSweeperController Controller { get; } = controller;

        public List<ContactEvent> NpcEvents { get; } = npcEvents;

        public List<ContactEvent> PlayerEvents { get; } = playerEvents;

        private Node3D Root { get; } = root;

        public Vector3 NpcWorld(Vector3 npcLocal) => NpcRoot.GlobalPosition + npcLocal;

        /// <summary>The world pose at which the hand target touches the player receiver's chest front face.</summary>
        public Vector3 PlayerChestTouchWorld()
        {
            Vector3 chestCentre = NpcWorld(_receiverOffsetFromNpc + new Vector3(0.0f, 1.28f, 0.0f));
            // Approach high on the chest — y 1.40 keeps the corridor clear of the receiver's folded hand and
            // forearm proxies beside it. The receiver faces the NPC, so its chest front face sits at chest
            // centre − Z by the half depth; the hand target touches it with its own half extent less a small
            // overlap.
            return chestCentre + new Vector3(0.0f, 0.12f, -ChestHalfDepth - (HandSize * 0.5f) + 0.005f);
        }

        public static async Task<NpcSweeperFixture> CreateAsync(SceneTree sceneTree, bool includePlayerReceiver)
        {
            Node3D root = new()
            {
                Name = "NpcHandSweeperTestRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            CharacterHub npcRoot = new()
            {
                Name = "Sky",
                Id = "Sky",
            };
            root.AddChild(npcRoot);

            Skeleton3D skeleton = CreateSkeleton();
            npcRoot.AddChild(skeleton);

            DynamicPhysicalRig rig = new()
            {
                Name = "NpcPhysicalRig",
                TargetSkeleton = skeleton,
                ColliderProfile = new BodyColliderProfile
                {
                    SourceScene = CreateColliderSourceScene(),
                },
            };
            skeleton.AddChild(rig);

            ContactMonitor monitor = new()
            {
                Name = "NpcContactMonitor",
                OwnerName = "Sky",
            };
            npcRoot.AddChild(monitor);

            List<ContactEvent> npcEvents = [];
            monitor.ContactEventEmitted += npcEvents.Add;

            // The NPC's arm-IK hand target: the body the IK system drives and the sweeper rides. It starts
            // exactly on the hand bone rest, fully overlapping its own hand proxy.
            Node3D ikTargets = new()
            {
                Name = "IKTargets",
            };
            npcRoot.AddChild(ikTargets);

            AnimatableBody3D handTarget = new()
            {
                Name = "RightHand",
                CollisionLayer = 8,
                CollisionMask = 0,
                SyncToPhysics = false,
            };
            ikTargets.AddChild(handTarget);
            handTarget.GlobalPosition = _fixtureOrigin + _handTargetRestLocal;

            // The production controller. OwnerName stays empty so attribution resolves from the character
            // hub's identity at sweeper creation; the bone names match the authored profile.
            HandContactSweeperController controller = new()
            {
                Name = "ContactSweepers",
                PhysicalRig = rig,
                RightHandIKTarget = handTarget,
                RightHandBoneName = "Right Hand",
                RightLowerArmBoneName = "Right Lower Arm",
            };
            npcRoot.AddChild(controller);

            // One process frame lets the rig's deferred generation run before attachments are placed and
            // before the controller's first physics tick can create the sweepers.
            await WaitForFramesAsync(sceneTree, 1);
            Assert.True(rig.GeneratedProxyCount >= 5, "The NPC rig should generate chest, head, arm, forearm, and hand proxies.");
            PlaceAttachmentsAtBoneRests(rig);

            List<ContactEvent> playerEvents = [];
            if (includePlayerReceiver)
            {
                // Yawed 180° so the receiver's chest front face looks back toward the NPC's approaching hand.
                Node3D playerRoot = new()
                {
                    Name = "PlayerReceiver",
                    Transform = new Transform3D(
                        Basis.FromEuler(new Vector3(0.0f, Mathf.Pi, 0.0f)),
                        _receiverOffsetFromNpc),
                };
                root.AddChild(playerRoot);

                Skeleton3D playerSkeleton = CreateSkeleton();
                playerRoot.AddChild(playerSkeleton);

                DynamicPhysicalRig playerRig = new()
                {
                    Name = "PlayerPhysicalRig",
                    TargetSkeleton = playerSkeleton,
                    ColliderProfile = new BodyColliderProfile
                    {
                        SourceScene = CreateColliderSourceScene(),
                    },
                };
                playerSkeleton.AddChild(playerRig);

                ContactMonitor playerMonitor = new()
                {
                    Name = "PlayerContactMonitor",
                    OwnerName = "Player",
                };
                playerRoot.AddChild(playerMonitor);
                playerMonitor.ContactEventEmitted += playerEvents.Add;
            }
            await WaitForPhysicsFramesAsync(sceneTree, 5);

            return new NpcSweeperFixture(
                root,
                npcRoot,
                rig,
                monitor,
                handTarget,
                controller,
                npcEvents,
                playerEvents);
        }

        /// <summary>
        /// Moves the hand target toward a world position in physics steps no larger than
        /// <paramref name="maxStepMetres" />, awaiting one physics frame per step: ~1.2 m/s at 0.02 m steps,
        /// below the 1.5 m/s impact threshold.
        /// </summary>
        public async Task MoveHandTargetTowardsAsync(SceneTree sceneTree, Vector3 worldPosition, float maxStepMetres)
        {
            while (HandTarget.GlobalPosition.DistanceTo(worldPosition) > 1e-4f)
            {
                Vector3 step = worldPosition - HandTarget.GlobalPosition;
                float distance = step.Length();
                HandTarget.GlobalPosition = HandTarget.GlobalPosition
                    + (step / distance * MathF.Min(distance, maxStepMetres));
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        public static void PlaceAttachmentsAtBoneRests(DynamicPhysicalRig rig)
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

        private static Skeleton3D CreateSkeleton()
        {
            Skeleton3D skeleton = new()
            {
                Name = "NpcSkeleton",
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
            CreateSourceAttachment(colliderRoot, "Right Upper Arm", new Vector3(0.12f, 0.12f, 0.12f));
            CreateSourceAttachment(colliderRoot, "Right Lower Arm", new Vector3(0.14f, 0.14f, 0.14f));
            CreateSourceAttachment(colliderRoot, "Right Hand", new Vector3(HandSize, HandSize, HandSize));

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
