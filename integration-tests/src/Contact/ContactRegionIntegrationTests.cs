using AlleyCat.Contact;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// End-to-end coverage of authored sub-part contact regions (INTR-005 plan step 4, TR16, TR18c, TR18d): a
/// "Right Cheek" annotation authored in the collider source scene resolves through the
/// <see cref="DynamicPhysicalRig" /> bone workflow onto the NPC head, and a player-hand contact landing inside
/// it is refined beyond body-part granularity — while a contact on the unannotated chest stays at body-part
/// granularity, and the authored regions generate no physics bodies, collision shapes, or extra nodes of any
/// kind.
/// </summary>
public sealed class ContactRegionIntegrationTests
{
    private const float HandSize = 0.12f;
    private const float HandHalfExtent = HandSize * 0.5f;
    private const float ChestHalfDepth = 0.09f;
    private const float HeadHalfExtent = 0.09f;
    private const float HeadTouchZ = HeadHalfExtent + HandHalfExtent - 0.005f;
    private const float ChestTouchZ = ChestHalfDepth + HandHalfExtent - 0.005f;

    // The "Right Cheek" annotation volume in head-bone-local space (source-scene coordinates are the bone
    // frame): it covers the right half of the head's −Z face — the face plane sits at bone-local z = −0.09 and
    // a hand centred at world x = −0.05 overlaps the head within bone-local x ∈ [0.01, 0.09], so every
    // coalesced contact point the sweeper can report there is inside the box.
    private static readonly Vector3 _cheekCenter = new(0.05f, 0.0f, -0.09f);

    private static readonly Vector3 _cheekHalfExtents = new(0.05f, 0.09f, 0.02f);

    // The authored "Crown" annotation volume in head-bone-local space: it caps the head's top face at
    // bone-local y = +0.10. A 0.08 m hand centred over the head keeps its whole footprint — and therefore
    // every coalesced contact point — inside the 0.10 m crown footprint.
    private static readonly Vector3 _crownCenter = new(0.0f, 0.09f, 0.0f);

    private static readonly Vector3 _crownHalfExtents = new(0.05f, 0.03f, 0.05f);

    private const float CrownHandSize = 0.08f;
    private const float HeadHalfHeight = 0.10f;

    private static readonly Vector3 _fixtureOrigin = new(70.0f, 12.0f, 0.0f);

