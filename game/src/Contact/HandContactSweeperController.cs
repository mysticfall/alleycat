using AlleyCat.Core.Logging;
using AlleyCat.Rigging.Physics;
using Godot;
using Microsoft.Extensions.Logging;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.Contact;

/// <summary>
/// Production wiring that hosts one <see cref="ContactSource" /> sweeper on each of a character's arm-IK
/// hand targets (INTR-005 TR13): the player XR hand sweepers and the NPC hand sweepers, resolved per TR18e
/// as profile-backed query shapes on the IK hand targets, swept each physics tick. The controller resolves
/// the hand bone's authored collider shapes from the rig's <see cref="BodyColliderProfile" /> into pure
/// query geometry, so sensing the hand registers no collider of its own; the tracked hand target's authored
/// collision shapes — the player hand actuator's movement and mirrored finger shapes — join the sweep
/// through <see cref="ContactSource" />'s default child-shape queries, so each role senses the hand it
/// actually has. The arm IK system (IK-002) keeps each target's frame on the hand it drives, so the profile
/// hand shapes track the hand through the sweep.
/// </summary>
/// <remarks>
/// <para>
/// <strong>TR18e resolution — hand sweeper collider mechanics.</strong> The shipped choice is the
/// approved default: query shapes sourced from the <see cref="BodyColliderProfile" /> hand-bone descriptors,
/// carried on the IK hand target as <see cref="ContactSource.QueryShapes" /> (query-only resources), and
/// swept each physics tick by a <see cref="ContactSource" /> whose sweep geometry rides the tracked hand
/// target — the query <c>ShapeCast3D</c>s are parented to the target, so the shapes follow the hand wherever
/// the IK drives it. Registering the shapes as additional real colliders was rejected: the IK hand target is
/// a movement body whose collision behaviour must stay unchanged by contact sensing.
/// </para>
/// <para>
/// <strong>Sweeper hosting.</strong> Each sweeper node lives under this controller, never under the hand
/// target itself: the IK system activity-gates its hand targets — a target with no provider influence is
/// <c>ProcessMode.Disabled</c> together with its whole subtree — and a sweeper hosted on the target would
/// silently stop sensing whenever the hand's IK goes inactive, although the animation-driven hand keeps
/// moving and touching. Hosting under the controller keeps sensing active through animation-driven rest
/// while the sweep geometry still rides the tracked target.
/// </para>
/// <para>
/// <strong>Role parity (INTR-005 TR12).</strong> The player and NPC templates host this same controller:
/// both roles' hand targets come from the shared character base scene, both roles' hands are driven by arm
/// IK (IK-002), and <c>PlayerVRIK</c> applies the same RIG-001 hand-target exception topology the NPC rig
/// applies — so one hosting contract serves both roles, and parity is the wiring, not a copy.
/// </para>
/// <para>
/// <strong>Self-pair exclusions.</strong> Each sweeper's <see cref="ContactSource.ExcludedReceivers" /> is
/// wired with the RIG-001 hand-target exception topology of its limb — the tracked hand's own proxy, its
/// lower-arm proxies, and the same-side finger proxies (RIG-001 TR12 adjacent-bone, TR13 same-side, TR15
/// hand-target exceptions) — mirroring how <c>PlayerVRIK</c> and the NPC rig apply physics collision
/// exceptions to the hand targets. The set is resolved and assigned before the sweeper enters the tree,
/// honouring the sweeper's wiring-order contract (no pre-wiring tick can deliver an excluded pair), and
/// re-supplied whenever rig regeneration replaces the proxies.
/// </para>
/// <para>
/// <strong>Regeneration re-supply.</strong> The controller polls one physics priority step ahead of the
/// hosted sweepers and compares the rig's current proxy instances against the wired set, so a rebuild —
/// whose freed proxies would otherwise leave the exclusion set empty on the next sweep — is re-supplied in
/// the same tick, before the sweeper runs. A synchronous <see cref="DynamicPhysicalRig.RegenerateNow" />
/// invoked between the two callbacks leaves a single-tick window; the rig's own refresh path is deferred and
/// has no window.
/// </para>
/// <para>
/// <strong>Enable pass-through.</strong> <see cref="Enabled" /> passes through to the hosted sweepers'
/// physics processing, so disabling the controller stops the sweep mid-flight — the monitor ends the
/// affected pairs on its next tick — and re-enabling resumes it without losing the hosted sweeper state.
/// While disabled no new sweepers are created.
/// </para>
/// </remarks>
[GlobalClass]
public partial class HandContactSweeperController : Node
{
    private const string RightHandDisplayName = "Right Hand";
    private const string LeftHandDisplayName = "Left Hand";

