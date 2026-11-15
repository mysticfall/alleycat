using AlleyCat.Contact;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterRoot = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Contact;

/// <summary>
/// Production activation coverage (INTR-005 step 8, TR12, TR13, AC-T7/T8/T11): the shipped character
/// templates activate contact detection without bespoke test fixtures. Both role templates host the
/// production <see cref="ContactMonitor" /> and the <see cref="HandContactSweeperController" /> hand
/// sweepers. Classified contact events flow end-to-end through the production-wired path in both directions
/// — a player hand driven into the NPC's chest-head corridor delivers a Touch event with owner attribution
/// and bone-local direction through the NPC template's monitor, and the NPC's hand sweeper driven into the
/// player's head delivers through the player template's monitor with the same contract — the controller's
/// <c>Enabled</c> flag passes through to the hosted sweepers, and the RIG-001 self-pair exclusion topology
/// with its wiring-order and regeneration re-supply contract holds on the player role against the player's
/// own generated proxies.
/// </summary>
public sealed class ProductionContactActivationIntegrationTests
{
    private const string NpcScenePath = "res://assets/characters/reference/ally_npc.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";

    private const string NpcId = "ally";
    private const string PlayerId = "reference_player";

    // Distinct from every bespoke Contact fixture origin so staged characters cannot interfere.
    private static readonly Vector3 _fixtureOrigin = new(190.0f, 22.0f, 0.0f);

