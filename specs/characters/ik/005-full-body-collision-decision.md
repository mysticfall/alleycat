---
id: IK-005
title: Full-Body Collision Decision Memo (Temp)
---

# Full-Body Collision Decision Memo

**Status**: Temporary — may be superseded by a formal specification.

## Purpose

Capture the current experimental direction for full-body collision handling in the player character, pending formal
specification. This memo records the problem space, constraints, architectural options considered, and the current
proof-of-concept implementation.

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

### Option B: Hybrid Root Body + Selective Region Proxies

- Keep the root/body capsule or concave collider as the primary collision volume.
- Add selective region proxies for high-priority areas (hands, forearms, torso front).
- Use collision layers to allow external dynamic objects to interact while preventing self-intersection.
- Pros: Balanced fidelity and performance, clearer collision geometry, easier to iterate.
- Cons: Requires defining which regions need proxies and tuning their shapes.

### Option C: Collision-Free Body with Interactive Push-Out

- Disable body collision entirely and use push-out forces when hands approach body.
- Pros: Simple to implement.
- Cons: No body collision for external objects, push-out can feel unnatural.

### Option D: Dynamic Generated Rig (Experimental Baseline)

- Use a runtime `[Tool]` component (`DynamicPhysicalRig`) that loads an external collider scene.
- Discover `CollisionShape3D` nodes in the source scene and derive source bone IDs from the nearest ancestor
  whose concrete type is exactly `Node3D`.
- Require the nearest exact `Node3D` ancestor name to already be the runtime skeleton bone name; no fallback
  remapping is part of the contract.
- Generate proxy rigid bodies with duplicated collision shapes attached to the corresponding skeleton bones.
- Clear and regenerate deterministically on each initialisation to ensure consistent state.
- Apply adjacent-bone collision exceptions to prevent unwanted self-collision between directly connected bones.
- Configure collision layers: proxies on layer 4 with mask 11, hands on layer 8 with mask 5, and explicit hand-interaction dynamic bodies on layer 2.
- Pros: Full-body coverage, deterministic regeneration, simpler direct-name contract.
- Cons: Requires the source collider scene to stay author-authored against runtime bone names.

**Decision**: Option D is the experimental baseline. The implementation scope remains minimal, focusing on
generating the full-body rig and verifying basic collision behavior through manual in-game testing.

## Prioritisation Framework

This section clarifies the distinction between three layers:

1. **Experimental Baseline** (already present in branch) — full-body rig generation
2. **Current Active Subphase** — hand-only tuning and interaction (narrower scope on top of baseline)
3. **Deferred Work** (intentionally not pursued) — broader full-body interaction work

### 1. Experimental Baseline (Full-Body Rig Generation)

The branch already contains the broader `DynamicPhysicalRig` implementation from earlier experimental passes:

- Full-body proxy collider generation for all major bones.
- Collision layer configuration: proxies on layer 4 with mask 11, hands on layer 8 with mask 5, dynamic interaction bodies on layer 2.
- Adjacent-bone collision exceptions applied.

This baseline provides the foundation. It exists and is validated as a complete unit. No further expansion is pursued until the active subphase is resolved.

### 2. Current Active Subphase (Hand-Only Collision And Dynamic Interaction)

- **Scope**: Hand followers only.
- **Goal**: Keep smooth `AnimatableBody3D` hand world/self collision while moving dynamic rigid-body interaction onto an explicit, hand-only capped force-transfer path.
- **Status**: Hand-only `AnimatableBody3D` path is **accepted** for world/self collision (smoothness, oscillation, and clipping verified). Hand-only dynamic-body interaction path (capped impact + sustained push) is **implemented** and under tuning.
- **In Scope Now**: Hand-only dynamic-body interaction with representative dynamic objects — for example testing with `EndWeight` or chain test assets to verify impact/sustained channels. This is the current active work.
- **Current Tuning Issue**: `EndWeight` (dynamic-body strength) feels too light — needs adjustment for more reasonable heavy-body feel.
- **Next immediate step**: Tune hand dynamic-body strength down/up for appropriate heavy-body resistance before expanding scope.
- **Does NOT expand scope to**:
  - Head collision work.
  - Broader arm/body physical interaction beyond hands.
  - Full-body reactive behaviour.
  - Broader external object interaction beyond hand-only testing (e.g., full-body chain interaction, carried items with torso/legs).