    /// <summary>The generated rig whose collider profile backs the query shapes and whose proxies bound self-contact.</summary>
    [ExportGroup("Dependencies")]
    [Export]
    public DynamicPhysicalRig? PhysicalRig
    {
        get;
        set;
    }

    /// <summary>Right-hand arm-IK target body the right-hand sweeper rides (IK-002).</summary>
    [Export]
    public AnimatableBody3D? RightHandIKTarget
    {
        get;
        set;
    }

    /// <summary>Left-hand arm-IK target body the left-hand sweeper rides (IK-002).</summary>
    [Export]
    public AnimatableBody3D? LeftHandIKTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Enables hosting the hand sweepers; when cleared, no sweepers are created, and already-hosted sweepers
    /// stop sweeping until it is set again. This export is the supported stop/resume mechanism: the
    /// pass-through applies from the controller's own physics processing, so disabling the controller node
    /// itself (for example via <c>ProcessMode</c>) does not apply the stop.
    /// </summary>
    [Export]
    public bool Enabled
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Owning character's name for counterpart attribution; when left blank it resolves from the nearest
    /// ancestor <see cref="CharacterHub" /> hub's <see cref="CharacterHub.Id" />, so template-installed
    /// characters attribute contacts to their own identity.
    /// </summary>
    [ExportGroup("Identity")]
    [Export]
    public string OwnerName
    {
        get;
        set;
    } = string.Empty;

    /// <summary>Skeleton bone name of the tracked right hand, as authored in the collider profile.</summary>
    [ExportGroup("Bones")]
    [Export]
    public StringName RightHandBoneName
    {
        get;
        set;
    } = new("RightHand");

    /// <summary>Skeleton bone name of the tracked right lower arm, as authored in the collider profile.</summary>
    [Export]
    public StringName RightLowerArmBoneName
    {
        get;
        set;
    } = new("RightLowerArm");

    /// <summary>Skeleton bone name of the tracked left hand, as authored in the collider profile.</summary>
    [Export]
    public StringName LeftHandBoneName
    {
        get;
        set;
    } = new("LeftHand");

    /// <summary>Skeleton bone name of the tracked left lower arm, as authored in the collider profile.</summary>
    [Export]
    public StringName LeftLowerArmBoneName
    {
        get;
        set;
    } = new("LeftLowerArm");

    private ILogger<HandContactSweeperController>? _logger;

    private bool _warnedMissingColliderProfile;

    private HandSweeper? _rightHandSweeper;

    private HandSweeper? _leftHandSweeper;

    private bool _hostedSweepersProcessingEnabled = true;

    /// <summary>The hosted hand sweepers, right first — a diagnostics and validation surface.</summary>
    public IReadOnlyList<ContactSource> HandSweepers
    {
        get
        {
            List<ContactSource> sweepers = [];
            if (_rightHandSweeper?.Sweeper is { } right && IsInstanceValid(right))
            {
                sweepers.Add(right);
            }

            if (_leftHandSweeper?.Sweeper is { } left && IsInstanceValid(left))
            {
                sweepers.Add(left);
            }

            return sweepers;
        }
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _logger = GameLoggerResolver.ResolveRequired<HandContactSweeperController>();

        // Run one priority step ahead of the hosted sweepers (default 0) so exclusion re-supplies land in
        // the same tick, before the next sweep (see the class remarks on regeneration re-supply).
        ProcessPhysicsPriority = -1;

        _logger.LogDebug("Hand contact sweeper controller '{ControllerName}' ready.", Name);
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        SyncHostedSweeperProcessing();

        MaintainSweepers();
    }

    private void SyncHostedSweeperProcessing()
    {
        if (Enabled == _hostedSweepersProcessingEnabled)
        {
            return;
        }

        _hostedSweepersProcessingEnabled = Enabled;
        foreach (ContactSource sweeper in HandSweepers)
        {
            // Pass-through: a disabled controller stops its hosted sweepers mid-flight; the receiver
            // monitor ends the affected pairs on its next tick because nothing observes them again.
            sweeper.SetPhysicsProcess(Enabled);
        }

        _logger?.LogDebug(
            "Hand sweeper controller '{ControllerName}' {SweepingState} its hosted sweepers.",
            Name,
            Enabled ? "resumed" : "stopped");
    }

