---
id: BODY-003
title: Full-Body Collision Decision
---

# Full-Body Collision Decision

> **Child spec of** [BODY-002: Character Physical Response System](../002-character-physical-response/index.md)

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
4. Authored body colliders should be reusable for non-BODY consumers, such as
   future IK queries, without coupling those consumers to BODY runtime nodes.

## Technical Requirements

1. `BodyColliderProfile` shall be a `[Tool]`, editor-visible Godot `Resource`
   that references a configurable collider source `PackedScene` and exposes
   query descriptors for authored shapes by source bone/name.
2. `BodyColliderProfile` descriptors shall expose the original `Shape3D`
   resource and immutable authoring metadata for read/query consumers. They
   must not duplicate `Shape3D` resources as part of query generation.
3. `DynamicPhysicalRig` shall be a `[Tool]` runtime component that generates
   proxy collision bodies from a required `BodyColliderProfile`.
4. For each source `CollisionShape3D`, the implementation shall resolve the
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
5. Generated target attachments shall be `BoneAttachment3D` nodes as children
   of the target `Skeleton3D`. Generated proxy bodies shall be top-level
   `AnimatableBody3D` nodes associated with the generated attachments.
   A local proxy transform shall be derived from the source authoring
   attachment/body/shape hierarchy, NOT from the target skeleton's current pose.
   This preserves authored collider offset/rotation without cancelling
   scene-authored target bone pose overrides (for example a mirror-room NPC
   pose override). Generated shape transforms are identity in the current
   topology; proxy generation may duplicate `Shape3D` resources only if needed
   for runtime-body safety, while the profile query contract remains
   non-duplicating.
6. Every physics frame, manual sync shall set:
   `ProxyBody.GlobalTransform = Attachment.GlobalTransform * LocalProxyTransform`
   so proxies follow animated/posed bones at runtime while preserving authored
   offsets/rotations.
7. The rig shall clear and rebuild deterministically on each initialisation.
8. Reference IK target nodes for the head, hands, and feet shall not carry
   primitive anatomical `CollisionShape3D` children. Anatomical body collision
   shape data belongs in `BodyColliderProfile` and generated proxy bodies.
9. Adjacent-bone bidirectional collision exceptions shall be applied between
   each generated body and its parent bone's generated body to prevent
   self-collision.
10. Collision layers shall be configured with proxy bodies on layer 4 with mask
   11.
11. Hand followers shall remain `AnimatableBody3D` nodes driven from
    `PlayerVRIK._PhysicsProcess`; they must tolerate targets that have no direct
    primitive collision-shape children. When authored primitive hand-target
    shapes are absent, IK shall generate runtime `CollisionShape3D` children on
    the hand target from shared `BodyColliderProfile` hand descriptors before
    using `AnimatableBody3D.TestMove` or `MoveAndCollide`.
12. Hand-to-dynamic-body interaction shall use explicit capped channels through
    `HandDynamicBodyInteractionController`. When hand targets have no direct
    primitive collision-shape children, the controller shall use query shapes
   derived from the shared collider profile hand descriptors:
    - Impact channel: capped impulse when contact begins during sufficiently
      fast approach.
    - Sustained channel: capped continuous force while contact persists and
      the user continues pressing.
13. Dynamic bodies eligible for explicit hand interaction must have collision
   layer 2 and belong to group `hand_dynamic_interaction_body`.
14. The hand interaction controller shall be configurable with strength-style
   parameters (thresholds, gains, and caps) rather than prop-specific rules.
15. Automated test coverage shall verify rig generation, hand follower setup,
   collision-layer contract, and explicit hand-to-dynamic-body force transfer.
16. Collision responses shall not override or conflict with physics-based
    head/hand target driving.
17. Runtime hand-target movement collision shapes generated for IK shall reuse
    descriptor `Shape3D` resources and descriptor local transforms. They are
    runtime/generated nodes, not authored primitive target shapes, and dependency
    direction remains IK → shared body collider profile/resource with no BODY → IK
    dependency.

## In Scope

- Full-body proxy rig generation from source collider scene.
- Shared body collider profile queries for authored collision shapes.
- Adjacent-bone collision exception handling.
- Primitive-free IK target nodes for head, hand, and foot targets.
- Hand followers as `AnimatableBody3D` nodes that tolerate missing direct target
  shapes and receive runtime profile-backed movement collision shapes where
  needed.
- Explicit capped impact and sustained push interaction with dynamic rigid
  bodies.
