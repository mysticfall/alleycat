---
title: Shoulder Correction Contract
---

# Shoulder Correction Contract

## Purpose

Define the shoulder-correction contract for IK-002: pre-IK correction behaviour integrated into `ArmIKController`,
parameterisation, ordering, and testability requirements.

## Contract Scope

- Shoulder correction inside `ArmIKController` before arm IK solve.
- Anatomical decomposition into elevation and protraction components using pose/body-basis reference.
- Side-aware signed contribution with configurable sign conventions.
- Exported parameter behaviour for per-instance tuning.
- Shoulder-specific testability and deterministic behaviour requirements.

## Shoulder Correction Placement

`ArmIKController` is the execution point for shoulder correction. Per arm, it applies corrective shoulder rotation, then
updates pole-target prediction for the downstream `TwoBoneIK3D` solve.

The correction exists to:

- Prevent visible deformation at the shoulder joint.
- Preserve natural silhouette in high-elevation and behind-body poses.
- Enable raised-overhead poses to exceed T-pose shoulder lift while preserving T-pose baseline.

## Correction Mechanism

Correction uses anatomical decomposition in body-basis space rather than rest-relative delta. The computation is performed by
`ShoulderCorrectionComputer` and consumed by `ArmIKController`.

### Anatomical Decomposition

1. **Body Basis Derivation**: Derive body-local basis each frame from skeleton landmarks:
   - **Up**: `Hips` → `Neck` direction in world space.
   - **Right**: `LeftShoulder` → `RightShoulder` direction, orthonormalised against up.
   - **Forward**: cross product of right and up.

2. **Shoulder Plane Decomposition**: Decompose shoulder orientation into two anatomical components in body basis:
   - **Elevation**: upward rotation from anatomical neutral (arms-at-sides), measured in the shoulder plane.
   - **Protraction**: forward reach rotation from anatomical neutral, measured orthogonal to elevation.

3. **Side-Aware Sign Application**: Apply signed contributions based on arm side:
   - Left arm: elevation and protraction use standard sign orientation.
   - Right arm: elevation and protraction signs are mirrored to maintain symmetric anatomical behaviour.

### Forward Elevation Damping

Elevated arm poses in front of the body (forward reach) receive reduced correction weight to preserve
natural player experience. This damping prevents over-correction during active reaching poses.

### Overhead Elevation Boost

Raised-overhead poses can exceed the T-pose shoulder lift baseline through an additive boost. This enables
full overhead reach while preserving the T-pose baseline as the neutral reference point.

## Exported Parameters

| Parameter | Type | Default | Description |
| --------- | ---- | ------- | ----------- |
| `Side` | `ArmSide` | — | Arm instance to process (same enum used by `ArmIKController`). |
| `ShoulderWeight` | `float` | 0.2 | Overall strength applied to shoulder correction component blend. |
| `AnatomicalNeutralLateralBias` | `float` | 0.15 | Lateral offset added to anatomical neutral pose in body basis. |
| `MaxElevationAngleDegrees` | `float` | 160 | Maximum elevation angle from neutral in degrees. |
| `MaxProtractionAngleDegrees` | `float` | 20 | Maximum protraction angle from neutral in degrees. |
| `ForwardElevationDamping` | `float` | 0.3 | Multiplier applied to elevation when arm is forward-reached. |
| `MaxOverheadElevationBoostDegrees` | `float` | 120 | Additional elevation boost for overhead poses in degrees. |

> **Note:** Default values reflect initial implementation tuning. Values may require character-specific adjustment and should be
> treated as illustrative rather than normative. All parameters are exported on `ArmIKController` and must be configurable per
> instance in the Godot editor.

`Side` must be set before the node enters the scene tree. Runtime arm-side switching is not required.

## Placement And Ordering

`ArmIKController` extends `SkeletonModifier3D` and must be a direct child of `Skeleton3D`.

Shoulder correction must run within each controller before the arm `TwoBoneIK3D` nodes. Required sequence:

1. `ArmIKController` (left)
2. `ArmIKController` (right)
3. `TwoBoneIK3D` (left)
4. `TwoBoneIK3D` (right)
5. `CopyTransformModifier3D`

Each `ArmIKController` instance processes one side only, based on `Side`.

## Testability Requirement

`ShoulderCorrectionComputer` must exist as a static helper with no Godot scene-tree dependencies.

It provides pure math helpers for:

- Body-basis derivation from skeleton landmarks.
- Shoulder plane decomposition into elevation and protraction components.
- Signed component application based on arm side.
- Forward elevation damping computation.
- Overhead elevation boost computation.
- Clamped component angle application using MaxElevationAngleDegrees and MaxProtractionAngleDegrees.

Dedicated C# unit tests in `@tests/src/IK/` must validate known input/output pairs.

## Acceptance Criteria Coverage

This contract defines details for:

- AC-06
- AC-11
- AC-12
- AC-13
- AC-14
- AC-15
- AC-16

Source-of-truth criteria wording is maintained in [IK-002 Overview](index.md#acceptance-criteria).

## References

- [IK-002 Overview](index.md)
- [Arm IK Contract](arm-ik-contract.md)