- The baseline already contains full-body rig generation, but the current active work operates strictly within hand followers.
- Hand followers remain the world/self collision authority.
- Dynamic rigid bodies are handled through a dedicated hand interaction channel rather than relying on uncapped engine-inferred `AnimatableBody3D` pushing for correctness.
- **Arm-Reactive Design Consideration**: The hand IK target may pass through a physical bar or obstacle while the arm collider (forearm/upper arm proxy) catches on it. This scenario must be captured as a design/verification concern when entering the arm-reactive phase — current hand-only implementation handles target position directly, so verification should confirm arm segments respond appropriately to obstacle contact.

### 3. Deferred Work

The following items are intentionally deferred pending hand-tuning and arm-reactive phase completion:

- **Head Collision/Oscillation Work**: Deferred until hand-only contract is fully stable and arm-reactive phase is resolved.
- **Full-Body Reactive Behaviour**: Deferred until arm-reactive work is complete and verified.
- **Broader External Object Interaction** (beyond hand-only): Full-body chain interaction, carried items contacting torso/legs, and similar dynamics involving body parts other than hands — deferred.
- **Collision/Locomotion Coupling**: Contract undefined; deferred pending stability.
- **Note**: Hand-only dynamic-body interaction is **now in scope** as the active subphase. Only the broader (full-body) external object work remains deferred.

## Current Implementation Status

### Phase 1: Dynamic Physical Rig Generation (Implemented)

The following contract is now implemented:

- `DynamicPhysicalRig` is a `[Tool]` runtime/editor component.
- Loads `res://assets/characters/reference/female/colliders.tscn` as the source collider scene.
- Traverses the source scene to find all `CollisionShape3D` nodes.
- For each shape, derives the source bone ID by traversing upward to find the nearest ancestor whose concrete type
  is exactly `Node3D`.
- Uses that exact `Node3D` ancestor name directly as the runtime skeleton bone name.
- Generates proxy rigid bodies by duplicating the source collision shapes.
- Attaches proxy bodies to the corresponding skeleton bones.
- Clears and regenerates deterministically on each initialisation.
- Applies adjacent-bone collision exceptions to prevent self-collision between directly connected bones.

### Phase 2: Collision Layer Contract (Implemented)

- Proxy bodies: layer 4, mask 11.
- Player hands: layer 8, mask 5 for physical world/self collision only.
- Dynamic rigid bodies intended for explicit hand interaction use layer 2.
- This configuration preserves world/self hand collision while separating dynamic rigid-body force transfer onto an explicit hand-controlled path.

### Phase 3: Hand-Only Dynamic Rigid-Body Interaction (Implemented)

- Hands retain the `AnimatableBody3D` follower path for world/self collision and smoothing.
- Hands query a dedicated dynamic-body interaction layer and apply explicit capped transfer through two channels:
  - **Impact channel**: capped impulse when contact begins during sufficiently fast approach.
  - **Sustained channel**: capped continuous force while contact persists and the user continues pressing/pushing.
- Both channels are governed by configurable hand-strength-style parameters (thresholds, gains, and caps) rather than per-prop tuning rules.
- Head interaction remains deferred.

### Planned Sequence

1. **First**: Keep the current `AnimatableBody3D` hand followers for world/self collision. **(Accepted)**
2. **Second**: Route dynamic rigid-body hand contact through explicit capped impact and sustained push channels. **(Implemented, under tuning)**
3. **Third**: Tune `EndWeight` (dynamic-body strength) for more reasonable heavy-body feel. **(Next immediate)**
4. **Fourth**: Expand scope to make arms reactive — arm segments respond to physical contact with world geometry while hand targets drive through. **(Pending, includes arm-through-bar verification)**
5. **Fifth**: Broader body collision work (head, full-body reactive behaviour) resumes only after arm-reactive phase is stable, subject to reprioritisation.

## User Requirements

1. Player hands and held objects must not pass through the player's own body during normal interaction.
2. External dynamic objects (chains, carried items) must interact naturally with body parts.
3. Collision handling must not cause visible jitter or instability in VR tracking.
4. Locomotion must remain responsive and not be unduly blocked by collision responses.

**Note**: Item 2 (dynamic object interaction) is partially in scope — hand-only dynamic-body interaction is actively being tested/tuned. Broader external object interaction (full-body chain interaction, carried items with torso/legs) remains deferred. Items 3–4 remain requirements for future phases.

## Technical Requirements

1. The implementation must use a `[Tool]` runtime component (`DynamicPhysicalRig`) that generates proxy colliders
   from an external source scene.
