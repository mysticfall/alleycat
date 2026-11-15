using AlleyCat.Contact;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Player/NPC receiver parity (INTR-005 plan step 6, UR1, TR12, TR19, AC-U1, AC-T7): one parameterised
/// fixture builds a receiver character rig for either role, and the same scripted counterpart scenario —
/// an external hand contact and a self-contact — runs identically against both roles through that single
/// parameterised script, asserting equivalent classified events: the same classes and transitions, the same
/// receiver body part, equivalent bone-local directions, intensities, and durations, and role-correct owner
/// attribution on both parties.
/// </summary>
public sealed class ContactParityIntegrationTests
{
    private const float HandSize = 0.12f;
    private const float ChestHalfDepth = 0.09f;
    private const float TouchZ = ChestHalfDepth + (HandSize * 0.5f) - 0.005f;

    // Receiver-role parameters: the owner identity differs; every structural property of the rig, monitor,
    // and scripted scenario is shared.
    private static readonly IReadOnlyDictionary<ReceiverRole, string> _roleOwnerNames = new Dictionary<ReceiverRole, string>
    {
        [ReceiverRole.Player] = "Player",
        [ReceiverRole.Npc] = "Sky",
    };

    private static readonly Vector3 _fixtureOrigin = new(100.0f, 14.0f, 0.0f);

    private enum ReceiverRole
    {
        Player,
        Npc,
    }

