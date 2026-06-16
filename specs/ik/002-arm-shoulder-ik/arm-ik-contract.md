---
title: Arm IK Contract
---

# Arm IK Contract

## Requirement

Define the arm-only contract for IK-002: `TwoBoneIK3D` arm solving, elbow pole-target prediction, and pose
independence constraints.

## Goal

Achieve natural, continuous arm IK solving across all reachable hand poses without visual
discontinuities or snapping, using resource-driven pole anchors.

## User Requirements

- UR-01: Elbow bend direction must appear natural across all key arm poses.
- UR-02: Small changes in hand position must produce smooth, corresponding changes in elbow
  direction without visible jumps or snaps.
- UR-03: Behaviour must remain pose-independent and consistent across upright and non-upright
  body orientations.
- UR-04: Resource-driven anchor configuration must support editor-based authoring and runtime
  loading without runtime mirroring toggles.

## Technical Requirements

- TR-01: Each arm uses a Godot `TwoBoneIK3D` node configured for the upper-arm → lower-arm →
  hand chain.
- TR-02: Shoulder correction executes in `ArmIKController` before IK solve via anatomical
  decomposition into elevation and protraction components in body-basis space.
- TR-03: Baseline pole direction derives from hand position relative to head across the unit sphere of
  arm directions in body basis.
- TR-04: Hand-rotation adjustment blends or offsets the baseline with hand-rotation contribution
  around the shoulder-to-hand axis.
- TR-05: Compression safeguard enforces minimum pole-offset distance for folded-arm poses via
  tunable gates and floors.
- TR-06: Body reference frame derives each frame from skeleton landmarks:
  - Up: Hips → Neck
  - Right: LeftShoulder → RightShoulder, orthonormalised against up
  - Forward: cross product of right and up
- TR-07: `ArmIKController` computes shoulder correction and pole-target positions in
  `_ProcessModificationWithDelta` before `TwoBoneIK3D` nodes.
- TR-08: Resource-driven pole anchors load from `ArmPoleAnchorSetResource` assets authored via
  editor bake workflow.

### Pole-Target Prediction Continuity

- TR-09: Baseline pole-direction function must be continuous (C0) across the unit sphere of arm
  directions in body basis.
- TR-10: No hard-threshold fallback branches. Output at branch boundaries must not differ
  materially from nominal branch output at the switching threshold.
- TR-11: No `abs()`-style midline reflections using absolute value to map across the body midline.
- TR-12: Degenerate case handling must smoothly bias toward an alternative direction when pole
  direction is near-parallel to arm direction.
- TR-13: Transition bands must distribute change smoothly across relevant arm-direction ranges.

### Compression Safeguard

- TR-14: Tunables: pole offset ratio, pole offset minimum, compression threshold, compression margin.
- TR-15: Per frame: compute base offset from arm length, determine folded gate from body-local
  vertical relation, enforce compressed floor when compression ratio <= threshold and folded
  gate is true.
- TR-16: Safeguard only constrains pole-target placement distance. It must not alter hand-target
  inputs, shoulder correction, hand-rotation correction, or `TwoBoneIK3D` chain membership.

## In Scope

- One `TwoBoneIK3D` chain per arm (left and right), solving shoulder-to-hand.
- Pole-target prediction from head and hand targets.
- Guarded elbow pole-offset compression safeguard with tunable gates and floors.
- Shoulder correction execution before IK solve.
- Baseline pose mapping plus hand-rotation adjustment.
- Resource-driven anchor configuration via `ArmPoleAnchorSetResource` assets.
- Behaviour consistent across upright and non-upright body orientations.
- Six key hand poses: Arms Lowered, Arms Raised Forward, Arms Raised Straight Overhead,
  Arms Raised To Each Side, Hands Behind The Head, Hands Covering The Chest.

## Out Of Scope

- Shoulder correction algorithmic details (see Shoulder Correction Contract).
- Hand-rotation adjustment algorithmic details (see Hand-Rotation Elbow Correction Contract).
- Inverse Kinematics solving beyond arm chains.
- Full-body IK coordination.
- Player input handling for hand targets.

## Acceptance Criteria

- AC-01: Arm-only `TwoBoneIK3D` chain solving shoulder-to-hand.
- AC-02: Elbow pole-target prediction keeping bend direction natural across key poses.
- AC-03: Baseline pole direction from hand position relative to head.
- AC-04: Hand-rotation adjustment on baseline.
- AC-05: Compression safeguard for folded-arm poses.
- AC-17: Pose-independence across upright and non-upright orientations.
- AC-18: C0 continuity for baseline pole direction.
- AC-19: No hard-threshold branches at continuity boundaries.
- AC-20: No `abs()`-style midline reflections.
- AC-21: Smooth degenerate case handling.
- AC-22: Distributed transition bands.
- AC-23: Six key hand poses with designated baseline pole directions.
- AC-24: Baseline approximates key pose values with continuous interpolation elsewhere.
- AC-25: Resource-driven anchor configuration via `ArmPoleAnchorSetResource`.
- AC-26: Normalised anchor representation with ReachRatio.
- AC-27: Editor bake workflow for anchor authoring.
- AC-28: Explicit ownership mapping between authoring root and output resource.
- AC-29: Symmetry without runtime mirror toggle.
- AC-30: Visual verification with resource assets.
- AC-31: C# integration tests for resource loading.

## References

- [IK-002 Overview](index.md)
- [Shoulder Correction Contract](shoulder-adjustment-contract.md)
- [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)