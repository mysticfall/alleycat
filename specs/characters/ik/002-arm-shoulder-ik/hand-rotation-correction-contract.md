---
title: Hand-Rotation Elbow Correction Contract
---

# Hand-Rotation Elbow Correction Contract

## Purpose

Define the hand-rotation-based elbow-correction contract for IK-002: a mechanism that uses the player's hand
controller rotation to apply small additional corrections to the elbow pole-target position, layered on top of the
baseline pole-target prediction.

This contract elaborates the "Hand-Rotation Adjustment" layer referenced in [Arm IK Contract](arm-ik-contract.md).

## Contract Scope

- Per-frame correction applied after the baseline pole direction is computed.
- Uses the rotation of the hand IK target (from VR controller) around a dynamically computed axis.
- Compares actual hand rotation against an interpolated reference rotation derived from key pose markers.
- Rotates the elbow pole target around the shoulder-to-hand axis to shift the elbow bend plane direction.
- Single configurable weight per arm, applied uniformly across all poses.
- Correction must be smooth, continuous, and deterministic.

## Mechanism

Each frame, for each arm:

1. **Dynamic Axis Computation**: Compute the normalised shoulder-to-hand direction vector. This is the axis around which
   both reference and actual hand rotations are measured, and around which the pole target is rotated.

2. **Reference Rotation Interpolation**: For each of the six key hand poses (matching those in the Arm IK Contract —
   Arms Lowered, Arms Raised Forward, Arms Raised Straight Overhead, Arms Raised To Each Side, Hands Behind The Head,
   Hands Covering The Chest):
    - Each key pose has an associated marker (`Marker3D`) that encodes both position and rotation in the scene. The
      marker rotation, transformed into body basis, defines the **reference hand rotation** for that pose.
    - The marker positions are defined in the **body basis** (body-local frame derived from skeleton landmarks as
      described in the Arm IK Contract).
    - Compute the distance from the current hand bone position (in body basis) to each reference marker position (in
      body basis).
    - Use inverse-distance weighting to blend the reference rotations into a single interpolated neutral rotation. The
      exact weighting scheme (for example inverse-distance-squared) is left to implementation, but the interpolation
      must
      be smooth and well-defined even when the hand is equidistant from multiple reference positions.

3. **Angular Difference Computation**: Decompose both the actual hand-target rotation and the interpolated reference
   rotation into scalar angles around the dynamic axis. Compute the signed angular difference.

4. **Pole-Target Rotation**: Rotate the current elbow pole-target position around the dynamic axis by the signed angular
   difference multiplied by the configurable weight. The pivot point for this rotation is the **closest point on the
   shoulder-to-hand line to the current pole-target position** (i.e. project the pole target onto the axis to find the
   pivot, then rotate the pole target around the axis at that pivot).

## Effect

The correction shifts the **elbow bend-plane direction** — the direction the elbow points — without changing the elbow
bend angle. This allows the player to move elbows closer together or further apart by rotating their hand controllers.

For example, in the "Hands Behind The Head" pose, rotating the hands around the global Y axis causes the elbows to
spread wider or tuck closer together.

## Exported Parameters

| Parameter            | Type    | Default | Description                                                                                                                            |
|----------------------|---------|---------|----------------------------------------------------------------------------------------------------------------------------------------|
| `HandRotationWeight` | `float` | 1.0     | Weight applied to the hand-rotation angular difference before rotating the pole target. A value of 0 disables the correction entirely. |

This parameter is exported on `ArmIkController` alongside the existing `Side`, `ShoulderWeight`, and `ElevationWeight`
parameters. It must be configurable per instance.

## Reference Rotation Source

The reference hand rotations are sourced from the `Marker3D` nodes under the `HandTargetPoses` node in the test scene
(`@game/tests/characters/ik/arm_shoulder_ik_test.tscn`). These markers encode both position and baseline rotation for
each key pose.

The reference data must be defined in the body basis to ensure pose-independence (the interpolation works correctly
regardless of overall body orientation, matching the Pose-Independence Requirement in the IK-002 overview).

The implementation may hardcode the reference marker transforms at authoring time;
the contract only requires that the interpolated neutral rotation be derived from these markers in body-basis space.

## Acceptance Criteria Coverage

This contract defines details for:

- AC-05

And adds the following new criteria (to be registered in index.md):

- AC-17: Hand-rotation correction rotates the elbow pole target around the shoulder-to-hand axis, pivoting at the
  closest point on that axis to the current pole target, with the rotation magnitude determined by the signed angular
  difference between actual and reference hand rotations scaled by `HandRotationWeight`.
- AC-18: Reference hand rotations are interpolated from key pose markers using inverse-distance weighting in body-basis
  space, producing smooth and continuous neutral rotations across all hand positions.
- AC-19: `HandRotationWeight` is exported on `ArmIkController` and is configurable per instance in the Godot editor.

Source-of-truth criteria wording is maintained in [IK-002 Overview](index.md#acceptance-criteria).

## Implementation Notes

- The dynamic axis (shoulder-to-hand direction) is the same direction used by the `TwoBoneIK3D` solver chain. Reuse
  this computation where possible.
- The pivot-point projection (closest point on axis to pole target) avoids introducing unintended lateral drift when
  rotating the pole target.
- When the hand is very close to the shoulder (near-zero axis length), the correction should gracefully degenerate —
  for example, by reducing correction weight proportionally to axis length or skipping the correction entirely.

## References

- [IK-002 Overview](index.md)
- [Arm IK Contract](arm-ik-contract.md)
- [Shoulder Correction Contract](shoulder-adjustment-contract.md)
- @game/tests/characters/ik/arm_shoulder_ik_test.gd