    /// <summary>
    /// Verifies the external-contact scenario against both roles through the shared script: the same counterpart hand
    /// approach produces equivalent classified events — class, body part, direction, intensity band,
    /// duration — on the player-role and NPC-role receivers, with each role's owner attribution correct
    /// (INTR-005 UR1, TR12, TR19).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerAndNpcReceivers_ExternalContact_ProduceEquivalentClassifiedEvents()
    {
        SceneTree sceneTree = GetSceneTree();
        ParityFixture fixture = await ParityFixture.CreateAsync(sceneTree);

        try
        {
            IReadOnlyList<ContactEvent> playerEvents = await fixture.RunExternalScenarioAsync(sceneTree, ReceiverRole.Player);
            IReadOnlyList<ContactEvent> npcEvents = await fixture.RunExternalScenarioAsync(sceneTree, ReceiverRole.Npc);

            ContactEvent playerTouch = Assert.Single(playerEvents);
            ContactEvent npcTouch = Assert.Single(npcEvents);

            Assert.Equal(ContactClass.Touch, playerTouch.Class);
            Assert.Equal(ContactClass.Touch, npcTouch.Class);
            Assert.Null(playerTouch.PreviousClass);
            Assert.Null(npcTouch.PreviousClass);

            // Role-correct attribution: the receiver party carries its role's owner, the shared counterpart
            // party is identical across roles.
            Assert.Equal("Chest", playerTouch.Receiver.DisplayName);
            Assert.Equal("Chest", npcTouch.Receiver.DisplayName);
            Assert.Equal("Player", playerTouch.Receiver.OwnerName);
            Assert.Equal("Sky", npcTouch.Receiver.OwnerName);
            Assert.Equal("Right Hand", playerTouch.Counterpart.DisplayName);
            Assert.Equal("Right Hand", npcTouch.Counterpart.DisplayName);
            Assert.Equal("Morgan", playerTouch.Counterpart.OwnerName);
            Assert.Equal("Morgan", npcTouch.Counterpart.OwnerName);
            Assert.Equal(playerTouch.Counterpart.Descriptor, npcTouch.Counterpart.Descriptor);

            AssertEquivalentMeasurements(playerTouch, npcTouch);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the self-contact scenario against both roles through the shared script: each receiver's own hand sweeper
    /// approaching its own chest produces equivalent self-attributed classified events on the player-role
    /// and NPC-role rigs (INTR-005 UR6, TR12, TR19).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerAndNpcReceivers_SelfContact_ProduceEquivalentSelfAttributedEvents()
    {
        SceneTree sceneTree = GetSceneTree();
        ParityFixture fixture = await ParityFixture.CreateAsync(sceneTree);

        try
        {
            IReadOnlyList<ContactEvent> playerEvents = await fixture.RunSelfContactScenarioAsync(sceneTree, ReceiverRole.Player);
            IReadOnlyList<ContactEvent> npcEvents = await fixture.RunSelfContactScenarioAsync(sceneTree, ReceiverRole.Npc);

            ContactEvent playerSelf = Assert.Single(playerEvents);
            ContactEvent npcSelf = Assert.Single(npcEvents);

            Assert.Equal(ContactClass.Touch, playerSelf.Class);
            Assert.Equal(ContactClass.Touch, npcSelf.Class);
            Assert.Null(playerSelf.PreviousClass);

            // Self attribution on both parties for both roles: the receiver and counterpart owners match
            // each other and the role's identity.
            Assert.Equal("Chest", playerSelf.Receiver.DisplayName);
            Assert.Equal("Chest", npcSelf.Receiver.DisplayName);
            Assert.Equal(playerSelf.Receiver.OwnerName, playerSelf.Counterpart.OwnerName);
            Assert.Equal(npcSelf.Receiver.OwnerName, npcSelf.Counterpart.OwnerName);
            Assert.Equal("Player", playerSelf.Receiver.OwnerName);
            Assert.Equal("Sky", npcSelf.Receiver.OwnerName);
            Assert.Equal("Player's Right Hand", playerSelf.Counterpart.Descriptor);
            Assert.Equal("Sky's Right Hand", npcSelf.Counterpart.Descriptor);

            AssertEquivalentMeasurements(playerSelf, npcSelf);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Asserts the role-independent measurement projection: equivalent bone-local directions, intensities
    /// within a small band, and durations within one physics tick of each other.
    /// </summary>
    private static void AssertEquivalentMeasurements(ContactEvent playerEvent, ContactEvent npcEvent)
    {
        System.Numerics.Vector3 directionDelta = playerEvent.Direction - npcEvent.Direction;
        Assert.True(
            directionDelta.Length() <= 0.1f,
            $"The bone-local directions should be equivalent across roles; were {playerEvent.Direction} " +
            $"and {npcEvent.Direction}.");
        Assert.True(
            MathF.Abs(playerEvent.Intensity - npcEvent.Intensity) <= MathF.Max(0.02f, playerEvent.Intensity * 0.25f),
            $"The intensities should be equivalent across roles; were {playerEvent.Intensity:F3} and " +
            $"{npcEvent.Intensity:F3}.");
        Assert.True(
            MathF.Abs((float)(playerEvent.Duration - npcEvent.Duration).TotalSeconds) <= 1.0f / 30.0f,
            $"The durations should be equivalent across roles within one physics tick; were " +
            $"{playerEvent.Duration.TotalSeconds:F3} s and {npcEvent.Duration.TotalSeconds:F3} s.");
    }

    /// <summary>
    /// Builds one receiver character per role — the same skeleton, generated rig, monitor, and hand
    /// wiring, differing only in owner identity — plus, per role, one neutral counterpart hand driven by
    /// the same scripted steps. The two receivers sit at separate offsets from the fixture origin.
    /// </summary>
    private sealed class ParityFixture
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

        // The folded right arm rests with its hand in front of the chest, clear of it (self-contact start).
        private static readonly Vector3 _ownHandRestLocal = new(0.10f, 1.16f, 0.27f);

        private static readonly Vector3 _playerReceiverOffset = new(-1.5f, 0.0f, 0.0f);

        private static readonly Vector3 _npcReceiverOffset = new(1.5f, 0.0f, 0.0f);

        private readonly IReadOnlyDictionary<ReceiverRole, ReceiverCharacter> _receivers;

        private ParityFixture(Node3D root, IReadOnlyDictionary<ReceiverRole, ReceiverCharacter> receivers)
        {
            Root = root;
            _receivers = receivers;
        }

        public Node3D Root
        {
            get;
        }

        public static async Task<ParityFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "ContactParityTestRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            Dictionary<ReceiverRole, ReceiverCharacter> receivers = [];
            foreach (ReceiverRole role in Enum.GetValues<ReceiverRole>())
            {
                receivers[role] = await ReceiverCharacter.CreateAsync(
                    sceneTree,
                    root,
                    role,
                    role == ReceiverRole.Player ? _playerReceiverOffset : _npcReceiverOffset);
            }

            return new ParityFixture(root, receivers);
        }

        /// <summary>
        /// Runs the external-contact script against one role: the neutral counterpart hand — identical across
        /// roles, driven by the same step list — approaches the receiver's chest front face slowly and
        /// settles. The approach runs high on the chest so the receiver's folded arm proxies stay clear of
        /// the corridor.
        /// </summary>
        public async Task<IReadOnlyList<ContactEvent>> RunExternalScenarioAsync(SceneTree sceneTree, ReceiverRole role)
        {
            ReceiverCharacter receiver = _receivers[role];
            receiver.Events.Clear();

            Vector3 chestTouch = receiver.Origin + new Vector3(0.0f, 1.40f, TouchZ);
            await MoveHandTowardsAsync(sceneTree, receiver.CounterpartHand, chestTouch, 0.02f);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            return receiver.Events;
        }

        /// <summary>
        /// Runs the self-contact script against one role: the receiver's own hand sweeper approaches its own
        /// chest front face slowly, across the folded forearm's excluded proxy, and settles.
        /// </summary>
        public async Task<IReadOnlyList<ContactEvent>> RunSelfContactScenarioAsync(SceneTree sceneTree, ReceiverRole role)
        {
            ReceiverCharacter receiver = _receivers[role];
            receiver.Events.Clear();

            Vector3 receiverOrigin = receiver.Origin;
            await StepCounterpartHandAsync(
                sceneTree,
                receiver.OwnHand,
                receiverOrigin + new Vector3(_ownHandRestLocal.X, _ownHandRestLocal.Y, 0.25f));
            await StepCounterpartHandAsync(
                sceneTree,
                receiver.OwnHand,
                receiverOrigin + new Vector3(_ownHandRestLocal.X, _ownHandRestLocal.Y, 0.23f));
            await StepCounterpartHandAsync(
                sceneTree,
                receiver.OwnHand,
                receiverOrigin + new Vector3(_ownHandRestLocal.X, _ownHandRestLocal.Y, 0.21f));
            await StepCounterpartHandAsync(
                sceneTree,
                receiver.OwnHand,
                receiverOrigin + new Vector3(_ownHandRestLocal.X, _ownHandRestLocal.Y, 0.19f));
            await StepCounterpartHandAsync(
                sceneTree,
                receiver.OwnHand,
                receiverOrigin + new Vector3(_ownHandRestLocal.X, _ownHandRestLocal.Y, 0.17f));
            await StepCounterpartHandAsync(
                sceneTree,
                receiver.OwnHand,
                receiverOrigin + new Vector3(_ownHandRestLocal.X, _ownHandRestLocal.Y, 0.16f));
            await StepCounterpartHandAsync(
                sceneTree,
                receiver.OwnHand,
                receiverOrigin + new Vector3(_ownHandRestLocal.X, _ownHandRestLocal.Y, TouchZ));
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            return receiver.Events;
        }

        private static async Task StepCounterpartHandAsync(SceneTree sceneTree, AnimatableBody3D hand, Vector3 worldPosition)
        {
            hand.GlobalPosition = worldPosition;
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        /// <summary>
        /// Moves a hand toward a world position in physics steps no larger than
        /// <paramref name="maxStepMetres" />, awaiting one physics frame per step.
        /// </summary>
        private static async Task MoveHandTowardsAsync(
            SceneTree sceneTree,
            AnimatableBody3D hand,
            Vector3 worldPosition,
            float maxStepMetres)
        {
            while (hand.GlobalPosition.DistanceTo(worldPosition) > 1e-4f)
            {
                Vector3 step = worldPosition - hand.GlobalPosition;
                float distance = step.Length();
                hand.GlobalPosition += step / distance * MathF.Min(distance, maxStepMetres);
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

        /// <summary>
        /// One receiver character for one role: the shared skeleton and generated rig — proxies for the
        /// chest, head, forearm, and hand — a monitor attributed to the role's owner, the role's own hand
        /// sweeper wired with its self-pair exclusions, and a neutral external counterpart hand sweeper.
        /// </summary>
        private sealed class ReceiverCharacter(
            Vector3 origin,
            DynamicPhysicalRig rig,
            List<ContactEvent> events,
            AnimatableBody3D counterpartHand,
            AnimatableBody3D ownHand)
        {
            public Vector3 Origin { get; } = origin;

            public DynamicPhysicalRig Rig { get; } = rig;

            public List<ContactEvent> Events { get; } = events;

            public AnimatableBody3D CounterpartHand { get; } = counterpartHand;

            public AnimatableBody3D OwnHand { get; } = ownHand;

            public static async Task<ReceiverCharacter> CreateAsync(SceneTree sceneTree, Node3D root, ReceiverRole role, Vector3 offset)
            {
                string ownerName = _roleOwnerNames[role];
                Vector3 origin = _fixtureOrigin + offset;

                Node3D characterRoot = new()
                {
                    Name = $"{role}Receiver",
                };
                root.AddChild(characterRoot);
                characterRoot.GlobalPosition = origin;

                Skeleton3D skeleton = CreateReceiverSkeleton();
                characterRoot.AddChild(skeleton);

                DynamicPhysicalRig rig = new()
                {
                    Name = $"{role}ReceiverPhysicalRig",
                    TargetSkeleton = skeleton,
                    ColliderProfile = new BodyColliderProfile
                    {
                        SourceScene = CreateColliderSourceScene(),
                    },
                };
                skeleton.AddChild(rig);

                ContactMonitor monitor = new()
                {
                    Name = $"{role}ReceiverContactMonitor",
                    OwnerName = ownerName,
                };
                characterRoot.AddChild(monitor);

                List<ContactEvent> events = [];
                monitor.ContactEventEmitted += events.Add;

                // One process frame lets the rig's deferred generation run.
                await WaitForFramesAsync(sceneTree, 1);
                Assert.True(rig.GeneratedProxyCount >= 4, "The receiver rig should generate chest, head, forearm, and hand proxies.");
                PlaceAttachmentsAtBoneRests(rig);

                // The neutral external counterpart hand — identical across roles: its attribution must not
                // vary with the receiver's role.
                AnimatableBody3D counterpartHand = CreateHandBody($"{role}ReceiverMorganHand");
                root.AddChild(counterpartHand);
                counterpartHand.GlobalPosition = origin + new Vector3(0.0f, 1.40f, 0.40f);
                counterpartHand.AddChild(new ContactSource
                {
                    Name = "MorganRightHandContactSource",
                    PartyDisplayName = "Right Hand",
                    OwnerName = "Morgan",
                });

                // The receiver's own hand sweeper: same owner as the receiver, excluded from its own hand
                // and lower-arm proxies (the RIG-001 hand-target exception topology).
                AnimatableBody3D ownHand = CreateHandBody($"{role}ReceiverOwnHand");
                root.AddChild(ownHand);
                ownHand.GlobalPosition = origin + _ownHandRestLocal;

                IReadOnlyList<PhysicsBody3D> handProxies = rig.GetGeneratedProxyBodiesForBone("Right Hand");
                IReadOnlyList<PhysicsBody3D> lowerArmProxies = rig.GetGeneratedProxyBodiesForBone("Right Lower Arm");
                _ = Assert.Single(handProxies);
                _ = Assert.Single(lowerArmProxies);

                ContactSource ownHandSource = new()
                {
                    Name = "OwnRightHandContactSource",
                    PartyDisplayName = "Right Hand",
                    OwnerName = ownerName,
                    ExcludedReceivers = [.. handProxies, .. lowerArmProxies],
                };
                ownHand.AddChild(ownHandSource);

                await WaitForPhysicsFramesAsync(sceneTree, 5);

                return new ReceiverCharacter(origin, rig, events, counterpartHand, ownHand);
            }

            private static AnimatableBody3D CreateHandBody(string bodyName)
            {
                AnimatableBody3D hand = new()
                {
                    Name = bodyName,
                    CollisionLayer = 8,
                    CollisionMask = 0,
                    SyncToPhysics = false,
                };
                hand.AddChild(new CollisionShape3D
                {
                    Name = "HandShape",
                    Shape = new BoxShape3D
                    {
                        Size = new Vector3(HandSize, HandSize, HandSize),
                    },
                });
                return hand;
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
}
