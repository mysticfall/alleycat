---
id: IK-003
title: Leg And Feet IK System
---

# Leg And Feet IK System

## Requirement

Provide a simplified lower-limb IK system that adjusts leg and foot posing for the player character while keeping feet
solved towards provided read-only foot target goals.

## Goal

Define a spec-first, testable contract for implementing and validating a `TwoBoneIK3D`-based leg-and-feet IK setup
whose knee pole direction is primarily derived from current animation geometry, with fallback to foot orientation when the
animation-derived direction is degenerate, aligned with IK-002 naming, design, and test conventions.

## User Requirements

1. Players must see stable, natural lower-body posing during common standing and crouch-adjacent poses.
2. Feet must follow authored foot target goals without visual snapping or instability.
3. Knee bend direction must remain visually coherent and deterministic as feet rotate, transitioning smoothly between animation-derived poses and foot-orientation-derived fallback.

## Technical Requirements

1. Implementation must provide per-leg `TwoBoneIK3D` solving with per-leg pole-target driving.
2. **Primary geometric method**: Knee pole direction must be derived from current animation geometry using the following computation:
   - Let `o1` = midpoint(upper leg origin, foot IK target position)
   - Let `o2` = midpoint(upper leg origin, current foot bone position)
   - Primary pole direction = normalise(lower leg origin - o2)
   - Line origin for pole-target placement = o2
3. **Fallback trigger and method**: When the animation-derived direction from o2 to lower leg origin is degenerate (too short or near-zero magnitude),
    the system must fall back to foot IK target orientation-based pole direction:
    - Fallback pole direction derives from foot IK target forward and up axes using the existing forward/up interpolation contract.
    - Fallback trigger threshold is implementation-defined and deterministic (internal constant, not tunable).
4. **Primary/fallback transition**: The transition between primary geometric method and fallback must be deterministic and smooth,
   with no discontinuous jumps in knee behaviour.
5. Runtime structure, naming, and validation flow must remain consistent with IK-002 conventions (controller-first,
    contract-first, photobooth + integration-test validation).
6. Foot target transforms must be treated as runtime inputs only and must not be mutated by `LegIKController`,
   `TwoBoneIK3D`, or other IK-003 runtime controller logic.
7. **Foot target synchronisation stage**: Before each leg IK solve cycle, IK targets must be re-synchronised from animated foot transforms
    to ensure the solver operates on current animation state. This sync stage runs once per tick, after the animation player produces its
    sampled pose but before the leg IK controller computes pole targets. The sync reads the current animated foot bone
    transforms (position + rotation) and copies them to the corresponding foot target nodes for downstream pole prediction and solve.
    This guarantees deterministic solve behaviour when animation timing or `TimeSeek` position changes.
8. **Ordering contract**: `FootTargetSyncController` must run before `HipReconciliationModifier` and any other foot-mutating modifiers.
    This ordering is a scene/pipeline authoring contract enforced through scene authoring. Runtime auto-reordering is intentionally not used.
9. Verification must use the lower-body photobooth basis scene and include a hips-override harness implemented via
    `BoneAttachment3D` override bone position without animation.
10. Knee pole offset must always enforce a minimum floor using rest leg half-length plus margin:
    `max(MinimumPoleOffset, (RestLegLength * 0.5) + RestLegHalfPoleOffsetMargin)`. This floor
    applies unconditionally and is not compression-gated.

## Specification Structure

This page is the source-of-truth overview for IK-003. Detailed contracts are split into focused pages:

- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- [Leg-Feet IK Test Setup Contract](test-setup-contract.md)

Use this page for scope and acceptance traceability, then use contract pages for implementation detail.

## In Scope

- Per-leg `TwoBoneIK3D` lower-limb solving (upper leg → lower leg → foot) for left and right sides.
- A per-leg controller that computes knee pole-target positions before IK solve.
- **Primary geometric method**: Animation-derived knee pole direction using current animation geometry:
  - Line origin (`o2`) = midpoint(upper leg origin, current foot bone position)
  - Primary pole direction = normalise(lower leg origin - o2)
- **Fallback method**: Foot orientation-derived pole direction when animation-derived direction is degenerate.
- Read-only foot target solving where runtime IK logic consumes targets as goals without mutating target transforms.
- **Foot target synchronisation stage**: Dedicated sync that re-synchronises foot IK targets from animated foot transforms at the start of each IK
   solve cycle, before pole-target computation. This guarantees the solver operates on current animation state.
- Unconditional knee pole minimum-offset safeguarding using a rest-leg-length-derived floor.
- A reusable leg-feet IK scene (`reference_female_ik.tscn`) and a lower-body photobooth verification workflow aligned to IK-002 structure.

## Out Of Scope

- Full-body IK orchestration beyond lower limbs.
- Hip locomotion synthesis, gait generation, or animation state-machine design.
- Footstep planning, terrain probing, and physics-based ground adaptation.
- Retargeting across unrelated skeleton topologies.
- Subjective animation polish beyond objective checks defined by this spec.
- Resolution of the separate leg-up regression outside the unconditional knee-pole safeguard contract.

