---
id: IK-005
title: Full-Body Collision Decision
---

# Full-Body Collision Decision

## Requirement

The player character must maintain collision integrity with the world while
preserving VR tracking stability. Hands must not pass through the player's own
body, and explicit hand-to-dynamic-body interaction must function without
relying on uncapped engine-inferred pushing.

## Goal

Enable reliable per-bone collision for the player character through a generated
proxy rig, while providing controlled hand interaction with dynamic rigid bodies
through explicit force-transfer channels.

## User Requirements

1. Player hands must not pass through the player's own body during normal
   interaction.
2. Collision handling must not cause visible jitter or instability in VR
   tracking.
3. Hand-to-dynamic-body interaction must transfer force smoothly without
   excessive bouncing or clipping.

## Technical Requirements

1. `DynamicPhysicalRig` shall be a `[Tool]` runtime component that generates
   proxy collision bodies from a configurable source scene.
2. For each source `CollisionShape3D`, the implementation shall resolve the
   target skeleton bone as follows:
    a. Walk up the source scene hierarchy and take the closest ancestor
       `BoneAttachment3D` node.
    b. Read that ancestor's exported `BoneName` property; use that string as-is
       to resolve against the target skeleton.
    c. If no `BoneAttachment3D` ancestor exists, fail fast with a clear
       source-scene setup error.
    d. If `BoneName` is empty or does not resolve against the target skeleton,
       log a warning once, increment the skipped-shape count, skip that shape,
       and continue processing remaining shapes.
    e. Required setup errors (missing source scene, missing shape resource, or
       missing physics-body ancestor) still fail fast.
3. Generated target attachments shall be `BoneAttachment3D` nodes as children
   of the target `Skeleton3D`. Generated proxy bodies shall be top-level
   `AnimatableBody3D` nodes associated with the generated attachments.
   A local proxy transform shall be derived from the source authoring
   attachment/body/shape hierarchy, NOT from the target skeleton's current pose.
   This preserves authored collider offset/rotation without cancelling
   scene-authored target bone pose overrides (for example a mirror-room NPC
   pose override). The generated `CollisionShape3D` resources are duplicated;
   generated shape transforms are identity in the current topology.
4. Every physics frame, manual sync shall set:
   `ProxyBody.GlobalTransform = Attachment.GlobalTransform * LocalProxyTransform`
   so proxies follow animated/posed bones at runtime while preserving authored
   offsets/rotations.
5. The rig shall clear and rebuild deterministically on each initialisation.
6. Adjacent-bone bidirectional collision exceptions shall be applied between
   each generated body and its parent bone's generated body to prevent
   self-collision.
7. Collision layers shall be configured with proxy bodies on layer 4 with mask
   11.
8. Hand followers shall remain `AnimatableBody3D` nodes for world and self
   collision with layer 8 and mask 5.
9. Hand-to-dynamic-body interaction shall use explicit capped channels through
   `HandDynamicBodyInteractionController`:
    - Impact channel: capped impulse when contact begins during sufficiently
      fast approach.
    - Sustained channel: capped continuous force while contact persists and
      the user continues pressing.
10. Dynamic bodies eligible for explicit hand interaction must have collision
    layer 2 and belong to group `hand_dynamic_interaction_body`.
11. The hand interaction controller shall be configurable with strength-style
    parameters (thresholds, gains, and caps) rather than prop-specific rules.
12. Automated test coverage shall verify rig generation, hand follower setup,
    collision-layer contract, and explicit hand-to-dynamic-body force transfer.
13. Collision responses shall not override or conflict with physics-based
    head/hand target driving.

## In Scope

- Full-body proxy rig generation from source collider scene.
- Adjacent-bone collision exception handling.
- Hand follower world/self collision as `AnimatableBody3D`.
- Explicit capped impact and sustained push interaction with dynamic rigid
  bodies.
- Configurable strength parameters for both interaction channels.
- Collision-layer contract for proxies, hands, and dynamic interaction bodies.
- Manual runtime sync of top-level proxy bodies to animated/posed bones.
- Automated verification of core contracts.

## Out Of Scope

- Head reactive collision handling.
- Arm-reactive obstacle handling where arm colliders catch on physical barriers
  while hand targets drive through.
- Collision/locomotion coupling and locomotion responsiveness under collision
  response.
- Full-body external object interaction beyond hands (torso/legs contact with
  chains, carried items, and similar dynamics).
- Held objects collision handling (distinct from hand contact with dynamic
  bodies).

## Acceptance Criteria

1. During gameplay, player hands do not clip through their own torso or other
   body parts.
2. No visible jitter or instability in VR tracking is caused by collision
   responses.
3. Dynamic rigid-body hand interaction transfers force smoothly without
   excessive bouncing.
4. `DynamicPhysicalRig` discovers all `CollisionShape3D` nodes from the
   configured source scene.
5. For each source shape, the implementation walks to the closest ancestor
   `BoneAttachment3D`. If no `BoneAttachment3D` ancestor exists, the rig
   fails fast during initialisation with a clear source-scene setup error.
6. If `BoneName` is empty or does not resolve against the target skeleton,
   the implementation logs a warning once, increments the skipped-shape count,
   skips that shape, and continues processing remaining shapes.
7. Generated attachments are `BoneAttachment3D` nodes under the target
   `Skeleton3D`. Generated proxy bodies are top-level `AnimatableBody3D`
   nodes associated with the generated attachments.
8. Local proxy transform is derived from the source authoring attachment/body/
   shape hierarchy and preserved at runtime; it does NOT cancel scene-authored
   target bone pose overrides.
9. Every physics frame, proxies update via manual sync:
   `ProxyBody.GlobalTransform = Attachment.GlobalTransform * LocalProxyTransform`.
10. Tests verify that proxies move with animated/posed bones at runtime,
    and that NPC/scene-authored pose overrides are not cancelled by the sync.
11. Generated proxy transforms preserve authored collider pose in skeleton-local
    space; rotated colliders are not flattened to identity. Tests verify a
    rotated source collider produces correctly rotated target collision shape.
12. The rig clears and regenerates deterministically on each initialisation.
13. Adjacent-bone collision exceptions are applied bidirectionally.
14. Collision layers are correctly configured: proxies layer 4/mask 11, hands
    layer 8/mask 5.
15. Hand-to-dynamic-body interaction uses explicit capped impact and sustained
    push channels.
16. Dynamic bodies receive explicit hand interaction only when they have
    collision layer 2 and belong to group `hand_dynamic_interaction_body`.
17. Impact and sustained channels are governed by configurable parameters.
18. Hand followers use `AnimatableBody3D` with physics-timed position updates
    from `PlayerVRIK._PhysicsProcess`, preserving target driving while
    maintaining collision.
19. Head collision remains out of scope; no requirement mandates head collision
    response that could conflict with head target driving.
20. Automated tests verify the core contracts for rig generation, hand follower
    setup, collision layers, and explicit force transfer.

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
- [DynamicPhysicalRig Implementation](@game/src/Body/DynamicPhysicalRig.cs)
- [HandDynamicBodyInteractionController Implementation](@game/src/IK/HandDynamicBodyInteractionController.cs)
- [IKTargetAnimatableFollower Implementation](@game/src/IK/IKTargetAnimatableFollower.cs)
- [PlayerVRIK Implementation](@game/src/IK/PlayerVRIK.cs)
- [DynamicPhysicalRig Integration Tests](../../../integration-tests/src/Body/DynamicPhysicalRigIntegrationTests.cs)
- [Hand Tests](../../../integration-tests/src/IK/HandDynamicBodyInteractionControllerIntegrationTests.cs)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)