    /// <summary>
    /// Verifies the production templates activate contact detection end-to-end: the player's right-hand
    /// sweeper — hosted by the player template's controller on the shared base-scene hand target — approaches
    /// the production NPC's head, and the NPC template's monitor delivers a classified Touch with NPC
    /// receiver attribution, player counterpart attribution, and a bone-local direction resolved onto the
    /// head's sagittal axis (INTR-005 TR11, TR12, TR13, AC-T7/T8).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerHandSweeper_IntoProductionNpc_DeliversClassifiedContactThroughTemplateMonitor()
    {
        SceneTree sceneTree = GetSceneTree();
        ProductionContactFixture fixture = await ProductionContactFixture.CreateAsync(sceneTree);

        try
        {
            // The production templates host their wiring: one hub-attributed monitor on each role root.
            ContactMonitor monitor = fixture.NpcRoot.GetNode<ContactMonitor>("ContactMonitor");
            Assert.Equal(NpcId, monitor.OwnerName);
            ContactMonitor playerMonitor = fixture.PlayerRoot.GetNode<ContactMonitor>("ContactMonitor");
            Assert.Equal(PlayerId, playerMonitor.OwnerName);

            ContactSource playerRightHand = fixture.PlayerRightHandSweeper;

            // Perpendicular head-front approach: teleport clear of the body 0.6 m out along the head proxy's
            // sagittal axis, then walk straight in — the head is spatially isolated above the torso, so the
            // corridor meets the head's front face alone.
            PhysicsBody3D headProxy = ProductionContactFixture.PickIsolatedProxy(fixture.NpcRig, "Head");
            Vector3 headFrontAxis = headProxy.GlobalTransform.Basis.Z.Normalized();
            fixture.PlayerRightHandTarget.GlobalPosition = headProxy.GlobalPosition + (headFrontAxis * 0.6f);
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            Assert.Equal(0, playerRightHand.LastSweepReceiverCountForTesting);

            // Straight-line approach in ≤ 0.02 m physics steps (~1.2 m/s at 60 Hz, below the 1.5 m/s impact
            // threshold), stopping at the first discovered receiver so contact classifies Touch at Begin.
            await ProductionContactFixture.MoveHandTowardsAsync(
                sceneTree,
                fixture.PlayerRightHandTarget,
                () => headProxy.GlobalPosition,
                stopWhenDiscovered: playerRightHand,
                maxStepMetres: 0.02f);
            Assert.True(
                playerRightHand.LastSweepReceiverCountForTesting > 0,
                "The player hand sweeper should discover the NPC's head before reaching its centre.");
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            // Exactly one classified transition fires: the Touch entry on the NPC's head, attributed to the
            // player's hand through the production delivery path.
            ContactEvent touch = Assert.Single(fixture.NpcEvents);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Null(touch.PreviousClass);
            Assert.Equal("Head", touch.Receiver.DisplayName);
            Assert.Equal(NpcId, touch.Receiver.OwnerName);
            Assert.Equal($"{NpcId}'s Head", touch.Receiver.Descriptor);
            Assert.Equal("Right Hand", touch.Counterpart.DisplayName);
            Assert.Equal(PlayerId, touch.Counterpart.OwnerName);
            Assert.Equal($"{PlayerId}'s Right Hand", touch.Counterpart.Descriptor);

            // The contacted head bone's local sagittal axis (±Z) dominates the receiver-relative direction.
            Assert.True(
                MathF.Abs(touch.Direction.Z) >= 0.7f,
                $"The head contact direction should resolve onto the head bone-local sagittal axis; was " +
                $"{touch.Direction}.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the player-as-receiver direction of the production delivery path: a controlled external hand
    /// sweeper — standing in for an NPC hand, because the production NPC hand targets are continuously
    /// actuated by the arm-IK actuator and cannot be kinematically teleported — approaches the production
    /// player's head through the same perpendicular corridor, and the player template's monitor delivers a
    /// classified Touch with player receiver attribution, NPC counterpart attribution, and a bone-local
    /// direction resolved onto the head's sagittal axis (INTR-005 TR11, TR12, TR13, AC-T7/T8).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ExternalHandSweeper_IntoProductionPlayer_DeliversClassifiedContactThroughTemplateMonitor()
    {
        SceneTree sceneTree = GetSceneTree();
        ProductionContactFixture fixture = await ProductionContactFixture.CreateAsync(sceneTree);

        try
        {
            // The controlled counterpart: an NPC-attributed hand body with its own sweeper, hosted under the
            // fixture root so teardown frees it with the staged characters.
            Node3D fixtureRoot = fixture.PlayerRoot.GetParentNode3D()
                ?? throw new InvalidOperationException("Expected the fixture root to parent the hand body.");
            AnimatableBody3D handBody = new()
            {
                Name = "NpcRightHand",
                CollisionLayer = 8,
                CollisionMask = 0,
                // Direct transform writes must stick instead of being snapped back by physics interpolation.
                SyncToPhysics = false,
            };
            handBody.AddChild(new CollisionShape3D
            {
                Name = "HandShape",
                Shape = new BoxShape3D { Size = new Vector3(0.12f, 0.12f, 0.12f) },
            });
            ContactSource handSource = new()
            {
                Name = "RightHandContactSource",
                PartyDisplayName = "Right Hand",
                OwnerName = NpcId,
            };
            handBody.AddChild(handSource);
            fixtureRoot.AddChild(handBody);

            // The player's authored rest pose can brush its own chest and upper arm with a hand sweeper —
            // genuine self-contact on non-excluded pairs — so history recorded before the approach is
            // cleared and the delivery assertion below scopes to the head receiver.
            fixture.PlayerEvents.Clear();

            // Perpendicular head-front approach mirroring the NPC-receiver fact: teleport clear of the body
            // 0.6 m out along the head proxy's sagittal axis, then walk straight in — the head is spatially
            // isolated above the torso, so the corridor meets the head's front face alone.
            PhysicsBody3D headProxy = ProductionContactFixture.PickIsolatedProxy(fixture.PlayerRig, "Head");
            Vector3 headFrontAxis = headProxy.GlobalTransform.Basis.Z.Normalized();
            handBody.GlobalPosition = headProxy.GlobalPosition + (headFrontAxis * 0.6f);
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            Assert.Equal(0, handSource.LastSweepReceiverCountForTesting);

            // Straight-line approach in ≤ 0.02 m physics steps (~1.2 m/s at 60 Hz, below the 1.5 m/s impact
            // threshold), stopping at the first discovered receiver so contact classifies Touch at Begin.
            await ProductionContactFixture.MoveHandTowardsAsync(
                sceneTree,
                handBody,
                () => headProxy.GlobalPosition,
                stopWhenDiscovered: handSource,
                maxStepMetres: 0.02f);
            Assert.True(
                handSource.LastSweepReceiverCountForTesting > 0,
                "The external hand sweeper should discover the player's head before reaching its centre.");
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            // Exactly one classified head transition fires through the player template's monitor: the Touch
            // entry on the player's head, attributed to the NPC's hand through the production delivery path.
            ContactEvent touch = Assert.Single(fixture.PlayerEvents, contactEvent => contactEvent.Receiver.DisplayName == "Head");
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Null(touch.PreviousClass);
            Assert.Equal("Head", touch.Receiver.DisplayName);
            Assert.Equal(PlayerId, touch.Receiver.OwnerName);
            Assert.Equal($"{PlayerId}'s Head", touch.Receiver.Descriptor);
            Assert.Equal("Right Hand", touch.Counterpart.DisplayName);
            Assert.Equal(NpcId, touch.Counterpart.OwnerName);
            Assert.Equal($"{NpcId}'s Right Hand", touch.Counterpart.Descriptor);

            // The contacted head bone's local sagittal axis (±Z) dominates the receiver-relative direction.
            Assert.True(
                MathF.Abs(touch.Direction.Z) >= 0.7f,
                $"The head contact direction should resolve onto the head bone-local sagittal axis; was " +
                $"{touch.Direction}.");

            // The delivery stays receiver-framed: the NPC's own monitor sees nothing from the player's head.
            Assert.Empty(fixture.NpcEvents);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the production hosting structure on both role templates: each character's controller hosts
    /// one sweeper per wired hand target with profile-backed query shapes, hub-resolved owner attribution,
    /// and its own limb's RIG-001 exception topology — the side-correct hand, lower-arm, and same-side
    /// finger proxies and nothing from the other side — and each role template hosts its own production
    /// <see cref="ContactMonitor" /> root child resolving receiver attribution from its character hub
    /// (INTR-005 TR12, TR13).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ProductionTemplates_HostHandSweepers_WithSideCorrectSelfPairExclusions()
    {
        SceneTree sceneTree = GetSceneTree();
        ProductionContactFixture fixture = await ProductionContactFixture.CreateAsync(sceneTree);

        try
        {
            AssertHostingTopology(fixture.NpcRoot, fixture.NpcController, NpcId);
            AssertHostingTopology(fixture.PlayerRoot, fixture.PlayerController, PlayerId);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the <c>Enabled</c> pass-through (step-6 review preamble): with the sweep stopped, a hand
    /// pushed deeper into an established chest contact delivers nothing and the hosted sweeper stops physics
    /// processing; re-enabling resumes the sweep so the still-overlapping hand delivers a fresh Touch entry
    /// through the production path (INTR-005 TR13).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerDisabled_StopsHostedSweeping_UntilReEnabled()
    {
        SceneTree sceneTree = GetSceneTree();
        ProductionContactFixture fixture = await ProductionContactFixture.CreateAsync(sceneTree);

        try
        {
            ContactSource playerRightHand = fixture.PlayerRightHandSweeper;

            // Establish a single-receiver head contact while enabled: perpendicular entry, stop at first
            // discovery so only the head pair is active.
            PhysicsBody3D headProxy = ProductionContactFixture.PickIsolatedProxy(fixture.NpcRig, "Head");
            Vector3 headFrontAxis = headProxy.GlobalTransform.Basis.Z.Normalized();
            fixture.PlayerRightHandTarget.GlobalPosition = headProxy.GlobalPosition + (headFrontAxis * 0.6f);
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            await ProductionContactFixture.MoveHandTowardsAsync(
                sceneTree,
                fixture.PlayerRightHandTarget,
                () => headProxy.GlobalPosition,
                stopWhenDiscovered: playerRightHand,
                maxStepMetres: 0.02f);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            ContactEvent initialTouch = Assert.Single(fixture.NpcEvents);
            Assert.Equal(ContactClass.Touch, initialTouch.Class);
            Assert.Equal("Head", initialTouch.Receiver.DisplayName);
            fixture.NpcEvents.Clear();

            // Disabling the controller stops the hosted sweepers mid-contact: pushing further into the head
            // delivers nothing, and the sweeper no longer physics-processes.
            fixture.PlayerController.Enabled = false;
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            Assert.False(playerRightHand.IsPhysicsProcessing(), "Disabling the controller should stop the hosted sweeper.");

            // The frozen last-sweep receiver count proves no sweep runs while disabled: pushing further into
            // the head leaves it unchanged and delivers nothing.
            int receiverCountWhenDisabled = playerRightHand.LastSweepReceiverCountForTesting;
            await ProductionContactFixture.MoveHandTowardsAsync(
                sceneTree,
                fixture.PlayerRightHandTarget,
                () => headProxy.GlobalPosition,
                stopWhenDiscovered: null,
                maxStepMetres: 0.02f);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Empty(fixture.NpcEvents);
            Assert.Equal(receiverCountWhenDisabled, playerRightHand.LastSweepReceiverCountForTesting);
            Assert.Contains(playerRightHand, fixture.PlayerController.HandSweepers);

            // Re-enabling resumes the hosted sweepers without re-hosting: the still-overlapping head pair
            // delivers a fresh Touch entry on the next ticks.
            fixture.PlayerController.Enabled = true;
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            ContactEvent touch = Assert.Single(fixture.NpcEvents);
            Assert.Equal(ContactClass.Touch, touch.Class);
            Assert.Null(touch.PreviousClass);
            Assert.Equal("Head", touch.Receiver.DisplayName);
            Assert.Equal(PlayerId, touch.Counterpart.OwnerName);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the player self-exclusion silence through the production player rig: the player's own hand
    /// and lower-arm proxies — which the player character's rig generates as receivers — are excluded from
    /// the player hand sweepers, so a hand target coinciding fully with its own limb proxies discovers no
    /// receivers even though the geometry genuinely overlaps (INTR-005 TR12, bounded by RIG-001 topology).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerHandSweeper_OwnLimbProxies_NeverDiscoverReceivers()
    {
        SceneTree sceneTree = GetSceneTree();
        ProductionContactFixture fixture = await ProductionContactFixture.CreateAsync(sceneTree);

        try
        {
            ContactSource playerRightHand = fixture.PlayerRightHandSweeper;

            PhysicsBody3D handProxy = Assert.Single(fixture.PlayerRig.GetGeneratedProxyBodiesForBone("RightHand"));
            PhysicsBody3D lowerArmProxy = Assert.Single(fixture.PlayerRig.GetGeneratedProxyBodiesForBone("RightLowerArm"));

            // Independent oracle: the target coincides fully with its own hand proxy, so an unexcluded sweep
            // would certainly discover it.
            fixture.PlayerRightHandTarget.GlobalPosition = handProxy.GlobalPosition;
            await WaitForPhysicsFramesAsync(sceneTree, 4);
            Assert.True(
                fixture.PlayerRightHandTarget.GlobalPosition.DistanceTo(handProxy.GlobalPosition) <= 0.02f,
                "The player hand target should fully overlap its own hand proxy.");

            Assert.Equal(0, playerRightHand.LastSweepReceiverCountForTesting);
            Assert.Empty(fixture.NpcEvents);

            // Slide onto the adjacent-bone excluded pair: the lower-arm proxy (RIG-001 TR12).
            await ProductionContactFixture.MoveHandTowardsAsync(
                sceneTree,
                fixture.PlayerRightHandTarget,
                () => lowerArmProxy.GlobalPosition,
                stopWhenDiscovered: null,
                maxStepMetres: 0.02f);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Equal(0, playerRightHand.LastSweepReceiverCountForTesting);
            Assert.Empty(fixture.NpcEvents);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies the wiring-order and re-supply contract on the player production role: after the player
    /// rig's synchronous regeneration replaces every proxy, the controller re-supplies the sweeper's
    /// excluded receivers with the regenerated instances before the next sweep, so the excluded self pairs
    /// stay silent without duplicating the hosted sweepers (INTR-005 TR12, TR18e).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerRigRegeneration_ReSuppliesExclusions_BeforeTheNextSweep()
    {
        SceneTree sceneTree = GetSceneTree();
        ProductionContactFixture fixture = await ProductionContactFixture.CreateAsync(sceneTree);

        try
        {
            ContactSource playerRightHand = fixture.PlayerRightHandSweeper;
            PhysicsBody3D oldHandProxy = Assert.Single(fixture.PlayerRig.GetGeneratedProxyBodiesForBone("RightHand"));
            ulong oldHandProxyId = oldHandProxy.GetInstanceId();

            fixture.PlayerRig.RegenerateNow();
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            PhysicsBody3D newHandProxy = Assert.Single(fixture.PlayerRig.GetGeneratedProxyBodiesForBone("RightHand"));
            Assert.NotEqual(oldHandProxyId, newHandProxy.GetInstanceId());

            Assert.Equal(2, fixture.PlayerController.HandSweepers.Count);
            Assert.All(playerRightHand.ExcludedReceivers, excluded => Assert.True(GodotObject.IsInstanceValid(excluded)));
            Assert.Contains(newHandProxy, playerRightHand.ExcludedReceivers);

            // The excluded pair stays silent against the regenerated proxies: the target returns onto its own
            // new hand proxy and discovers no receiver.
            fixture.PlayerRightHandTarget.GlobalPosition = newHandProxy.GlobalPosition;
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Equal(0, playerRightHand.LastSweepReceiverCountForTesting);
            Assert.Empty(fixture.NpcEvents);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static void AssertHostingTopology(CharacterRoot root, HandContactSweeperController controller, string ownerId)
    {
        Assert.Equal(2, controller.HandSweepers.Count);

        // Each role template hosts its own production monitor as a root child with blank authored
        // OwnerName, so receiver attribution resolves from the character hub's identity.
        ContactMonitor monitor = Assert.IsType<ContactMonitor>(
            Assert.Single(root.FindChildren("*", "ContactMonitor", true, false)));
        Assert.Equal(root, monitor.GetParent());
        Assert.Equal(ownerId, monitor.OwnerName);

        (ContactSource Sweeper, string Side, string HandBone, string LowerArmBone)[] hosted =
        [
            (controller.HandSweepers[0], "Right", "RightHand", "RightLowerArm"),
            (controller.HandSweepers[1], "Left", "LeftHand", "LeftLowerArm"),
        ];

        DynamicPhysicalRig rig = root.GetNode<DynamicPhysicalRig>("Female/GeneralSkeleton/DynamicPhysicalRig");
        foreach ((ContactSource sweeper, string side, string handBone, string lowerArmBone) in hosted)
        {
            Assert.Equal($"{side}HandContactSource", sweeper.Name.ToString());
            Assert.Equal(controller, sweeper.GetParent());
            Assert.Equal(root.GetNode<AnimatableBody3D>($"IKTargets/{side}Hand"), sweeper.TrackedBody);
            Assert.True(sweeper.QueryShapeCountForTesting > 0, $"The {side} sweeper should carry profile query shapes.");
            Assert.Equal(ownerId, sweeper.OwnerName);

            // The exclusion set covers exactly the limb's RIG-001 topology: its own hand, lower arm, and
            // same-side finger proxies — and nothing from the other side's hand.
            IReadOnlyList<PhysicsBody3D> handProxies = rig.GetGeneratedProxyBodiesForBone(handBone);
            IReadOnlyList<PhysicsBody3D> lowerArmProxies = rig.GetGeneratedProxyBodiesForBone(lowerArmBone);
            IReadOnlyList<PhysicsBody3D> fingerProxies = rig.GetGeneratedFingerProxyBodiesForHand(handBone);

            Assert.Contains(handProxies[0], sweeper.ExcludedReceivers);
            if (lowerArmProxies.Count > 0)
            {
                Assert.Contains(lowerArmProxies[0], sweeper.ExcludedReceivers);
            }

            foreach (PhysicsBody3D fingerProxy in fingerProxies)
            {
                Assert.Contains(fingerProxy, sweeper.ExcludedReceivers);
            }

            string otherHandBone = side == "Right" ? "LeftHand" : "RightHand";
            IReadOnlyList<PhysicsBody3D> otherHandProxies = rig.GetGeneratedProxyBodiesForBone(otherHandBone);
            if (otherHandProxies.Count > 0)
            {
                Assert.DoesNotContain(otherHandProxies[0], sweeper.ExcludedReceivers);
            }

            Assert.All(sweeper.ExcludedReceivers, excluded => Assert.True(GodotObject.IsInstanceValid(excluded)));
        }
    }

    /// <summary>
    /// Stages the shipped NPC and player character scenes with the production player-identity override the
    /// reference player fixture authors, waits for the role installers and rig generation to host the
    /// template wiring, and records every classified event each role template's monitor emits.
    /// </summary>
    private sealed class ProductionContactFixture(
        Node3D root,
        CharacterRoot npcRoot,
        CharacterRoot playerRoot,
        DynamicPhysicalRig npcRig,
        DynamicPhysicalRig playerRig,
        HandContactSweeperController npcController,
        HandContactSweeperController playerController,
        AnimatableBody3D playerRightHandTarget,
        List<ContactEvent> npcEvents,
        List<ContactEvent> playerEvents)
    {
        private const int HostedSweeperWaitFrames = 240;

        public CharacterRoot NpcRoot { get; } = npcRoot;

        public CharacterRoot PlayerRoot { get; } = playerRoot;

        public DynamicPhysicalRig NpcRig { get; } = npcRig;

        public DynamicPhysicalRig PlayerRig { get; } = playerRig;

        public HandContactSweeperController NpcController { get; } = npcController;

        public HandContactSweeperController PlayerController { get; } = playerController;

        public AnimatableBody3D PlayerRightHandTarget { get; } = playerRightHandTarget;

        public List<ContactEvent> NpcEvents { get; } = npcEvents;

        public List<ContactEvent> PlayerEvents { get; } = playerEvents;

        public ContactSource PlayerRightHandSweeper => PlayerController.HandSweepers[0];

        public static async Task<ProductionContactFixture> CreateAsync(SceneTree sceneTree)
        {
            Node3D root = new()
            {
                Name = "ProductionContactActivationRoot",
                GlobalPosition = _fixtureOrigin,
            };
            Node parent = sceneTree.CurrentScene
                ?? throw new InvalidOperationException("Expected an active current scene for 3D integration test setup.");
            parent.AddChild(root);

            CharacterRoot npcRoot = LoadAndInstantiate(NpcScenePath);
            // The shipped scenes share the authored 'ally' identity; production co-installs override the
            // player's identity (as reference_player_fixture.tscn does), so mirror that override here.
            CharacterRoot playerRoot = LoadAndInstantiate(PlayerScenePath);
            playerRoot.Id = PlayerId;

            root.AddChild(npcRoot);
            playerRoot.Position = new Vector3(0.0f, 0.0f, 2.5f);
            root.AddChild(playerRoot);

            // The role installers run on ready (deferred), and the controllers host their sweepers once the
            // rigs' deferred generation completes — poll physics frames until both roles are fully hosted
            // and the sweepers have run their first sweep (query shapes resolved).
            HandContactSweeperController? npcController = null;
            HandContactSweeperController? playerController = null;
            for (int frame = 0; frame < HostedSweeperWaitFrames; frame += 1)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                npcController ??= npcRoot.GetNodeOrNull<HandContactSweeperController>("ContactSweepers");
                playerController ??= playerRoot.GetNodeOrNull<HandContactSweeperController>("ContactSweepers");
                if (IsSweeping(npcController) && IsSweeping(playerController))
                {
                    break;
                }
            }

            Assert.NotNull(npcController);
            Assert.NotNull(playerController);
            if (!IsSweeping(npcController) || !IsSweeping(playerController))
            {
                throw new InvalidOperationException(
                    "The production templates did not host sweeping hand sweepers in time; NPC hosted " +
                    $"{npcController.HandSweepers.Count} sweepers, player hosted {playerController.HandSweepers.Count}.");
            }

            DynamicPhysicalRig npcRig = npcRoot.GetNode<DynamicPhysicalRig>("Female/GeneralSkeleton/DynamicPhysicalRig");
            DynamicPhysicalRig playerRig = playerRoot.GetNode<DynamicPhysicalRig>("Female/GeneralSkeleton/DynamicPhysicalRig");
            Assert.True(npcRig.GeneratedProxyCount > 0, "The production NPC rig should generate receiver proxies.");
            Assert.True(playerRig.GeneratedProxyCount > 0, "The production player rig should generate receiver proxies.");

            ContactMonitor monitor = npcRoot.GetNode<ContactMonitor>("ContactMonitor");
            List<ContactEvent> npcEvents = [];
            monitor.ContactEventEmitted += npcEvents.Add;

            ContactMonitor playerMonitor = playerRoot.GetNode<ContactMonitor>("ContactMonitor");
            List<ContactEvent> playerEvents = [];
            playerMonitor.ContactEventEmitted += playerEvents.Add;

            AnimatableBody3D playerRightHandTarget = playerRoot.GetNode<AnimatableBody3D>("IKTargets/RightHand");

            return new ProductionContactFixture(
                root,
                npcRoot,
                playerRoot,
                npcRig,
                playerRig,
                npcController,
                playerController,
                playerRightHandTarget,
                npcEvents,
                playerEvents);
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        private Node3D Root { get; } = root;

        /// <summary>
        /// Picks the highest generated proxy for a bone — for stacked torso proxies the topmost instance is
        /// the one spatially isolated from the neighbouring bones' proxies, so a perpendicular front approach
        /// touches it alone.
        /// </summary>
        public static PhysicsBody3D PickIsolatedProxy(DynamicPhysicalRig rig, string boneName)
        {
            IReadOnlyList<PhysicsBody3D> proxies = rig.GetGeneratedProxyBodiesForBone(boneName);
            Assert.NotEmpty(proxies);

            PhysicsBody3D highest = proxies[0];
            for (int proxyIndex = 1; proxyIndex < proxies.Count; proxyIndex += 1)
            {
                if (proxies[proxyIndex].GlobalPosition.Y > highest.GlobalPosition.Y)
                {
                    highest = proxies[proxyIndex];
                }
            }

            return highest;
        }

        private static bool IsSweeping(HandContactSweeperController? controller)
            => controller is not null
                && controller.HandSweepers.Count == 2
                && controller.HandSweepers[0].QueryShapeCountForTesting > 0
                && controller.HandSweepers[1].QueryShapeCountForTesting > 0;

        private static CharacterRoot LoadAndInstantiate(string scenePath)
        {
            PackedScene scene = ResourceLoader.Load<PackedScene>(scenePath)
                ?? throw new InvalidOperationException($"Failed to load the character scene at '{scenePath}'.");
            return Assert.IsType<CharacterRoot>(scene.Instantiate(), exactMatch: false);
        }

        /// <summary>
        /// Moves a hand target toward a world position in physics steps no larger than
        /// <paramref name="maxStepMetres" />, awaiting one physics frame per step — ~1.2 m/s at 0.02 m steps,
        /// below the impact threshold — optionally stopping as soon as <paramref name="stopWhenDiscovered" />
        /// discovers a receiver. The target position resolves live each step so idle-animation sway of the
        /// aimed body part cannot walk the hand off its approach corridor.
        /// </summary>
        public static async Task MoveHandTowardsAsync(
            SceneTree sceneTree,
            Node3D handTarget,
            Func<Vector3> worldPositionResolver,
            ContactSource? stopWhenDiscovered,
            float maxStepMetres)
        {
            int stepLimit = 800;
            while (stepLimit-- > 0)
            {
                if (stopWhenDiscovered?.LastSweepReceiverCountForTesting > 0)
                {
                    return;
                }

                Vector3 worldPosition = worldPositionResolver();
                Vector3 step = worldPosition - handTarget.GlobalPosition;
                float distance = step.Length();
                if (distance <= 1e-4f)
                {
                    break;
                }

                handTarget.GlobalPosition += step / distance * MathF.Min(distance, maxStepMetres);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            if (stopWhenDiscovered is not null)
            {
                // Let the final resting pose sweep at least once even when the target reached the destination.
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }
        }
    }
}