    /// <summary>
    /// Verifies a slow touch on the head's right side is refined to the authored "Right Cheek" region — name,
    /// tags, and primary mark carried on the event — while the body-part identity stays on the receiver
    /// (INTR-005 UR2, TR8, TR16).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CheekContact_RefinesHeadToAuthoredRegion()
    {
        SceneTree sceneTree = GetSceneTree();
        RegionFixture fixture = await RegionFixture.CreateAsync(sceneTree, authorRegions: true);

        try
        {
            // Slow approach below the impact threshold, offset onto the head's right half: Touch at Begin.
            await fixture.MoveHandAsync(sceneTree, HandPose(-0.05f, HeadY, 0.24f));
            await fixture.MoveHandAsync(sceneTree, HandPose(-0.05f, HeadY, 0.22f));
            await fixture.MoveHandAsync(sceneTree, HandPose(-0.05f, HeadY, 0.20f));
            await fixture.MoveHandAsync(sceneTree, HandPose(-0.05f, HeadY, 0.18f));
            await fixture.MoveHandAsync(sceneTree, HandPose(-0.05f, HeadY, 0.16f));
            await fixture.MoveHandAsync(sceneTree, HandPose(-0.05f, HeadY, HeadTouchZ));
            await RegionFixture.HoldHandAsync(sceneTree, 2);

            ContactEvent touch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Equal("Head", touch.Receiver.DisplayName);
            Assert.Equal("Sky's Head", touch.Receiver.Descriptor);

            // Independent oracle: the engine-observed world contact point, transformed into the head bone's
            // frame here, lies inside the authored cheek box by plain arithmetic — so the event must carry it.
            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Vector3 boneLocal = fixture.WorldToHeadBoneLocal(ContactVectors.ToGodot(active.LastSample.ContactPoint));
            Assert.InRange(boneLocal.X, _cheekCenter.X - _cheekHalfExtents.X, _cheekCenter.X + _cheekHalfExtents.X);
            Assert.InRange(MathF.Abs(boneLocal.Y), 0f, _cheekHalfExtents.Y);
            Assert.InRange(
                boneLocal.Z, _cheekCenter.Z - _cheekHalfExtents.Z, _cheekCenter.Z + _cheekHalfExtents.Z);

            ContactRegionMatch cheek = Assert.Single(touch.Regions);
            Assert.Equal("Right Cheek", cheek.Name);
            Assert.True(cheek.IsPrimary, "The sole matching region is the primary.");
            Assert.Equal(["face", "cheek"], cheek.Tags);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a touch on the crown of the head is refined to the authored "Crown" region — closing the
    /// cheek-versus-crown discrimination in both directions: the sole match proves the higher-priority
    /// face-anchored cheek region does not claim a crown contact (INTR-005 UR2, TR16, TR18d).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CrownContact_RefinesHeadToAuthoredCrownRegion()
    {
        SceneTree sceneTree = GetSceneTree();
        RegionFixture fixture = await RegionFixture.CreateAsync(sceneTree, authorRegions: true, handSize: CrownHandSize);

        try
        {
            // Slow vertical descent at the head's centre: 0.02 m steps at 60 Hz stay below the impact
            // threshold; the final 0.015 m step lands at ~0.9 m/s — Touch at Begin.
            await fixture.MoveHandAsync(sceneTree, CrownPose(_fixtureOrigin.Y + 1.89f));
            await fixture.MoveHandAsync(sceneTree, CrownPose(_fixtureOrigin.Y + 1.87f));
            await fixture.MoveHandAsync(sceneTree, CrownPose(_fixtureOrigin.Y + 1.85f));
            await fixture.MoveHandAsync(sceneTree, CrownPose(_fixtureOrigin.Y + 1.83f));
            await fixture.MoveHandAsync(sceneTree, CrownPose(_fixtureOrigin.Y + 1.81f));
            await fixture.MoveHandAsync(sceneTree, CrownPose(_fixtureOrigin.Y + 1.79f));
            await fixture.MoveHandAsync(sceneTree, CrownPose(_fixtureOrigin.Y + 1.77f));
            await fixture.MoveHandAsync(sceneTree, CrownPose(CrownTouchY));
            await RegionFixture.HoldHandAsync(sceneTree, 2);

            ContactEvent touch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Equal("Head", touch.Receiver.DisplayName);
            Assert.Equal("Sky's Head", touch.Receiver.Descriptor);

            // Independent oracle: the engine-observed world contact point, transformed into the head bone's
            // frame here, lies inside the authored crown box by plain arithmetic — so the event must carry it.
            RawContactSnapshot active = Assert.Single(fixture.Monitor.GetActiveRawContacts());
            Vector3 boneLocal = fixture.WorldToHeadBoneLocal(ContactVectors.ToGodot(active.LastSample.ContactPoint));
            Assert.InRange(boneLocal.X, _crownCenter.X - _crownHalfExtents.X, _crownCenter.X + _crownHalfExtents.X);
            Assert.InRange(boneLocal.Y, _crownCenter.Y - _crownHalfExtents.Y, _crownCenter.Y + _crownHalfExtents.Y);
            Assert.InRange(boneLocal.Z, _crownCenter.Z - _crownHalfExtents.Z, _crownCenter.Z + _crownHalfExtents.Z);

            ContactRegionMatch crown = Assert.Single(touch.Regions);
            Assert.Equal("Crown", crown.Name);
            Assert.True(crown.IsPrimary, "The sole matching region is the primary.");
            Assert.Equal(["face"], crown.Tags);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies a touch on the unannotated chest resolves no regions and stays at body-part granularity
    /// (INTR-005 UR2, TR16).
    /// </summary>
    [Headless]
    [Fact]
    public async Task UnannotatedPartContact_StaysAtBodyPartGranularity()
    {
        SceneTree sceneTree = GetSceneTree();
        RegionFixture fixture = await RegionFixture.CreateAsync(sceneTree, authorRegions: true);

        try
        {
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.20f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.18f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, 0.16f));
            await fixture.MoveHandAsync(sceneTree, HandPose(0.0f, ChestY, ChestTouchZ));
            await RegionFixture.HoldHandAsync(sceneTree, 2);

            ContactEvent touch = Assert.Single(fixture.Events);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Equal("Chest", touch.Receiver.DisplayName);
            Assert.Empty(touch.Regions);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies authored regions add no physics presence (INTR-005 TR16, acceptance criterion: generated-node
    /// counts unchanged): building the NPC from the same source scene with and without region markers yields
    /// identical node, physics-body, and collision-shape counts under the receiver host, and no
    /// <see cref="ContactRegion" /> node exists in the live tree — the markers live only in the source scene.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AuthoredRegions_GenerateNoPhysicsPresence()
    {
        SceneTree sceneTree = GetSceneTree();

        NpcBuild withoutRegions = await BuildNpcAsync(sceneTree, authorRegions: false);
        NpcBuild withRegions = await BuildNpcAsync(sceneTree, authorRegions: true);

        try
        {
            // The authored profile resolves both regions through the source-scene bone workflow onto the head.
            ContactRegionProfile profile = withRegions.Monitor.RegionProfile
                ?? throw new InvalidOperationException("Expected the region fixture to configure a region profile.");
            IReadOnlyList<ContactRegionDefinition> definitions = profile.QueryRegionDefinitions();
            Assert.Equal(2, definitions.Count);
            Assert.All(definitions, definition => Assert.Equal("Head", definition.BoneName));
            Assert.Contains(definitions, definition => definition.Name == "Right Cheek");
            Assert.Contains(definitions, definition => definition.Name == "Crown");

            Assert.Equal(withoutRegions.Rig.GeneratedProxyCount, withRegions.Rig.GeneratedProxyCount);
            Assert.Equal(2, withRegions.Rig.GeneratedProxyCount);
            Assert.Equal(withoutRegions.TotalNodeCount, withRegions.TotalNodeCount);
            Assert.Equal(withoutRegions.PhysicsBodyCount, withRegions.PhysicsBodyCount);
            Assert.Equal(2, withRegions.PhysicsBodyCount);
            Assert.Equal(withoutRegions.CollisionShapeCount, withRegions.CollisionShapeCount);
            Assert.True(
                withRegions.CollisionShapeCount == 2,
                $"The chest and head proxies should own exactly one collision shape each; counted " +
                $"{withRegions.CollisionShapeCount}.");
            Assert.True(
                CountNodesOfType<ContactRegion>(sceneTree.CurrentScene!) == 0,
                "Region markers must not exist in the live tree; they are annotation data only.");
        }
        finally
        {
            withoutRegions.Dispose();
            withRegions.Dispose();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static float ChestY => _fixtureOrigin.Y + 1.28f;

    private static float HeadY => _fixtureOrigin.Y + 1.62f;

    private static float HeadTopY => HeadY + HeadHalfHeight;

    private static float CrownTouchY => HeadTopY + (CrownHandSize * 0.5f) - 0.005f;

    /// <summary>A hand pose in world space: lateral offset, height, and approach distance from the NPC.</summary>
    private static Vector3 HandPose(float lateralX, float y, float approachZ)
        => _fixtureOrigin + new Vector3(lateralX, y - _fixtureOrigin.Y, approachZ);

    /// <summary>A vertical crown-descent hand pose in world space, centred over the head.</summary>
    private static Vector3 CrownPose(float worldY) => new(_fixtureOrigin.X, worldY, _fixtureOrigin.Z);

    private static async Task<NpcBuild> BuildNpcAsync(SceneTree sceneTree, bool authorRegions)
    {
        Node3D root = new()
        {
            Name = $"ContactRegionTestRoot_{authorRegions}",
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

        Skeleton3D skeleton = RegionFixture.CreateNpcSkeleton();
        npcRoot.AddChild(skeleton);

        PackedScene sourceScene = RegionFixture.CreateSourceScene(authorRegions);
        DynamicPhysicalRig rig = new()
        {
            Name = "NpcPhysicalRig",
            TargetSkeleton = skeleton,
            ColliderProfile = new BodyColliderProfile
            {
                SourceScene = sourceScene,
            },
        };
        skeleton.AddChild(rig);

        ContactMonitor monitor = new()
        {
            Name = "NpcContactMonitor",
            OwnerName = "Sky",
            RegionProfile = authorRegions ? new ContactRegionProfile { SourceScene = sourceScene } : null,
        };
        npcRoot.AddChild(monitor);

        // One process frame lets the rig's deferred generation run.
        await WaitForFramesAsync(sceneTree, 1);
        Assert.True(rig.GeneratedProxyCount >= 2, "The NPC rig should generate Chest and Head receiver proxies.");

        return new NpcBuild(
            root,
            rig,
            monitor,
            CountNodes(npcRoot),
            CountNodesOfType<PhysicsBody3D>(npcRoot),
            CountNodesOfType<CollisionShape3D>(npcRoot));
    }

    private static int CountNodes(Node root)
    {
        int count = 1;
        foreach (Node child in root.GetChildren())
        {
            count += CountNodes(child);
        }

        return count;
    }

    private static int CountNodesOfType<T>(Node root)
        where T : Node
    {
        int count = root is T ? 1 : 0;
        foreach (Node child in root.GetChildren())
        {
            count += CountNodesOfType<T>(child);
        }

        return count;
    }

    private readonly record struct NpcBuild(
        Node3D Root,
        DynamicPhysicalRig Rig,
        ContactMonitor Monitor,
        int TotalNodeCount,
        int PhysicsBodyCount,
        int CollisionShapeCount)
    {
        public void Dispose()
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
            }
        }
    }

    /// <summary>
    /// Builds the region-slice wiring: the NPC rig from a source scene carrying authored region markers under
    /// the head bone attachment, a <see cref="ContactMonitor" /> bound to a <see cref="ContactRegionProfile" />
    /// over the same scene, and a player hand sweeper. The skeleton's poses are pinned to its rests so the
    /// live bone frame the monitor resolves through equals the rest frame the source scene authors against —
    /// the state a real skeleton holds when not animated.
    /// </summary>
    private sealed class RegionFixture(
        Node3D root,
        Skeleton3D skeleton,
        ContactMonitor monitor,
        AnimatableBody3D handBody,
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

        public ContactMonitor Monitor { get; } = monitor;

        public IReadOnlyList<ContactEvent> Events { get; } = events;

        public static async Task<RegionFixture> CreateAsync(SceneTree sceneTree, bool authorRegions, float handSize = HandSize)
        {
            Node3D root = new()
            {
                Name = "ContactRegionFixtureRoot",
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

            PackedScene sourceScene = CreateSourceScene(authorRegions);
            DynamicPhysicalRig rig = new()
            {
                Name = "NpcPhysicalRig",
                TargetSkeleton = skeleton,
                ColliderProfile = new BodyColliderProfile
                {
                    SourceScene = sourceScene,
                },
            };
            skeleton.AddChild(rig);

            ContactMonitor monitor = new()
            {
                Name = "NpcContactMonitor",
                OwnerName = "Sky",
                RegionProfile = authorRegions ? new ContactRegionProfile { SourceScene = sourceScene } : null,
            };
            npcRoot.AddChild(monitor);

            AnimatableBody3D handBody = new()
            {
                Name = "PlayerRightHand",
                CollisionLayer = 8,
                CollisionMask = 0,
                SyncToPhysics = false,
            };
            handBody.AddChild(new CollisionShape3D
            {
                Name = "HandShape",
                Shape = new BoxShape3D
                {
                    Size = new Vector3(handSize, handSize, handSize),
                },
            });
            root.AddChild(handBody);
            handBody.GlobalPosition = HandPose(0.0f, HeadY, 0.40f);

            ContactSource handSource = new()
            {
                Name = "RightHandContactSource",
                PartyDisplayName = "Right Hand",
                OwnerName = "Player",
            };
            handBody.AddChild(handSource);

            List<ContactEvent> events = [];
            monitor.ContactEventEmitted += events.Add;

            await WaitForFramesAsync(sceneTree, 1);
            Assert.True(rig.GeneratedProxyCount >= 2, "The NPC rig should generate Chest and Head receiver proxies.");
            PlaceAttachmentsAtBoneRests(rig);
            await WaitForPhysicsFramesAsync(sceneTree, 5);

            return new RegionFixture(root, skeleton, monitor, handBody, events);
        }

        public async Task MoveHandAsync(SceneTree sceneTree, Vector3 worldPosition)
        {
            handBody.GlobalPosition = worldPosition;
            await WaitForPhysicsFramesAsync(sceneTree, 1);
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

        /// <summary>
        /// Transforms a world point into the head bone's local frame using the skeleton's live global pose —
        /// the frame TR5/TR16 define — independently of the monitor's internals; used as the test's oracle.
        /// </summary>
        public Vector3 WorldToHeadBoneLocal(Vector3 worldPoint)
        {
            int headBoneIndex = skeleton.FindBone("Head");
            Assert.True(headBoneIndex >= 0, "The fixture skeleton should expose the Head bone.");
            Transform3D headFrame = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(headBoneIndex);
            return headFrame.AffineInverse() * worldPoint;
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

        internal static Skeleton3D CreateNpcSkeleton()
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

            // Pin every bone's pose to its rest: the monitor resolves through live poses while the source
            // scene authors against the rest frame, and a real skeleton holds pose = rest when not animated.
            for (int boneIndex = 0; boneIndex < skeleton.GetBoneCount(); boneIndex += 1)
            {
                skeleton.SetBonePose(boneIndex, skeleton.GetBoneRest(boneIndex));
            }

            return skeleton;
        }

        internal static PackedScene CreateSourceScene(bool authorRegions)
        {
            Node root = new()
            {
                Name = "CollidersRoot",
            };

            _ = CreateSourceAttachment(root, "Chest", new Vector3(0.40f, 0.44f, 0.18f));
            BoneAttachment3D headAttachment = CreateSourceAttachment(root, "Head", new Vector3(0.18f, 0.20f, 0.18f));

            if (authorRegions)
            {
                ContactRegion cheek = new()
                {
                    Name = "RightCheekRegion",
                    RegionName = "Right Cheek",
                    Tags = ["face", "cheek"],
                    Priority = 10,
                    Position = _cheekCenter,
                    HalfExtents = _cheekHalfExtents,
                };
                headAttachment.AddChild(cheek);
                // PackedScene.Pack only serialises nodes owned by the packed root.
                cheek.Owner = root;

                ContactRegion crown = new()
                {
                    Name = "CrownRegion",
                    RegionName = "Crown",
                    Tags = ["face"],
                    Priority = 0,
                    Position = new Vector3(0.0f, 0.09f, 0.0f),
                    HalfExtents = new Vector3(0.05f, 0.03f, 0.05f),
                };
                headAttachment.AddChild(crown);
                crown.Owner = root;
            }

            PackedScene sourceScene = new();
            Error packResult = sourceScene.Pack(root);
            root.Free();

            Assert.Equal(Error.Ok, packResult);
            return sourceScene;
        }

        private static BoneAttachment3D CreateSourceAttachment(Node root, string boneName, Vector3 boxSize)
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
            return sourceBoneAttachment;
        }
    }
}
