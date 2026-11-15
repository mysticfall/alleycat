using AlleyCat.Contact;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Self-contact coverage of the contact detection chain (INTR-005 plan step 5, UR6, TR12): a character's own
/// hand sweeper — a generic <see cref="ContactSource" /> tracking a kinematic hand body — sweeps the same
/// rig's generated <see cref="PhysicalBodyPart3D" /> proxies. Pairs the RIG-001 self-collision exception
/// topology excludes (the hand against its own hand and lower-arm proxies; RIG-001 TR12 adjacent-bone
/// exceptions, TR13 same-side exceptions, TR15 hand-target exceptions) never report, while a co-existing pair
/// — the hand against its own chest — delivers a classified self-contact event through the same
/// <see cref="ContactMonitor.ContactEventEmitted" /> path as external contact, with self attribution on both
/// parties.
/// </summary>
public sealed class SelfContactIntegrationTests
{
    private const float HandSize = 0.12f;
    private const float ChestHalfDepth = 0.09f;
    private const float TouchZ = ChestHalfDepth + (HandSize * 0.5f) - 0.005f;

    // Skeleton-local anchor positions (the host container is unrotated, so these equal world offsets from the
    // fixture origin): the folded arm rests with its hand in front of the chest, clear of it. The upper arm
    // sits far enough out that a hand touching it from above cannot also overlap the chest beside it.
    private static readonly Vector3 _handProxyLocal = new(0.20f, 1.16f, 0.27f);
    private static readonly Vector3 _lowerArmProxyLocal = new(0.32f, 1.16f, 0.17f);

    private static readonly Vector3 _fixtureOrigin = new(90.0f, 12.0f, 0.0f);