## Context

### Simplicity Relative To IK-002

IK-003 intentionally remains simpler than IK-002:

- Feet remain primarily animation-driven.
- IK solves towards provided foot targets and does not rewrite foot target transforms.
- Knee pole direction is primarily derived from current animation geometry (`o2` line origin method), with fallback to foot orientation only when animation-derived direction is degenerate.

### Reference Character And Test Basis

The implementation and validation workflow must use:

- Reference character scene: `@game/assets/characters/reference/female/reference_female.tscn`
- Reference character IK scene: `@game/assets/characters/reference/female/reference_female_ik.tscn` (contains the leg IK setup)
- Lower-body photobooth basis scene:
  `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn`

The reusable leg IK setup is contained in `reference_female_ik.tscn`, which is instanced by `player.tscn`.

## Component Contracts

- **Leg-Feet IK Contract:** solver setup, knee-pole prediction, and controller ordering are defined in
  [Leg-Feet IK Contract](leg-feet-ik-contract.md).
- **Leg-Feet IK Test Setup Contract:** photobooth wiring, hips override harness, and validation flow are defined in
  [Leg-Feet IK Test Setup Contract](test-setup-contract.md).

## Acceptance Criteria

All criteria remain normative. IDs are provided for traceability to contract pages.

| ID    | Requirement | Primary Contract |
|-------|-------------|------------------|
| AC-00 | The specification defines both user-visible lower-limb behaviour outcomes and technical implementation contracts required for delivery and validation. | This Page |
| AC-01 | Each leg uses a `TwoBoneIK3D` node to solve upper-leg → lower-leg → foot towards its leg/foot target path. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-02 | A per-leg controller computes the knee pole-target each frame before downstream `TwoBoneIK3D` execution. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-03 | **Primary geometric method**: The controller derives knee pole direction from current animation geometry: `o2` = midpoint(upper leg origin, current foot bone position); primary pole direction = normalise(lower leg origin - `o2`). Line origin for placement uses `o2`. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-04 | **Fallback trigger and method**: When the animation-derived direction from `o2` to lower leg origin is degenerate (too short or near-zero magnitude), the system must fall back to foot IK target orientation-based pole direction. Fallback threshold is deterministic and implementation-defined. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-05 | The primary-to-fallback transition is smooth and deterministic with no discontinuous jumps in knee behaviour across required test poses. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-06 | Per-leg IK solve consumes provided foot target transforms as goal inputs each runtime update, without requiring an additional runtime target-clamp contract. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-06a | A dedicated foot-target sync controller (`FootTargetSyncController`) re-synchronises foot IK targets from animated foot transforms (position + rotation) at the beginning of each IK solve cycle, before pole-target computation and solve. This sync runs once per tick after animation sampling and guarantees the solver operates on current animation state. `FootTargetSyncController` must run before `HipReconciliationModifier` and any other foot-mutating modifiers — ordering is a scene/pipeline authoring contract. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-07 | Naming, node-role conventions, and modifier-order design are consistent with IK-002 patterns (controller-first, contract split, per-side instances). | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-08 | Reusable IK scene saved at `@game/assets/characters/reference/female/reference_female_ik.tscn` containing `LegIKController` (left/right), `TwoBoneIK3D` (left/right), and leg target nodes (`IKTargets/LeftFoot`, `IKTargets/RightFoot`). Consuming scene `@game/assets/characters/reference/player.tscn` inherits this scene and wires foot targets for runtime solving. | This Page |
| AC-09 | A photobooth verification scene exists under `@game/tests/` and inherits `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn`, following `@specs/testing/002-visual-verification-scope/index.md`. | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-10 | The verification setup provides a `BoneAttachment3D`-based hips harness that can override hips bone position without animation playback. | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-11 | Visual checks confirm natural knee and foot behaviour without obvious inversion, discontinuous knee-plane changes, or over-correction in required poses. This includes verification that the primary geometric method produces stable knee behaviour across test poses. | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-12 | A C# integration test loads the same verification scene and validates non-visual assertions: pole-direction continuity under continuous animation changes, read-only foot-target input behaviour, and stable hips-override response. | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-13 | During IK-003 runtime updates, provided foot target transforms remain unmodified by solver/controller logic and are consumed as read-only goals for solving. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-14 | Knee pole offset unconditionally enforces `max(MinimumPoleOffset, (RestLegLength * 0.5) + RestLegHalfPoleOffsetMargin)` as a minimum floor. This floor applies in all leg states and is not compression-gated. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |

## References

- @game/assets/characters/reference/female/reference_female.tscn
- @game/assets/characters/reference/female/reference_female_ik.tscn
- @game/assets/characters/reference/player.tscn
- @game/assets/testing/photobooth/templates/lower_body_5_cams.tscn
- @specs/characters/ik/002-arm-shoulder-ik/index.md
- @specs/characters/ik/003-leg-feet-ik/leg-feet-ik-contract.md
- @specs/characters/ik/003-leg-feet-ik/test-setup-contract.md
- @specs/testing/002-visual-verification-scope/index.md
