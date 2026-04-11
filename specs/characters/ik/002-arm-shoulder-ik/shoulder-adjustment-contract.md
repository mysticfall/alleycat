---
title: Shoulder Correction Contract
---

# Shoulder Correction Contract

## Purpose

Define the shoulder-correction contract for IK-002: pre-IK correction behaviour integrated into `ArmIkController`,
parameterisation, ordering, and testability requirements.

## Contract Scope

- Shoulder correction inside `ArmIkController` before arm IK solve.
- Corrective rotation from rest-to-current shoulder→elbow look-at delta in body space.
- Exported parameter behaviour for per-instance tuning.
- Shoulder-specific testability and deterministic behaviour requirements.

## Shoulder Correction Placement

`ArmIkController` is the execution point for shoulder correction. Per arm, it applies corrective shoulder rotation, then
updates pole-target prediction for the downstream `TwoBoneIK3D` solve.

The correction exists to:

- Prevent visible deformation at the shoulder joint.
- Preserve natural silhouette in high-elevation and behind-body poses.

## Correction Mechanism

Correction compares current shoulder→elbow direction against rest shoulder→elbow direction in body space. The
computation is performed by `ShoulderCorrectionComputer` and consumed by `ArmIkController`.

1. **Rest Look-At Caching**: cache a rest look-at basis from rest shoulder→elbow direction in body space.
2. **Current Look-At Build**: build a current look-at basis from estimated current shoulder→elbow direction in body
   space.
3. **Delta Rotation Computation**: compute rest-to-current delta rotation from the two look-at bases.
4. **Damped Delta Computation**: damp the delta by slerping towards identity using adaptive weighting.
5. **Shoulder Basis Application**: apply the damped delta to the shoulder rest basis.

Correction must be deterministic: identical input pose yields identical shoulder rotation.

## Exported Parameters

| Parameter | Type | Default | Description |
| --------- | ---- | ------- | ----------- |
| `Side` | `ArmSide` | — | Arm instance to process (same enum used by `ArmIkController`). |
| `ShoulderWeight` | `float` | 0.55 | Overall dampening strength applied to shoulder correction delta. |
| `ElevationWeight` | `float` | 0.55 | Base adaptive contribution used when deriving correction weight from arm elevation context. |

These parameters are exported on `ArmIkController` and must be configurable per instance.

`Side` must be set before the node enters the scene tree. Runtime arm-side switching is not required.

## Placement And Ordering

`ArmIkController` extends `SkeletonModifier3D` and must be a direct child of `Skeleton3D`.

Shoulder correction must run within each controller before the arm `TwoBoneIK3D` nodes. Required sequence:

1. `ArmIkController` (left)
2. `ArmIkController` (right)
3. `TwoBoneIK3D` (left)
4. `TwoBoneIK3D` (right)
5. `CopyTransformModifier3D`

Each `ArmIkController` instance processes one side only, based on `Side`.

## Testability Requirement

`ShoulderCorrectionComputer` must exist as a static helper with no Godot scene-tree dependencies.

It provides pure math helpers for:

- Look-at basis construction from shoulder→elbow direction in body space.
- Damped delta computation (rest-to-current delta slerped towards identity).
- Adaptive correction-weight computation.

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
