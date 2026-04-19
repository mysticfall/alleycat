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
- Guarded elbow pole-offset compression safeguard with tunable gates and floors.
- Shoulder correction execution in `ArmIKController` before IK solve using anatomical decomposition into elevation and
  protraction components in body-basis space (algorithm details in shoulder contract).
- Baseline pose mapping plus hand-rotation adjustment (detailed in the [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)).
- Behaviour consistent across upright and non-upright body orientations.

## Mechanism

Each arm uses a Godot `TwoBoneIK3D` node configured for the upper-arm → lower-arm → hand chain.

The key algorithmic requirement is elbow pole-target prediction that keeps bend direction natural across key poses.

## Pole-Target Prediction

Prediction is composed of two layers:

1. **Baseline Pole Direction** from hand position relative to head.
2. **Hand-Rotation Adjustment** applied on top of the baseline.

### Compression Safeguard For Pole Offset

Pole-target placement must include a guarded distance floor for compressed folded-arm poses.

#### Required Tunables

- **Pole Offset Ratio**: scales current arm length into a base pole offset.
- **Pole Offset Minimum**: hard minimum floor for baseline pole offset.
- **Compression Threshold**: maximum clamped compression ratio that enables compressed-floor enforcement.
- **Compression Margin**: additive margin applied on top of half rest-arm length for compressed floor enforcement.

Exact numeric values are intentionally tunable.

#### Required Computation And Gates

Per arm, each frame:

1. **Apply shoulder correction** (via `ApplyShoulderCorrectionPreIK`) to obtain the post-correction shoulder position.
2. Compute `currentArmLength = distance(hand, postCorrectionShoulder)`.
3. Compute `baseOffset = max(currentArmLength * poleOffsetRatio, poleOffsetMinimum)`.
4. Compute `compressionRatio = clamp(currentArmLength / restArmLength, 0.0, 1.0)`.
5. Determine folded gate from body-local vertical relation using the **post-correction shoulder position**: folded gate is true when `hand Y <= shoulder Y` in body basis (equivalently `(hand - shoulder) · bodyUp <= 0`).
6. Activate compressed-floor enforcement only when:
   - `compressionRatio <= compressionThreshold`, and
   - folded gate is true.
7. When active, compute `compressedFloor = max(poleOffsetMinimum, restArmLength * 0.5 + compressionMargin)` and
   enforce `finalOffset >= compressedFloor`.
8. When inactive, `finalOffset = baseOffset`.

#### Non-Invasive Boundary

The safeguard only constrains pole-target placement distance. It must not alter:

- hand-target position inputs,
- shoulder-correction computation path,
- hand-rotation correction logic, or
- `TwoBoneIK3D` chain membership and solve targets.

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

The hand-rotation adjustment uses the runtime-authored hand pose rotation (from XR hand target driving) around the
shoulder-to-hand axis, compared against an interpolated reference rotation from key pose markers, to rotate the elbow
pole target and shift the bend-plane direction. Full algorithmic details, parameterisation, and acceptance criteria are defined in the
[Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md).

## Implementation Notes

### TwoBoneIK3D Configuration

Set all three bone names explicitly:

- `root_bone_name` (upper arm)
- `middle_bone_name` (lower arm)
- `end_bone_name` (hand)

Missing any required bone name (especially `middle_bone_name`) can produce no visible solver effect without error.

### ArmIKController

`ArmIKController` extends `SkeletonModifier3D` and is decorated with `[GlobalClass]`.

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
- AC-20
- AC-21
- AC-22
- AC-23

Source-of-truth criteria wording is maintained in [IK-002 Overview](index.md#acceptance-criteria).

## References

- [IK-002 Overview](index.md)
- [Shoulder Correction Contract](shoulder-adjustment-contract.md)
- [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)