    /// <summary>
    /// Verifies the topology-excluded self pairs never report: the hand sweeper resting fully on its own hand
    /// proxy and then sliding onto its own lower-arm proxy — both inside the RIG-001 exception set —
    /// discovers no receivers and emits nothing, although the geometry genuinely overlaps (INTR-005 TR12).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ExcludedSelfPairs_HandOnOwnHandAndForearmProxies_NeverReport()
    {
        SceneTree sceneTree = GetSceneTree();
        SelfContactFixture fixture = await SelfContactFixture.CreateAsync(sceneTree);

        try
        {
            PhysicsBody3D handProxy = Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Hand"));
            PhysicsBody3D lowerArmProxy =
                Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Lower Arm"));

            // Independent oracle: the hand body fully coincides with its own hand proxy — identical 0.12 m
            // boxes at the same centre — so an unexcluded sweep would certainly discover the proxy.
            Assert.True(
                fixture.HandBody.GlobalPosition.DistanceTo(handProxy.GlobalPosition) <= 0.005f,
                $"The hand body should fully overlap its own hand proxy; hand was " +
                $"{fixture.HandBody.GlobalPosition}, proxy was {handProxy.GlobalPosition}.");

            await SelfContactFixture.HoldHandAsync(sceneTree, 4);

            Assert.Empty(fixture.Events);
            Assert.Empty(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal(0, fixture.HandSource.LastSweepReceiverCountForTesting);

            // Slide along the forearm onto its proxy — the adjacent-bone excluded pair (RIG-001 TR12).
            await fixture.MoveHandAsync(sceneTree, World(_lowerArmProxyLocal));
            Assert.True(
                fixture.HandBody.GlobalPosition.DistanceTo(lowerArmProxy.GlobalPosition) <= 0.005f,
                $"The hand body should fully overlap its own lower-arm proxy; hand was " +
                $"{fixture.HandBody.GlobalPosition}, proxy was {lowerArmProxy.GlobalPosition}.");

            await SelfContactFixture.HoldHandAsync(sceneTree, 4);

            Assert.Empty(fixture.Events);
            Assert.Empty(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal(0, fixture.HandSource.LastSweepReceiverCountForTesting);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a co-existing self pair delivers self-contact through the shared path: the character's hand
    /// approaching its own chest proxy — not adjacent, not same-side, so the RIG-001 topology lets the pair
    /// physically co-exist in contact — classifies Touch at Begin with the character's attribution on both
    /// parties (INTR-005 UR6, TR11, TR12).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CoExistingSelfPair_OwnHandOnOwnChest_DeliversSelfAttributedContact()
    {
        SceneTree sceneTree = GetSceneTree();
        SelfContactFixture fixture = await SelfContactFixture.CreateAsync(sceneTree);

        try
        {
            // Approach from the own-hand rest across the forearm's excluded proxy, onto the chest front face:
            // 0.02 m steps at 60 Hz stay below the impact threshold; the final 0.015 m step lands at ~0.9 m/s.
            await fixture.MoveHandAsync(sceneTree, World(new Vector3(_handProxyLocal.X, _handProxyLocal.Y, 0.25f)));
            await fixture.MoveHandAsync(sceneTree, World(new Vector3(_handProxyLocal.X, _handProxyLocal.Y, 0.23f)));
            await fixture.MoveHandAsync(sceneTree, World(new Vector3(_handProxyLocal.X, _handProxyLocal.Y, 0.21f)));
            await fixture.MoveHandAsync(sceneTree, World(new Vector3(_handProxyLocal.X, _handProxyLocal.Y, 0.19f)));
            await fixture.MoveHandAsync(sceneTree, World(new Vector3(_handProxyLocal.X, _handProxyLocal.Y, 0.17f)));
            await fixture.MoveHandAsync(sceneTree, World(new Vector3(_handProxyLocal.X, _handProxyLocal.Y, 0.16f)));
            await fixture.MoveHandAsync(sceneTree, World(new Vector3(_handProxyLocal.X, _handProxyLocal.Y, TouchZ)));
            await SelfContactFixture.HoldHandAsync(sceneTree, 2);

            ContactEvent touch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Null(touch.PreviousClass);

            // Self attribution on both parties: the receiver and the counterpart belong to the same character,
            // carried in the payload through the same delivery path as external contact (TR11, TR12).
            Assert.Equal("Chest", touch.Receiver.DisplayName);
            Assert.Equal("Sky", touch.Receiver.OwnerName);
            Assert.Equal("Sky's Chest", touch.Receiver.Descriptor);
            Assert.Equal("Right Hand", touch.Counterpart.DisplayName);
            Assert.Equal("Sky", touch.Counterpart.OwnerName);
            Assert.Equal("Sky's Right Hand", touch.Counterpart.Descriptor);
            Assert.Equal(touch.Receiver.OwnerName, touch.Counterpart.OwnerName);

            // The chest is the one and only receiver the sweep delivered — the excluded forearm proxy it
            // crossed on the way never entered the raw stream.
            Assert.Equal(1, fixture.HandSource.LastSweepReceiverCountForTesting);
            Assert.All(
                fixture.Monitor.GetRecentRawEvents(),
                rawEvent => Assert.Equal("Chest", rawEvent.Receiver.DisplayName));
            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal("Chest", active.Receiver.DisplayName);
            Assert.Equal("Right Hand", active.Source.DisplayName);
            Assert.Equal("Sky", active.Source.OwnerName);

            // Sanity: the chest front-face normal resolves in the chest bone-local frame (the full frame
            // discrimination is covered by the step-3 scenarios).
            Assert.True(
                touch.Direction.Z >= 0.8f,
                $"The chest contact direction should resolve to the chest bone-local +Z axis; was " +
                $"{touch.Direction}.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the one RIG-001 topology boundary previously unexercised (step-6 preamble): the hand
    /// against its own upper-arm proxy — not adjacent, not same-side, so the topology lets the pair
    /// physically co-exist in contact — IS reported as self-contact, while sitting outside the hand-target
    /// exception set that keeps the hand and lower-arm pairs silent (INTR-005 TR12; RIG-001 TR12, TR13).
    /// The hand approaches from above the shoulder, a path that cannot clip the chest beside the upper arm.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CoExistingSelfPair_OwnHandOnOwnUpperArm_IsReported()
    {
        SceneTree sceneTree = GetSceneTree();
        SelfContactFixture fixture = await SelfContactFixture.CreateAsync(sceneTree);

        try
        {
            PhysicsBody3D upperArmProxy =
                Assert.Single(fixture.Rig.GetGeneratedProxyBodiesForBone("Right Upper Arm"));

            // Lift the hand out and over the shoulder: above every proxy, clear of the chest beside the
            // upper arm, then descend onto the upper-arm proxy's top face in ≤ 0.02 m physics steps
            // (~1.2 m/s, below the impact threshold).
            await fixture.MoveHandTowardsAsync(
                sceneTree,
                World(new Vector3(0.32f, 1.50f, 0.27f)),
                0.02f);
            await fixture.MoveHandTowardsAsync(
                sceneTree,
                World(new Vector3(0.32f, 1.50f, 0.10f)),
                0.02f);
            await fixture.MoveHandTowardsAsync(
                sceneTree,
                World(new Vector3(0.32f, 1.50f, 0.0f)),
                0.02f);
            await fixture.MoveHandTowardsAsync(
                sceneTree,
                World(new Vector3(0.32f, 1.44f, 0.0f)),
                0.02f);
            await fixture.MoveHandTowardsAsync(
                sceneTree,
                World(new Vector3(0.32f, 1.395f, 0.0f)),
                0.02f);
            await SelfContactFixture.HoldHandAsync(sceneTree, 4);

            // Independent oracle: the hand body overlaps its own upper-arm proxy from above — the proxy's
            // top face sits at y 1.34, the hand's bottom face at 1.335.
            Assert.True(
                fixture.HandBody.GlobalPosition.DistanceTo(upperArmProxy.GlobalPosition) <= 0.16f,
                $"The hand body should rest on its own upper-arm proxy; hand was " +
                $"{fixture.HandBody.GlobalPosition}, proxy was {upperArmProxy.GlobalPosition}.");

            ContactEvent touch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Null(touch.PreviousClass);

            // Self attribution through the shared path, on the co-existing pair.
            Assert.Equal("Right Upper Arm", touch.Receiver.DisplayName);
            Assert.Equal("Sky", touch.Receiver.OwnerName);
            Assert.Equal("Sky's Right Upper Arm", touch.Receiver.Descriptor);
            Assert.Equal("Sky's Right Hand", touch.Counterpart.Descriptor);

            // The upper arm is the one and only receiver the sweep delivered.
            Assert.Equal(1, fixture.HandSource.LastSweepReceiverCountForTesting);
            Assert.All(
                fixture.Monitor.GetRecentRawEvents(),
                rawEvent => Assert.Equal("Right Upper Arm", rawEvent.Receiver.DisplayName));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static Vector3 World(Vector3 skeletonLocal) => _fixtureOrigin + skeletonLocal;

    /// <summary>
    /// Builds the self-contact wiring: one character rig whose generated proxies cover the chest, head,
    /// forearm, and hand — the arm folded so the hand rests in front of the chest — a
    /// <see cref="ContactMonitor" /> on the character host, and the character's own hand body carrying a
    /// <see cref="ContactSource" /> sweeper whose <see cref="ContactSource.ExcludedReceivers" /> carries the
    /// rig's own hand and lower-arm proxies: the RIG-001 exception set for a tracked hand (RIG-001 TR12
    /// adjacent-bone, TR13 same-side, TR15 hand-target exceptions). Shape queries do not consult physics
    /// collision exceptions, so the set is wired explicitly.
    /// </summary>
    private sealed class SelfContactFixture(
        Node3D root,
        DynamicPhysicalRig rig,
        ContactMonitor monitor,
        AnimatableBody3D handBody,
        ContactSource handSource,
        List<ContactEvent> events)
    {
        internal static readonly (string Name, string? Parent, Vector3 LocalPosition)[] BoneDefinitions =
        [
            ("Hips", null, new Vector3(0.0f, 0.98f, 0.0f)),
            ("Spine", "Hips", new Vector3(0.0f, 0.12f, 0.0f)),
            ("Chest", "Spine", new Vector3(0.0f, 0.18f, 0.0f)),
            ("Neck", "Chest", new Vector3(0.0f, 0.18f, 0.0f)),
            ("Head", "Neck", new Vector3(0.0f, 0.16f, 0.0f)),
            ("Right Upper Arm", "Chest", new Vector3(0.32f, 0.0f, 0.0f)),
            ("Right Lower Arm", "Right Upper Arm", new Vector3(0.0f, -0.12f, 0.17f)),
            ("Right Hand", "Right Lower Arm", new Vector3(-0.12f, 0.0f, 0.10f)),
        ];

        public DynamicPhysicalRig Rig { get; } = rig;

        public ContactMonitor Monitor { get; } = monitor;

        public AnimatableBody3D HandBody { get; } = handBody;

        public ContactSource HandSource { get; } = handSource;

        public IReadOnlyList<ContactEvent> Events { get; } = events;

        public static async Task<SelfContactFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "SelfContactTestRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            Node3D npcRoot = new()
            {
                Name = "Npc",
            };
            root.AddChild(npcRoot);

            Skeleton3D skeleton = CreateNpcSkeleton();
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

            AnimatableBody3D handBody = new()
            {
                Name = "NpcRightHand",
                CollisionLayer = 8,
                CollisionMask = 0,
                SyncToPhysics = false,
            };
            handBody.AddChild(new CollisionShape3D
            {
                Name = "HandShape",
                Shape = new BoxShape3D
                {
                    Size = new Vector3(HandSize, HandSize, HandSize),
                },
            });
            root.AddChild(handBody);
            handBody.GlobalPosition = World(_handProxyLocal);

            List<ContactEvent> events = [];
            monitor.ContactEventEmitted += events.Add;

            // One process frame lets the rig's deferred generation run.
            await WaitForFramesAsync(sceneTree, 1);
            Assert.True(rig.GeneratedProxyCount >= 5, "The rig should generate Chest, Head, upper-arm, forearm, and hand proxies.");
            PlaceAttachmentsAtBoneRests(rig);

            // The RIG-001 self-collision exception set for this tracked hand: its own hand proxy and its own
            // lower-arm proxy (RIG-001 TR12, TR15). The chest and every other proxy stay deliverable, so the
            // same sweeper senses self-contact where the topology allows the pair to co-exist.
            IReadOnlyList<PhysicsBody3D> handProxies = rig.GetGeneratedProxyBodiesForBone("Right Hand");
            IReadOnlyList<PhysicsBody3D> lowerArmProxies = rig.GetGeneratedProxyBodiesForBone("Right Lower Arm");
            _ = Assert.Single(handProxies);
            _ = Assert.Single(lowerArmProxies);

            // The character's own hand sweeper, wired with its exception set before it can sweep: it enters
            // the tree only after the set is resolved, so no pre-wiring tick can deliver the excluded pairs.
            ContactSource handSource = new()
            {
                Name = "RightHandContactSource",
                PartyDisplayName = "Right Hand",
                // The sweeper belongs to the same character as the rig: contacts it delivers are self-contact.
                OwnerName = "Sky",
                ExcludedReceivers = [.. handProxies, .. lowerArmProxies],
            };
            handBody.AddChild(handSource);

            await WaitForPhysicsFramesAsync(sceneTree, 5);

            return new SelfContactFixture(root, rig, monitor, handBody, handSource, events);
        }

        public async Task MoveHandAsync(SceneTree sceneTree, Vector3 worldPosition)
        {
            HandBody.GlobalPosition = worldPosition;
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        /// <summary>
        /// Moves the hand toward a world position in physics steps no larger than
        /// <paramref name="maxStepMetres" />, awaiting one physics frame per step.
        /// </summary>
        public async Task MoveHandTowardsAsync(SceneTree sceneTree, Vector3 worldPosition, float maxStepMetres)
        {
            while (HandBody.GlobalPosition.DistanceTo(worldPosition) > 1e-4f)
            {
                Vector3 step = worldPosition - HandBody.GlobalPosition;
                float distance = step.Length();
                HandBody.GlobalPosition = HandBody.GlobalPosition
                    + (step / distance * MathF.Min(distance, maxStepMetres));
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }
        }

        public static async Task HoldHandAsync(SceneTree sceneTree, int physicsFrames)
            => await WaitForPhysicsFramesAsync(sceneTree, physicsFrames);

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(root) && root.IsInsideTree())
            {
                root.QueueFree();
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

        private static Skeleton3D CreateNpcSkeleton()
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

            // Pin every bone's pose to its rest, the state a real skeleton holds when not animated.
            for (int boneIndex = 0; boneIndex < skeleton.GetBoneCount(); boneIndex += 1)
            {
                skeleton.SetBonePose(boneIndex, skeleton.GetBoneRest(boneIndex));
            }

            return skeleton;
        }

        private static PackedScene CreateColliderSourceScene()
        {
            Node root = new()
            {
                Name = "CollidersRoot",
            };

            CreateSourceAttachment(root, "Chest", new Vector3(0.40f, 0.44f, 0.18f));
            CreateSourceAttachment(root, "Head", new Vector3(0.18f, 0.20f, 0.18f));
            CreateSourceAttachment(root, "Right Upper Arm", new Vector3(0.12f, 0.12f, 0.12f));
            CreateSourceAttachment(root, "Right Lower Arm", new Vector3(0.14f, 0.14f, 0.14f));
            CreateSourceAttachment(root, "Right Hand", new Vector3(0.12f, 0.12f, 0.12f));

            PackedScene sourceScene = new();
            Error packResult = sourceScene.Pack(root);
            root.Free();

            Assert.Equal(Error.Ok, packResult);
            return sourceScene;
        }

        private static void CreateSourceAttachment(Node root, string boneName, Vector3 boxSize)
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

            root.AddChild(sourceBoneAttachment);
            sourceBoneAttachment.Owner = root;
            sourceBoneAttachment.AddChild(sourceBody);
            sourceBody.Owner = root;
            sourceBody.AddChild(sourceShape);
            sourceShape.Owner = root;
        }
    }
}