2. The nearest exact `Node3D` ancestor name for each source `CollisionShape3D` must already match the runtime
   skeleton bone name directly; fallback remapping is not part of the contract.
3. Proxy bodies must be generated by duplicating source collision shapes and attaching them to the corresponding
   skeleton bones.
4. The rig must clear and regenerate deterministically on each initialisation to ensure consistent state.
5. Adjacent-bone collision exceptions must be applied to prevent self-collision between directly connected bones.
6. Collision layers must be configured such that proxies are on layer 4 with mask 11, hands are on layer 8
   with mask 5, and dynamic rigid bodies intended for explicit hand interaction use layer 2.
7. Collision responses must not override or conflict with physics-based head/hand target driving.
8. Hand followers must preserve smooth `AnimatableBody3D` world/self collision while avoiding reliance on uncapped engine-inferred pushing against dynamic rigid bodies for correctness.
9. Dynamic rigid-body hand interaction must use an explicit hand-only two-channel model:
    - a capped impact impulse on contact start or sufficiently fast approach, and
    - a capped sustained force while contact persists and the user continues pressing/pushing.
10. These hand interaction channels must be governed by configurable strength-style parameters (thresholds, gains, and caps) rather than prop-specific rules.
11. Dynamic rigid bodies intended for this contract must use a dedicated interaction layer separate from world/static collision so the hand follower can preserve world/self collision authority.
12. Focused automated coverage is in scope for the hand-only explicit interaction contract.
13. The collision/locomotion coupling contract must be defined before further integration phases proceed.
14. The `EndWeight` (dynamic-body strength) parameter must be tuned to provide a more reasonable heavy-body feel — current implementation under review. **(Active Tuning)**
15. Arm-reactive behaviour must be implemented to make arm segments respond to physical contact while hand targets drive through. This includes handling the arm-through-bar scenario where the hand IK target passes a physical obstacle but the arm collider catches. **(Pending, Next Phase)**

**Note**: Technical Requirement 13 remains deferred beyond the current hand-only phase.

## Acceptance Criteria

### Active Validation (Current Subphase)

1. **UR-1**: During gameplay, the player's hands do not clip through their own torso or other body parts.
2. **UR-3**: No visible jitter or instability in VR tracking caused by collision responses.
3. **TR-1**: `DynamicPhysicalRig` component loads the source collider scene and discovers all `CollisionShape3D` nodes.
4. **TR-2**: Source bone IDs are derived from the nearest `Node3D` ancestor of each collision shape.
5. **TR-3**: The resolved nearest-exact-`Node3D` bone name is used directly as the runtime skeleton bone name, with no
    fallback remapping.
6. **TR-4**: Proxy rigid bodies are generated with duplicated collision shapes attached to the corresponding skeleton
    bones.
7. **TR-5**: The rig clears and regenerates deterministically on each initialisation.
8. **TR-6**: Adjacent-bone collision exceptions are applied to prevent self-collision between directly connected bones.
9. **TR-7**: Collision layers are configured correctly: proxies layer 4/mask 11, hands layer 8/mask 5, and dynamic
     rigid bodies participating in explicit hand interaction use layer 2.
10. **TR-8**: Dynamic rigid-body hand interaction is handled by explicit capped impulse/force transfer logic rather
     than relying solely on default raw `AnimatableBody3D` pushing.
11. **TR-9**: Configurable strength-style parameters exist for both the impact and sustained push channels.
12. **TR-10**: Focused automated coverage verifies the hand-only collision/dynamic interaction contract.
13. **TR-14**: `EndWeight` (dynamic-body strength) is tuned to provide reasonable heavy-body feel. **(In Progress — under review)**
14. **TR-15**: Arm-reactive behaviour verified — arm segments respond to physical contact; arm-through-bar scenario handled appropriately. **(Pending — arm-reactive phase)**

### Deferred Criteria (Future Phases)

13. **UR-2**: Chains or similar external dynamic objects contact and interact with the body naturally without excessive
       bouncing. (Deferred)
14. **UR-4**: Locomotion remains responsive and is not unduly blocked by collision responses. (Deferred)
15. Collision/locomotion coupling contract is defined. (Deferred)
16. Head collision/oscillation work. (Deferred — pending arm-reactive phase completion)
17. Full-body reactive behaviour beyond arms. (Deferred — pending arm-reactive phase completion)

**Note**: Arm-reactive work (TR-15) is now the immediate next step after hand tuning — moved from deferred to active next phase.

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
- [IK Implementation Notes](implementation-notes.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
