using AlleyCat.Contact;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// End-to-end vertical-slice coverage of the contact detection chain (INTR-005 plan step 3): a player XR hand
/// <see cref="ContactSource" /> sweeper discovers an NPC rig's generated receiver proxies through the
/// <c>contact_receiver</c> group, a <see cref="ContactMonitor" /> aggregates the raw lifecycle with
/// receiver-side velocity sampling (TR18f), and the classification engine emits classified
/// <see cref="ContactEvent" />s — asserted for sustained press, impact-at-begin, touch-to-caress revision,
/// silent contact end, and finite relative velocities on a moving NPC part.
/// </summary>
public sealed class ContactDetectionIntegrationTests
{
    private const float HandSize = 0.12f;
    private const float HandHalfExtent = HandSize * 0.5f;
    private const float ChestHalfDepth = 0.09f;
    private const float HeadHalfExtent = 0.09f;
    private const float TouchZ = ChestHalfDepth + HandHalfExtent - 0.005f;
    private const float PressZ = ChestHalfDepth + HandHalfExtent - 0.028f;
    private const float PressSecondsPerTick = 1.0f / 60.0f;

    private static readonly Vector3 _fixtureOrigin = new(60.0f, 12.0f, 0.0f);

    /// <summary>
    /// Verifies a sustained slow press classifies Touch then Press, carrying the receiver body part, owner
    /// attribution on both parties, the bone-local contact direction, and a non-zero duration (INTR-005 UR1,
    /// UR4, TR8, TR12).
    /// </summary>
    [Headless]
    [Fact]
    public async Task SustainedSlowPress_ClassifiesTouchThenPressWithAttribution()
    {
        SceneTree sceneTree = GetSceneTree();
        ContactFixture fixture = await ContactFixture.CreateAsync(sceneTree);

        try
        {
            // Slow approach stays below the impact threshold; the press-rate arm holds during the press-in and
            // the depth arm holds during the sustained rest at 0.028 m of penetration.
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.20f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.18f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.16f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, TouchZ));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.135f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.128f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, PressZ));
            await ContactFixture.HoldHandAsync(sceneTree, 14);

            Assert.Equal(
            [
                ContactClass.Touch,
                ContactClass.Press,
            ], fixture.Events.Select(contactEvent => contactEvent.Class));

            ContactEvent press = fixture.Events[1];
            Assert.Equal(ContactClass.Touch, press.PreviousClass);
            Assert.True(
                press.Duration >= TimeSpan.FromSeconds(0.1),
                $"The sustained press should confirm after the 0.15 s window; duration was {press.Duration.TotalSeconds:F3} s.");
            Assert.True(
                press.Intensity >= 0.01f,
                $"The press intensity should report the sustained penetration depth; intensity was {press.Intensity:F4} m.");

            Assert.Equal("Chest", press.Receiver.DisplayName);
            Assert.Equal("Sky", press.Receiver.OwnerName);
            Assert.Equal("Sky's Chest", press.Receiver.Descriptor);
            Assert.Equal("Right Hand", press.Counterpart.DisplayName);
            Assert.Equal("Player", press.Counterpart.OwnerName);
            Assert.Equal("Player's Right Hand", press.Counterpart.Descriptor);

            // The NPC container is yaw-rotated 180°, so the chest bone's local +Z faces world −Z: a world +Z
            // contact normal resolves to the bone-local −Z axis. The chest proxy body additionally carries a
            // distinguishing +90° local yaw (counter-rotated inside its shape, so the world collider geometry
            // is unchanged): resolving through the body frame would instead report ±X, so this assertion proves
            // the bone frame is used.
            Assert.True(
                press.Direction.Z <= -0.8f && MathF.Abs(press.Direction.X) <= 0.35f,
                $"The bone-local direction should resolve to −Z under the rotated rig with a yaw-distinct proxy " +
                $"frame; direction was {press.Direction}.");

            Assert.Equal(RawContactPhase.Begin, fixture.Monitor.GetRecentRawEvents()[0].Phase);
            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Assert.Equal("Chest", active.Receiver.DisplayName);

            // The sweeper swept the hand body's own collision shapes and coalesced the single chest receiver.
            Assert.Equal(1, fixture.HandSource.QueryShapeCountForTesting);
            Assert.Equal(1, fixture.HandSource.LastSweepReceiverCountForTesting);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a fast approach classifies Impact/Hit at contact Begin from approach kinematics, with the raw
    /// normal pointing outward from the receiver surface into the counterpart (INTR-005 UR3, TR3, TR6).
    /// </summary>
    [Headless]
    [Fact]
    public async Task FastApproach_ClassifiesImpactAtBegin()
    {
        SceneTree sceneTree = GetSceneTree();
        ContactFixture fixture = await ContactFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, HeadY, 0.24f));
            // One 0.115 m step at 60 Hz approaches at ~6.9 m/s, well above the 1.5 m/s impact threshold.
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, HeadY, HeadHalfExtent + HandHalfExtent - 0.025f));
            await ContactFixture.HoldHandAsync(sceneTree, 4);

            ContactEvent impact = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Impact, impact.Class);
            Assert.Null(impact.PreviousClass);
            Assert.True(
                impact.Intensity is >= 3.0f and <= 9.0f,
                $"The impact intensity should report the ~6.9 m/s approach speed; intensity was {impact.Intensity:F2} m/s.");
            Assert.Equal("Head", impact.Receiver.DisplayName);
            Assert.Equal("Sky", impact.Receiver.OwnerName);
            Assert.Equal("Player's Right Hand", impact.Counterpart.Descriptor);

            // The raw contact normal is the receiver-surface outward normal: the hand approaches the head's
            // world +Z face, so the normal points from the head into the hand — along +Z. The Begin sample
            // carries the approach itself: its normal closing speed matches the approach.
            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            System.Numerics.Vector3 normal = active.LastSample.ContactNormal;
            Assert.True(
                normal.Z >= 0.9f && MathF.Abs(normal.X) <= 0.1f && MathF.Abs(normal.Y) <= 0.1f,
                $"The receiver-surface outward normal should face the approaching hand along +Z; normal was {normal}.");
            RawContactEvent begin = Assert.Single(
                fixture.Monitor.GetRecentRawEvents(),
                rawEvent => rawEvent.Phase == RawContactPhase.Begin && rawEvent.Receiver.DisplayName == "Head");
            Assert.True(
                begin.Sample.PressRate >= 3.0f,
                $"A 6.9 m/s approach should report a matching normal closing speed; press rate was {begin.Sample.PressRate:F2} m/s.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a slow lateral stroke revises an earlier Touch classification to Caress/Stroke exactly once as
    /// a single transition (INTR-005 UR3, TR6).
    /// </summary>
    [Headless]
    [Fact]
    public async Task SlowLateralStroke_RevisesTouchToCaressExactlyOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        ContactFixture fixture = await ContactFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.20f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.18f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.16f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, TouchZ));

            // Slide at 0.3 m/s along the chest surface — above the 0.2 m/s slip threshold — for 0.3 s, past the
            // 0.25 s confirmation window.
            const float slideStep = 0.3f * PressSecondsPerTick;
            for (int step = 1; step <= 18; step += 1)
            {
                await fixture.MoveHandAsync(sceneTree, HandPose(step * slideStep, ChestY, TouchZ));
            }

            await ContactFixture.HoldHandAsync(sceneTree, 2);

            Assert.Equal(
            [
                ContactClass.Touch,
                ContactClass.Caress,
            ], fixture.Events.Select(contactEvent => contactEvent.Class));

            ContactEvent caress = fixture.Events[1];
            Assert.Equal(ContactClass.Touch, caress.PreviousClass);
            Assert.True(
                caress.Duration.TotalSeconds is >= 0.15 and <= 0.4,
                $"The caress revision should confirm after the 0.25 s window; duration was {caress.Duration.TotalSeconds:F3} s.");
            Assert.True(
                caress.Intensity is >= 0.2f and <= 0.45f,
                $"The caress intensity should report the ~0.3 m/s tangential slip; intensity was {caress.Intensity:F2} m/s.");
            Assert.Equal("Chest", caress.Receiver.DisplayName);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies removing the hand ends the raw contact cleanly — one Begin, updates, one End under one stable
    /// id — with no spurious classified emissions, and that steady contact stays silent after its entry
    /// transition (INTR-005 TR3, TR4, TR6).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HandRemoval_EndsContactWithoutSpuriousEmissions()
    {
        SceneTree sceneTree = GetSceneTree();
        ContactFixture fixture = await ContactFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.20f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.18f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.16f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, TouchZ));

            // Steady contact holds its entry classification and emits nothing further.
            await ContactFixture.HoldHandAsync(sceneTree, 8);
            ContactEvent touch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, touch.Class);

            // Retract far past the receiver and let the tick cycle flush the End.
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.40f));
            await ContactFixture.HoldHandAsync(sceneTree, 4);

            _ = Assert.Single(fixture.Events);
            Assert.Empty(fixture.Monitor.GetActiveRawContacts());

            IReadOnlyList<RawContactEvent> rawEvents =
            [
                .. fixture.Monitor.GetRecentRawEvents().Where(rawEvent => rawEvent.Receiver.DisplayName == "Chest"),
            ];
            ContactId contactId = rawEvents[0].Contact;
            Assert.Equal(RawContactPhase.Begin, rawEvents[0].Phase);
            Assert.Equal(RawContactPhase.End, rawEvents[^1].Phase);
            Assert.Equal(1, rawEvents.Count(rawEvent => rawEvent.Phase == RawContactPhase.Begin));
            Assert.Equal(1, rawEvents.Count(rawEvent => rawEvent.Phase == RawContactPhase.End));
            Assert.True(rawEvents.Count(rawEvent => rawEvent.Phase == RawContactPhase.Update) >= 1);
            Assert.All(rawEvents, rawEvent => Assert.Equal(contactId, rawEvent.Contact));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies receiver-side velocity sampling: an NPC part moving under a resting hand produces finite,
    /// correctly-signed relative velocities in the raw measurements (INTR-005 TR3, TR18f).
    /// </summary>
    [Headless]
    [Fact]
    public async Task MovingNpcPart_ProducesFiniteRelativeVelocities()
    {
        SceneTree sceneTree = GetSceneTree();
        ContactFixture fixture = await ContactFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.20f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.18f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.16f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, TouchZ));
            await ContactFixture.HoldHandAsync(sceneTree, 2);

            // The NPC leans toward the resting hand at 0.5 m/s: the receiver moves +Z under a static
            // counterpart, so the relative velocity of the counterpart with respect to the receiver is −Z.
            const float npcStep = 0.5f * PressSecondsPerTick;
            Vector3 npcStepVector = new(0.0f, 0.0f, npcStep);
            for (int step = 0; step < 6; step += 1)
            {
                fixture.NpcRoot.GlobalPosition = fixture.NpcRoot.GlobalPosition + npcStepVector;
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            System.Numerics.Vector3 relativeVelocity = active.LastSample.RelativeVelocity;
            Assert.True(
                float.IsFinite(relativeVelocity.X) && float.IsFinite(relativeVelocity.Y)
                && float.IsFinite(relativeVelocity.Z),
                $"The relative velocity must stay finite on a moving receiver; was {relativeVelocity}.");
            Assert.True(
                relativeVelocity.Z is <= -0.35f and >= -0.65f,
                $"The receiver moving +Z at 0.5 m/s under a static hand should give a −Z relative velocity near 0.5; was {relativeVelocity}.");
            Assert.True(
                MathF.Abs(relativeVelocity.X) <= 0.1f && MathF.Abs(relativeVelocity.Y) <= 0.1f,
                $"The relative velocity should stay on the contact normal axis; was {relativeVelocity}.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static float ChestY => _fixtureOrigin.Y + 1.28f;

    private static float HeadY => _fixtureOrigin.Y + 1.62f;

    /// <summary>A hand pose in world space: lateral offset, height, and approach distance from the NPC.</summary>
    private static Vector3 HandPose(float lateralX, float y, float approachZ)
        => _fixtureOrigin + new Vector3(lateralX, y - _fixtureOrigin.Y, approachZ);

    /// <summary>
    /// Builds the vertical-slice wiring: an NPC rig whose generated <see cref="PhysicalBodyPart3D" /> proxies
    /// join the <c>contact_receiver</c> group, a <see cref="ContactMonitor" /> on the NPC host, and a player
    /// hand body with a <see cref="ContactSource" /> sweeper. The NPC container is yaw-rotated 180° so bone-local
    /// direction assertions are non-trivial.
    /// </summary>
    private sealed class ContactFixture(
        Node3D root,
        Node3D npcRoot,
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
        ];

        public Node3D NpcRoot { get; } = npcRoot;

        public ContactMonitor Monitor { get; } = monitor;

        public IReadOnlyList<ContactEvent> Events { get; } = events;

        public static async Task<ContactFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "ContactDetectionTestRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            Node3D npcRoot = new()
            {
                Name = "Npc",
                Transform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, Mathf.Pi, 0.0f)), Vector3.Zero),
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
                Name = "PlayerRightHand",
                CollisionLayer = 8,
                CollisionMask = 0,
                // Matches the production hand-actuator bodies and the legacy controller fixture: direct
                // transform writes must stick instead of being snapped back by physics interpolation.
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
            handBody.GlobalPosition = HandPose(0.0f, ChestY, 0.40f);

            ContactSource handSource = new()
            {
                Name = "RightHandContactSource",
                PartyDisplayName = "Right Hand",
                OwnerName = "Player",
            };
            handBody.AddChild(handSource);

            List<ContactEvent> events = [];
            monitor.ContactEventEmitted += events.Add;

            // One process frame lets the rig's deferred generation run; the physics frames prime the sweeper's
            // finite-difference velocity sampling with a still hand.
            await WaitForFramesAsync(sceneTree, 1);
            Assert.True(rig.GeneratedProxyCount >= 2, "The NPC rig should generate Chest and Head receiver proxies.");
            Assert.All(
                CollectProxyBodies(rig),
                proxy => Assert.True(proxy.IsInGroup(ContactReceiverGroup.Name),
                    $"Generated proxy '{proxy.Name}' should join the {ContactReceiverGroup.Name} group."));
            PlaceAttachmentsAtBoneRests(rig);
            await WaitForPhysicsFramesAsync(sceneTree, 5);

            return new ContactFixture(root, npcRoot, monitor, handBody, handSource, events);
        }

        public async Task MoveHandAsync(SceneTree sceneTree, Vector3 worldPosition)
        {
            HandBody.GlobalPosition = worldPosition;
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        public static async Task HoldHandAsync(SceneTree sceneTree, int physicsFrames)
            => await WaitForPhysicsFramesAsync(sceneTree, physicsFrames);

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        private Node3D Root { get; } = root;

        private AnimatableBody3D HandBody { get; } = handBody;

        public ContactSource HandSource { get; } = handSource;

        private static IEnumerable<PhysicsBody3D> CollectProxyBodies(DynamicPhysicalRig rig)
        {
            foreach ((string boneName, _, _) in BoneDefinitions)
            {
                foreach (PhysicsBody3D proxy in rig.GetGeneratedProxyBodiesForBone(boneName))
                {
                    yield return proxy;
                }
            }
        }

        /// <summary>
        /// Places each generated attachment at its bone's global rest pose: the test skeleton never animates,
        /// so the attachments do not receive pose updates an animated skeleton would drive. The chest proxy
        /// body additionally receives a distinguishing +90° local yaw — its source body counter-rotates the
        /// shape, so the world collider geometry is unchanged while the proxy-body frame provably differs from
        /// the bone frame the monitor must resolve through (step-3 review preamble).
        /// </summary>
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
                        if (boneName == "Chest")
                        {
                            proxy.Transform = new Transform3D(
                                Basis.FromEuler(new Vector3(0.0f, Mathf.Pi * 0.5f, 0.0f)),
                                Vector3.Zero);
                        }
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

            return skeleton;
        }

        private static PackedScene CreateColliderSourceScene()
        {
            Node root = new()
            {
                Name = "CollidersRoot",
            };

            // The chest source body yaws −90°: the generated shape counter-rotates the chest proxy's
            // distinguishing +90° local yaw (see PlaceAttachmentsAtBoneRests), keeping the world collider
            // geometry identical while the proxy-body frame differs from the bone frame.
            CreateSourceAttachment(root, "Chest", new Vector3(0.40f, 0.44f, 0.18f), -Mathf.Pi * 0.5f);
            CreateSourceAttachment(root, "Head", new Vector3(0.18f, 0.20f, 0.18f));

            PackedScene sourceScene = new();
            Error packResult = sourceScene.Pack(root);
            root.Free();

            Assert.Equal(Error.Ok, packResult);
            return sourceScene;
        }

        private static void CreateSourceAttachment(Node root, string boneName, Vector3 boxSize, float sourceBodyYawRadians = 0f)
        {
            BoneAttachment3D sourceBoneAttachment = new()
            {
                Name = $"{boneName}Attachment",
                BoneName = boneName,
            };
            AnimatableBody3D sourceBody = new()
            {
                Name = $"{boneName}SourceBody",
                Transform = new Transform3D(
                    Basis.FromEuler(new Vector3(0.0f, sourceBodyYawRadians, 0.0f)),
                    Vector3.Zero),
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
