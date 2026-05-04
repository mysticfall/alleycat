---
id: IK-005
title: Full-Body Collision Decision Memo (Temp)
---

# Full-Body Collision Decision Memo

**Status**: Temporary — may be superseded by a formal specification.

## Purpose

Capture the current exploration direction for full-body collision handling in the player character, pending formal
specification. This memo records the problem space, constraints, architectural options considered, and a phased proof-of-
concept plan.

## Problem Statement

The player character requires accurate per-major-bone collision for movement, including:

- Locomotion-driven body positioning must respect collision geometry.
- The player's own hands and held objects must not pass through their own body.
- External dynamic objects (chains, ropes, carried items) must interact naturally with body parts.

Current PlayerVRIK lacks explicit self-collision handling beyond temporary exceptions.

## Current Project Constraints

1. **Physics-based head/hand targets**: PlayerVRIK drives head and hand targets through physics-based interpolation,
   making direct collision-driven positioning complex.
2. **Animation-driven feet**: Feet remain the animation-driven source of truth, creating hybrid control where upper body
   follows physics targets while lower body follows animation.
3. **Collision/locomotion coupling unspecified**: The interaction between collision responses and locomotion state is not
   yet defined.
4. **Existing temporary self-collision exceptions**: Current PlayerVRIK implements temporary self-collision bypasses that
   will need replacement.

## Architecture Options Considered

### Option A: Full Per-Bone Proxy Colliders

- Assign a collider to each major bone in the skeleton.
- Pros: Maximum fidelity, direct collision feedback.
- Cons: High computational cost, complex tuning, potential instability with physics-driven targets.

### Option B: Hybrid Root Body + Selective Region Proxies (Preferred Direction)

- Keep the root/body capsule or concave collider as the primary collision volume.
- Add selective region proxies for high-priority areas (hands, forearms, torso front).
- Use collision layers to allow external dynamic objects to interact while preventing self-intersection.
- Pros: Balanced fidelity and performance, clearer collision geometry, easier to iterate.
- Cons: Requires defining which regions need proxies and tuning their shapes.

### Option C: Collision-Free Body with Interactive Push-Out

- Disable body collision entirely and use push-out forces when hands approach body.
- Pros: Simple to implement.
- Cons: No body collision for external objects, push-out can feel unnatural.

**Decision**: Option B is the current preferred direction.

## Phased Proof-Of-Concept Plan

### Phase 1: Upper-Body Proxy Colliders

1. Identify high-priority regions (hands, forearms, upper torso front).
2. Create proxy collider nodes attached to the appropriate bones.
3. Configure collision layers to prevent self-intersection while allowing external objects.
4. Validate in-game that hands no longer pass through torso during normal play.

### Phase 2: External Object Interaction

1. Test dynamic objects (chains, thrown items) interacting with body colliders.
2. Verify natural behaviour without excessive bouncing or clipping.
3. Adjust proxy shapes and collision responses as needed.

### Phase 3: Locomotion Integration

1. Define how collision state feeds into locomotion decision-making.
2. Validate that movement respects collision feedback (for example, reduced speed when crowded).
3. Iterate on collision/locomotion coupling based on in-game feel.

### Phase 4: Full-Body Coverage (Future)

- Expand proxy coverage to full body if Phases 1-3 prove the approach viable.
- This phase is contingent on earlier proof-of-concept success.

## User Requirements

1. Player hands and held objects must not pass through the player's own body during normal interaction.
2. External dynamic objects (chains, carried items) must interact naturally with body parts.
3. Collision handling must not cause visible jitter or instability in VR tracking.
4. Locomotion must remain responsive and not be unduly blocked by collision responses.

## Technical Requirements

1. The implementation must use proxy colliders on selected bones rather than per-bone colliders on every skeletal node.
2. Collision layers must be configured to separate self-collision prevention from external object interaction.
3. Proxy colliders must be attached to bone-attached nodes and follow the skeleton correctly.
4. Collision responses must not override or conflict with physics-based head/hand target driving.
5. The implementation must be validated in-game with real locomotion scenarios, not just isolated collision tests.
6. The collision/locomotion coupling contract must be defined before Phase 3 begins.

## Acceptance Criteria

1. **UR-1**: During gameplay, the player's hands do not clip through their own torso or other body parts.
2. **UR-2**: Chains or similar external dynamic objects contact and interact with the body naturally without excessive
   bouncing.
3. **TR-1**: Upper-body proxy colliders are implemented on at least hands, forearms, and torso.
4. **TR-2**: Collision layer configuration prevents self-intersection while allowing external object interaction.
5. **TR-3**: No visible jitter or instability in VR tracking caused by collision responses.
6. **TR-4**: A collision/locomotion coupling contract is defined before Phase 3 proceeds.

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
- [IK Implementation Notes](implementation-notes.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)