    private void MaintainSweepers()
    {
        if (!Enabled)
        {
            return;
        }

        DynamicPhysicalRig? rig = PhysicalRig;
        if (rig is null || !IsInstanceValid(rig))
        {
            return;
        }

        _rightHandSweeper ??= TryCreateHandSweeper(
            rig,
            RightHandIKTarget,
            RightHandBoneName,
            RightLowerArmBoneName,
            "RightHandContactSource",
            RightHandDisplayName);
        _leftHandSweeper ??= TryCreateHandSweeper(
            rig,
            LeftHandIKTarget,
            LeftHandBoneName,
            LeftLowerArmBoneName,
            "LeftHandContactSource",
            LeftHandDisplayName);

        _rightHandSweeper?.Maintain();
        _leftHandSweeper?.Maintain();
    }

    private HandSweeper? TryCreateHandSweeper(
        DynamicPhysicalRig rig,
        AnimatableBody3D? handTarget,
        StringName handBoneName,
        StringName lowerArmBoneName,
        string sweeperName,
        string handDisplayName)
    {
        if (handTarget is null || !IsInstanceValid(handTarget))
        {
            return null;
        }

        // The exclusion set resolves from generated proxies, so the sweepers wait for the rig's first
        // generation before they exist — the wiring-order contract keeps any pre-wiring tick from
        // delivering a pair that regeneration topology excludes.
        if (rig.GeneratedProxyCount == 0)
        {
            return null;
        }

        BodyColliderProfile? colliderProfile = rig.ColliderProfile;
        if (colliderProfile is null)
        {
            if (!_warnedMissingColliderProfile)
            {
                _warnedMissingColliderProfile = true;
                _logger?.LogWarning(
                    "Hand sweeper '{SweeperName}' found no collider profile on rig '{RigName}'; the hand " +
                    "will not be swept.",
                    sweeperName,
                    rig.Name);
            }

            return null;
        }

        ContactSource sweeper = new()
        {
            Name = sweeperName,
            PartyDisplayName = handDisplayName,
            OwnerName = ResolveOwnerName(),
            // The tracked body carries the sweep geometry: the sweeper's query ShapeCasts are parented to
            // the hand target, so the profile shapes ride the hand wherever the IK drives it.
            TrackedBody = handTarget,
            QueryShapes = BuildProfileQueryShapes(colliderProfile, handBoneName),
            // Assigned in the initialiser's order, before the sweeper enters the tree below: the sweeper's
            // first sweep already honours the RIG-001 self-pair topology (wiring-order contract).
            ExcludedReceivers = CollectExcludedReceivers(rig, handBoneName, lowerArmBoneName),
        };

        sweeper.SetPhysicsProcess(_hostedSweepersProcessingEnabled);

        // Hosted under the controller, never under the hand target: the IK system activity-gates its hand
        // targets (a target with no provider influence is ProcessMode.Disabled with its whole subtree), and
        // a sweeper hosted there would silently stop sensing whenever the hand's IK goes inactive. Contact
        // sensing must track the hand through animation-driven rest just as through active IK.
        AddChild(sweeper);

        _logger?.LogDebug(
            "Hand sweeper '{SweeperName}' hosted with {QueryShapeCount} profile query shapes and " +
            "{ExcludedReceiverCount} excluded self-pair receivers, tracking hand target '{HandTargetName}'.",
            sweeperName,
            sweeper.QueryShapes.Length,
            sweeper.ExcludedReceivers.Length,
            handTarget.Name);

        return new HandSweeper(this, rig, sweeper, handBoneName, lowerArmBoneName);
    }

    private static ContactQueryShape[] BuildProfileQueryShapes(BodyColliderProfile colliderProfile, StringName handBoneName)
    {
        IReadOnlyList<BodyColliderShapeDescriptor> descriptors = colliderProfile.QueryShapeDescriptorsForBone(handBoneName);

        var queryShapes = new ContactQueryShape[descriptors.Count];
        for (int descriptorIndex = 0; descriptorIndex < descriptors.Count; descriptorIndex += 1)
        {
            BodyColliderShapeDescriptor descriptor = descriptors[descriptorIndex];
            queryShapes[descriptorIndex] = new ContactQueryShape
            {
                Shape = descriptor.Shape,
                Transform = descriptor.LocalTransform,
                Disabled = descriptor.Disabled,
            };
        }

        return queryShapes;
    }

