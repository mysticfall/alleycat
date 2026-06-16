---
id: ik-002-hand-rotation-correction
title: Hand-Rotation Elbow Correction Contract
---

# Hand-Rotation Elbow Correction Contract

## Requirement

Deliver a hand-rotation-driven elbow-correction layer for IK-002: per-frame adjustments to the elbow pole-target
position using the angular difference between actual and interpolated reference hand rotations, applied after the
baseline pole-target prediction.

## Goal

Allow players to shift the elbow bend-plane direction by rotating their hand controllers, without altering the
elbow bend angle. The correction must be smooth, deterministic, and gracefully degenerate when the hand is near the
shoulder.

## User Requirements

1. Rotating a hand controller around its longitudinal axis shifts the elbow bend-plane direction on the same arm.
2. The elbow bend angle is unaffected by the hand-rotation correction.
3. The correction is disabled when `HandRotationWeight` is set to zero.

## Technical Requirements

1. The correction is applied per-frame after the baseline pole direction is computed.
2. The rotation axis is the normalised shoulder-to-hand direction vector (body basis), shared with the solver chain.
3. Reference hand rotations are interpolated from six key pose markers (Marker3D nodes under HandTargetPoses in the test
   scene) using inverse-distance weighting in body-basis space; interpolation must be smooth and well-defined when
   equidistant from multiple reference positions.
4. The angular difference is the signed scalar angle between actual and interpolated hand rotations decomposed around
   the dynamic axis.
5. The pole target is rotated around the dynamic axis at the closest point on that axis to the current pole target,
   scaled by `HandRotationWeight`.
6. `HandRotationWeight` (float, default 1.0) is exported on `ArmIKController` and configurable per instance.
7. When the hand is near the shoulder (near-zero axis length), the correction gracefully degenerates — for example, by
   reducing weight proportionally or skipping the correction entirely.

## In Scope

- Per-frame correction layered on the baseline pole-target prediction.
- Body-basis reference rotation interpolation from six key pose markers.
- Signed angular difference decomposition around the shoulder-to-hand axis.
- Pivot-based pole-target rotation at the closest point on the axis.
- Exported `HandRotationWeight` parameter on `ArmIKController`.

## Out Of Scope

- Baseline pole-target computation (handled by the Arm IK Contract).
- Shoulder correction contracts.
- Non-deterministic or discontinuous correction behaviour.

## Acceptance Criteria

1. AC-17: Hand-rotation correction rotates the elbow pole target around the shoulder-to-hand axis, pivoting at the
   closest point on that axis to the current pole target, with rotation magnitude determined by the signed angular
   difference between actual and reference hand rotations scaled by `HandRotationWeight`.
2. AC-18: Reference hand rotations are interpolated from key pose markers using inverse-distance weighting in
   body-basis space, producing smooth and continuous neutral rotations across all hand positions.
3. AC-19: `HandRotationWeight` is exported on `ArmIKController` and is configurable per instance in the Godot
   editor.

Source-of-truth criteria wording is maintained in [IK-002 Overview](index.md#acceptance-criteria).

## References

- [IK-002 Overview](index.md)
- [Arm IK Contract](arm-ik-contract.md)
- [Shoulder Correction Contract](shoulder-adjustment-contract.md)
- @game/tests/ik/arm_shoulder_ik_test.gd
- @game/tests/ik/arm_shoulder_ik_test.tscn
