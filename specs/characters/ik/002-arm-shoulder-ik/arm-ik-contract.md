---
title: Arm IK Contract
---

# Arm IK Contract

## Purpose

Define the arm-only contract for IK-002: `TwoBoneIK3D` arm solving, elbow pole-target prediction, and pose
independence constraints.

## Contract Scope

- One `TwoBoneIK3D` chain per arm (left and right), solving shoulder-to-hand.
- Pole-target prediction from head and hand targets.
- Shoulder correction execution in `ArmIkController` before IK solve using the look-at-delta method (algorithm details
  in shoulder contract).
- Baseline pose mapping plus hand-rotation adjustment (detailed in the [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)).
- Behaviour consistent across upright and non-upright body orientations.

## Mechanism

Each arm uses a Godot `TwoBoneIK3D` node configured for the upper-arm → lower-arm → hand chain.

The key algorithmic requirement is elbow pole-target prediction that keeps bend direction natural across key poses.

## Pole-Target Prediction

Prediction is composed of two layers:

1. **Baseline Pole Direction** from hand position relative to head.
2. **Hand-Rotation Adjustment** applied on top of the baseline.

### Key Hand Poses

When hand rotation matches the baseline below, baseline pole direction is laterally outward (away from body midline),
except where noted.

| Pose | Hand Position | Baseline Hand Rotation | Baseline Pole Direction |
| ---- | ------------- | ---------------------- | ----------------------- |
| Arms Lowered | Hands at sides | Palms facing each other | Laterally outward |
| Arms Raised Forward | Hands in front of body | Palms facing each other | Laterally outward |
| Arms Raised Straight Overhead | Hands above head | Palms facing forward | Laterally outward |
| Arms Raised To Each Side | Hands extended to sides | Palms facing downward | Posterior (toward the back) |
| Hands Behind The Head | Hands behind head | Palms facing forward | Laterally outward |
| Hands Covering The Chest | Hands in front of chest | Palms facing backward | Laterally outward (optional) |

The "Hands Covering The Chest" pose remains optional and may be deferred.

### Hand-Rotation Adjustment

Actual pole direction is derived by blending or offsetting the baseline with a hand-rotation contribution.

The hand-rotation adjustment uses the player's hand controller rotation around the shoulder-to-hand axis, compared
against an interpolated reference rotation from key pose markers, to rotate the elbow pole target and shift the
bend-plane direction. Full algorithmic details, parameterisation, and acceptance criteria are defined in the
[Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md).

## Implementation Notes

### TwoBoneIK3D Configuration

Set all three bone names explicitly:

- `root_bone_name` (upper arm)
- `middle_bone_name` (lower arm)
- `end_bone_name` (hand)

Missing any required bone name (especially `middle_bone_name`) can produce no visible solver effect without error.

### ArmIkController

`ArmIkController` extends `SkeletonModifier3D` and is decorated with `[GlobalClass]`.

It computes shoulder correction and pole-target positions in `_ProcessModificationWithDelta`, and must run before
`TwoBoneIK3D` nodes.

### Body Reference Frame

Derive body-local basis each frame from skeleton landmarks:

- **Up**: `Hips` → `Neck`
- **Right**: `LeftShoulder` → `RightShoulder`, orthonormalised against up
- **Forward**: cross product of right and up

This frame keeps behaviour pose-independent.

### Baseline Hand Rotation Encoding

Verification pose markers (`Marker3D`) encode target hand position and baseline hand rotation per pose.

`CopyTransformModifier3D` at the end of the stack copies hand-target rotation to hand bones so visual orientation matches
marker orientation.

### Phased Delivery

Initial phase may deliver positional baseline prediction first. Hand-rotation adjustment, now fully specified in the
[Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md), may be implemented in a subsequent
phase.

## Acceptance Criteria Coverage

This contract defines details for:

- AC-01
- AC-02
- AC-03
- AC-04
- AC-05
- AC-17
- AC-18
- AC-19

Source-of-truth criteria wording is maintained in [IK-002 Overview](index.md#acceptance-criteria).

## References

- [IK-002 Overview](index.md)
- [Shoulder Correction Contract](shoulder-adjustment-contract.md)
- [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)
