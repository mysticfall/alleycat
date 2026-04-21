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
whose knee pole direction is derived from the associated foot orientation, aligned with IK-002 naming, design, and test
conventions.

## User Requirements

1. Players must see stable, natural lower-body posing during common standing and crouch-adjacent poses.
2. Feet must follow authored foot target goals without visual snapping or instability.
3. Knee bend direction must remain visually coherent with foot orientation as feet rotate.

## Technical Requirements

1. Implementation must provide per-leg `TwoBoneIK3D` solving with per-leg pole-target driving.
2. Knee pole prediction must derive from the associated foot direction using a forward/up axis blend weighted by the
   dot product between foot forward and the current leg direction vector.
3. Runtime structure, naming, and validation flow must remain consistent with IK-002 conventions (controller-first,
   contract-first, photobooth + integration-test validation).
4. Foot target transforms must be treated as runtime inputs only and must not be mutated by `LegIKController`,
   `TwoBoneIK3D`, or other IK-003 runtime controller logic.
5. Verification must use the lower-body photobooth basis scene and include a hips-override harness implemented via
   `BoneAttachment3D` override bone position without animation.
6. In compressed crouch-like leg states, knee pole offset must enforce a minimum floor using rest leg length:
   `max(MinimumPoleOffset, (RestLegLength * 0.5) + RestLegHalfPoleOffsetMargin)`, with compression gating and margin
   exposed as tunable parameters.

## Specification Structure

This page is the source-of-truth overview for IK-003. Detailed contracts are split into focused pages:

- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- [Leg-Feet IK Test Setup Contract](test-setup-contract.md)

Use this page for scope and acceptance traceability, then use contract pages for implementation detail.

## In Scope

- Per-leg `TwoBoneIK3D` lower-limb solving (upper leg → lower leg → foot) for left and right sides.
- A per-leg controller that computes knee pole-target positions before IK solve.
- Foot-direction-driven knee pole logic using forward/up axis interpolation driven by the foot-forward-to-leg-direction
  dot product.
- Read-only foot target solving where runtime IK logic consumes targets as goals without mutating target transforms.
- Compressed-state knee pole minimum-offset safeguarding using a rest-leg-length-derived floor.
- A reusable leg-feet IK scene and a lower-body photobooth verification workflow aligned to IK-002 structure.

## Out Of Scope

- Full-body IK orchestration beyond lower limbs.
- Hip locomotion synthesis, gait generation, or animation state-machine design.
- Footstep planning, terrain probing, and physics-based ground adaptation.
- Retargeting across unrelated skeleton topologies.
- Subjective animation polish beyond objective checks defined by this spec.
- Resolution of the separate leg-up regression outside the crouch/compressed knee-pole safeguard contract.

## Context

### Simplicity Relative To IK-002

IK-003 intentionally remains simpler than IK-002:

- Feet remain primarily animation-driven.
- IK solves towards provided foot targets and does not rewrite foot target transforms.
- Knee pole direction uses local foot-orientation cues only (no shoulder/head-equivalent predictor layer).

### Reference Character And Test Basis

The implementation and validation workflow must use:

- Reference character scene: `@game/assets/characters/reference/female/reference_female.tscn`
- Lower-body photobooth basis scene:
  `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn`

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
| AC-03 | Knee pole direction selection uses foot forward and foot up axes, with interpolation driven by the dot product between foot forward and the current leg direction vector. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-04 | The interpolation is smooth and deterministic across continuous foot rotation changes, with no discontinuous knee flips in required test poses. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-05 | Per-leg IK solve consumes provided foot target transforms as goal inputs each runtime update, without requiring an additional runtime target-clamp contract. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-06 | Naming, node-role conventions, and modifier-order design are consistent with IK-002 patterns (controller-first, contract split, per-side instances). | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-07 | IK configuration is saved as a reusable scene with a clear path (to be determined during implementation), with consuming scenes responsible for binding runtime targets. | This Page |
| AC-08 | A photobooth verification scene exists under `@game/tests/` and inherits `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn`, following `@specs/testing/002-visual-verification-scope/index.md`. | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-09 | The verification setup provides a `BoneAttachment3D`-based hips harness that can override hips bone position without animation playback. | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-10 | Visual checks confirm natural knee and foot behaviour without obvious inversion, discontinuous knee-plane changes, or over-correction in required poses. | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-11 | A C# integration test loads the same verification scene and validates non-visual assertions (for example pole-direction continuity, read-only foot-target input behaviour, and stable hips-override response). | [Leg-Feet IK Test Setup Contract](test-setup-contract.md) |
| AC-12 | During IK-003 runtime updates, provided foot target transforms remain unmodified by solver/controller logic and are consumed as read-only goals for solving. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |
| AC-13 | In compressed crouch-like leg states, knee pole offset enforces `max(MinimumPoleOffset, (RestLegLength * 0.5) + RestLegHalfPoleOffsetMargin)` as a minimum floor, with compression gating and margin remaining tunable. | [Leg-Feet IK Contract](leg-feet-ik-contract.md) |

## References

- @game/assets/characters/reference/female/reference_female.tscn
- @game/assets/testing/photobooth/templates/lower_body_5_cams.tscn
- @specs/characters/ik/002-arm-shoulder-ik/index.md
- @specs/characters/ik/003-leg-feet-ik/leg-feet-ik-contract.md
- @specs/characters/ik/003-leg-feet-ik/test-setup-contract.md
- @specs/testing/002-visual-verification-scope/index.md
