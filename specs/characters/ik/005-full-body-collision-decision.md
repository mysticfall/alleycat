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
   nearest ancestor whose concrete type is exactly `Node3D`; its name must
   directly match the runtime skeleton bone name with no fallback remapping,
   and fail fast if unresolved.
3. Proxy bodies shall be generated as `AnimatableBody3D` nodes with duplicated
   collision shapes attached to corresponding skeleton bones via
   `BoneAttachment3D`.
4. The rig shall clear and rebuild deterministically on each initialisation.
5. Adjacent-bone bidirectional collision exceptions shall be applied between
   each generated body and its parent bone's generated body to prevent
   self-collision.
6. Collision layers shall be configured with proxy bodies on layer 4 with mask
   11.
7. Hand followers shall remain `AnimatableBody3D` nodes for world and self
   collision with layer 8 and mask 5.
8. Hand-to-dynamic-body interaction shall use explicit capped channels through
   `HandDynamicBodyInteractionController`:
   - Impact channel: capped impulse when contact begins during sufficiently
     fast approach.
   - Sustained channel: capped continuous force while contact persists and
     the user continues pressing.
9. Dynamic bodies eligible for explicit hand interaction must have collision
   layer 2 and belong to group `hand_dynamic_interaction_body`.
10. The hand interaction controller shall be configurable with strength-style
    parameters (thresholds, gains, and caps) rather than prop-specific rules.
11. Automated test coverage shall verify rig generation, hand follower setup,
    collision-layer contract, and explicit hand-to-dynamic-body force transfer.
12. Collision responses shall not override or conflict with physics-based
    head/hand target driving.

## In Scope

- Full-body proxy rig generation from source collider scene.
- Adjacent-bone collision exception handling.
- Hand follower world/self collision as `AnimatableBody3D`.
- Explicit capped impact and sustained push interaction with dynamic rigid
  bodies.
- Configurable strength parameters for both interaction channels.
- Collision-layer contract for proxies, hands, and dynamic interaction bodies.
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
5. Source bone mapping uses the nearest exact `Node3D` ancestor name directly
   as the runtime skeleton bone name with no fallback.
6. Generated proxy bodies are `AnimatableBody3D` nodes attached to skeleton
   bones via `BoneAttachment3D`.
7. The rig clears and regenerates deterministically on each initialisation.
8. Adjacent-bone collision exceptions are applied bidirectionally.
9. Collision layers are correctly configured: proxies layer 4/mask 11, hands
   layer 8/mask 5.
10. Hand-to-dynamic-body interaction uses explicit capped impact and sustained
    push channels.
11. Dynamic bodies receive explicit hand interaction only when they have
    collision layer 2 and belong to group `hand_dynamic_interaction_body`.
12. Impact and sustained channels are governed by configurable parameters.
13. Hand followers use `AnimatableBody3D` with physics-timed position updates
    from `PlayerVRIK._PhysicsProcess`, preserving target driving while
    maintaining collision.
14. Head collision remains out of scope; no requirement mandates head collision
    response that could conflict with head target driving.
15. Automated tests verify the core contracts for rig generation, hand follower
    setup, collision layers, and explicit force transfer.

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
- [DynamicPhysicalRig Implementation](../../../game/src/IK/DynamicPhysicalRig.cs)
- [HandDynamicBodyInteractionController Implementation](../../../game/src/IK/HandDynamicBodyInteractionController.cs)
- [IKTargetAnimatableFollower Implementation](../../../game/src/IK/IKTargetAnimatableFollower.cs)
- [PlayerVRIK Implementation](../../../game/src/IK/PlayerVRIK.cs)
- [DynamicPhysicalRig Integration Tests](../../../integration-tests/src/IK/DynamicPhysicalRigIntegrationTests.cs)
- [HandDynamicBodyInteraction Integration Tests](../../../integration-tests/src/IK/HandDynamicBodyInteractionControllerIntegrationTests.cs)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)