- Configurable strength parameters for both interaction channels.
- Collision-layer contract for generated proxies and dynamic interaction bodies.
- Manual runtime sync of top-level proxy bodies to animated/posed bones.
- IK consumers (e.g. `PlayerVRIK`) consuming `BodyColliderProfile` hand descriptors
  for profile-backed query and runtime movement collision shapes, with dependency
  direction IK → shared profile and no BODY → IK coupling.
- Automated verification of core contracts.

## Out Of Scope

- Head reactive collision handling.
- Arm-reactive obstacle handling where arm colliders catch on physical barriers
  while hand targets drive through.
- Collision/locomotion coupling and locomotion responsiveness under collision
  response.
- IK limb-level retargeting or pose correction driven by generated proxies.
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
4. `BodyColliderProfile` discovers all `CollisionShape3D` nodes from the
   configured source scene and exposes descriptors by source bone/name.
5. `BodyColliderProfile` descriptors expose the original `Shape3D` resources
   without duplicating them for query consumers.
6. `DynamicPhysicalRig` fails fast with a clear setup error when no
   `BodyColliderProfile` is configured.
7. For each source shape, the implementation walks to the closest ancestor
   `BoneAttachment3D`. If no `BoneAttachment3D` ancestor exists, the rig
   fails fast during initialisation with a clear source-scene setup error.
8. If `BoneName` is empty or does not resolve against the target skeleton,
   the implementation logs a warning once, increments the skipped-shape count,
   skips that shape, and continues processing remaining shapes.
9. Generated attachments are `BoneAttachment3D` nodes under the target
   `Skeleton3D`. Generated proxy bodies are top-level `AnimatableBody3D`
   nodes associated with the generated attachments.
10. Local proxy transform is derived from the source authoring attachment/body/
   shape hierarchy and preserved at runtime; it does NOT cancel scene-authored
   target bone pose overrides.
11. Every physics frame, proxies update via manual sync:
   `ProxyBody.GlobalTransform = Attachment.GlobalTransform * LocalProxyTransform`.
12. Tests verify that proxies move with animated/posed bones at runtime,
     and that NPC/scene-authored pose overrides are not cancelled by the sync.
13. Generated proxy transforms preserve authored collider pose in skeleton-local
     space; rotated colliders are not flattened to identity. Tests verify a
     rotated source collider produces correctly rotated target collision shape.
14. The rig clears and regenerates deterministically on each initialisation.
15. Reference head, hand, and foot IK targets have no direct primitive
     `CollisionShape3D` children; their anatomical collision shape data is
     sourced from `BodyColliderProfile` and generated proxies.
16. Adjacent-bone collision exceptions are applied bidirectionally.
17. Collision layers are correctly configured for generated proxies:
     layer 4/mask 11.
18. Hand followers use `AnimatableBody3D` with physics-timed position updates
      from `PlayerVRIK._PhysicsProcess`, preserving target driving and tolerating
      the absence of direct primitive authored target shapes.
19. Hand-to-dynamic-body interaction uses explicit capped impact and sustained
      push channels, with profile-backed query shapes when direct primitive hand
      target shapes are absent.
20. Dynamic bodies receive explicit hand interaction only when they have
     collision layer 2 and belong to group `hand_dynamic_interaction_body`.
21. Impact and sustained channels are governed by configurable parameters.
22. Head collision remains out of scope; no requirement mandates head collision
     response that could conflict with head target driving.
23. Automated tests verify the core contracts for rig generation, hand follower
      setup, collision layers, and explicit force transfer.
24. Automated tests verify that a shapeless authored hand target gains runtime
      profile-backed movement collision shapes that reuse descriptor `Shape3D`
      resources/transforms and make `TestMove`/`MoveAndCollide` collide with
      body/proxy obstacles rather than passing through.

## References

- [BODY-002: Character Physical Response System](../002-character-physical-response/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](../../characters/ik/004-vrik-pose-state-machine/index.md)
- [DynamicPhysicalRig Implementation](@game/src/Body/DynamicPhysicalRig.cs)
- [BodyColliderProfile Implementation](@game/src/Body/BodyColliderProfile.cs)
- [HandDynamicBodyInteractionController Implementation](@game/src/IK/HandDynamicBodyInteractionController.cs)
- [IKTargetAnimatableFollower Implementation](@game/src/IK/IKTargetAnimatableFollower.cs)
- [PlayerVRIK Implementation](@game/src/IK/PlayerVRIK.cs)
- [DynamicPhysicalRig Integration Tests](@integration-tests/src/Body/DynamicPhysicalRigIntegrationTests.cs)
- [Hand Tests](@integration-tests/src/IK/HandDynamicBodyInteractionControllerIntegrationTests.cs)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