    private static PhysicsBody3D[] CollectExcludedReceivers(
        DynamicPhysicalRig rig,
        StringName handBoneName,
        StringName lowerArmBoneName)
        => [
            .. rig.GetGeneratedProxyBodiesForBone(handBoneName),
            .. rig.GetGeneratedProxyBodiesForBone(lowerArmBoneName),
            .. rig.GetGeneratedFingerProxyBodiesForHand(handBoneName),
        ];

    /// <summary>
    /// Resolves the owner name for counterpart attribution: the exported name when authored, otherwise the
    /// nearest ancestor <see cref="CharacterHub" /> hub's identity, so template-installed characters
    /// attribute their contacts to themselves.
    /// </summary>
    private string ResolveOwnerName()
    {
        if (!string.IsNullOrWhiteSpace(OwnerName))
        {
            return OwnerName;
        }

        for (Node? node = GetParent(); node is not null; node = node.GetParent())
        {
            if (node is CharacterHub character && !string.IsNullOrWhiteSpace(character.Id))
            {
                return character.Id;
            }
        }

        return Name;
    }

    /// <summary>
    /// One hosted hand sweeper with its RIG-001 exception topology: re-supplies
    /// <see cref="ContactSource.ExcludedReceivers" /> whenever rig regeneration replaces the limb's proxies,
    /// comparing proxy instance ids so a rebuild is caught even at an unchanged proxy count. The per-tick
    /// comparison is allocation-free; only a detected change re-collects and re-supplies.
    /// </summary>
    private sealed class HandSweeper(
        HandContactSweeperController owner,
        DynamicPhysicalRig rig,
        ContactSource sweeper,
        StringName handBoneName,
        StringName lowerArmBoneName)
    {
        private ulong[] _excludedReceiverIds = CollectCurrentIds(rig, handBoneName, lowerArmBoneName);

        public ContactSource Sweeper { get; } = sweeper;

        public void Maintain()
        {
            if (!IsInstanceValid(Sweeper) || !IsInstanceValid(rig))
            {
                return;
            }

            if (CurrentIdsMatchCache())
            {
                return;
            }

            _excludedReceiverIds = CollectCurrentIds(rig, handBoneName, lowerArmBoneName);
            Sweeper.ExcludedReceivers = CollectExcludedReceivers(rig, handBoneName, lowerArmBoneName);
            owner._logger?.LogDebug(
                "Hand sweeper '{SweeperName}' re-supplied its excluded self-pair receivers after a rig " +
                "change: {ExcludedReceiverCount} receivers.",
                Sweeper.Name,
                Sweeper.ExcludedReceivers.Length);
        }

        /// <summary>Walks the limb's current proxies in cache order without allocating.</summary>
        private bool CurrentIdsMatchCache()
        {
            int idIndex = 0;
            return AppendIdsIfMatching(rig.GetGeneratedProxyBodiesForBone(handBoneName), ref idIndex)
                && AppendIdsIfMatching(rig.GetGeneratedProxyBodiesForBone(lowerArmBoneName), ref idIndex)
                && AppendIdsIfMatching(rig.GetGeneratedFingerProxyBodiesForHand(handBoneName), ref idIndex)
                && idIndex == _excludedReceiverIds.Length;
        }

        private bool AppendIdsIfMatching(IReadOnlyList<PhysicsBody3D> bodies, ref int idIndex)
        {
            for (int bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex += 1)
            {
                if (idIndex >= _excludedReceiverIds.Length
                    || _excludedReceiverIds[idIndex] != bodies[bodyIndex].GetInstanceId())
                {
                    return false;
                }

                idIndex += 1;
            }

            return true;
        }

        private static ulong[] CollectCurrentIds(DynamicPhysicalRig rig, StringName handBoneName, StringName lowerArmBoneName)
        {
            List<ulong> ids = [];
            CollectIds(rig.GetGeneratedProxyBodiesForBone(handBoneName), ids);
            CollectIds(rig.GetGeneratedProxyBodiesForBone(lowerArmBoneName), ids);
            CollectIds(rig.GetGeneratedFingerProxyBodiesForHand(handBoneName), ids);
            return [.. ids];
        }

        private static void CollectIds(IReadOnlyList<PhysicsBody3D> bodies, List<ulong> ids)
        {
            foreach (PhysicsBody3D body in bodies)
            {
                ids.Add(body.GetInstanceId());
            }
        }
    }
}
