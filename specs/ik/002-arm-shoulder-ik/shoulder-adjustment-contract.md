---
title: Shoulder Correction Contract
---

# Shoulder Correction Contract

## Requirement

Define the shoulder-correction contract for IK-002: pre-IK correction behaviour,
parameterisation, ordering, and testability requirements.

## Goal

Prevent visible deformation at the shoulder joint, preserve natural silhouette
in high-elevation and behind-body poses, and enable raised-overhead poses to
exceed T-pose shoulder lift while preserving the T-pose baseline.

## User Requirements

- Shoulder correction must prevent visible deformation during arm movement.
- Natural silhouette must be preserved in high-elevation and behind-body poses.
- Raised-overhead poses must exceed T-pose shoulder lift while preserving the
  T-pose baseline as the neutral reference.
- Forward-reached arm poses must not appear over-corrected.

## Technical Requirements

### Execution Point

`ArmIKController` is the execution point for shoulder correction. Each instance
applies corrective shoulder rotation before the downstream `TwoBoneIK3D` solve.

### Anatomical Decomposition

Shoulder orientation must be decomposed into elevation and protraction components
using body-basis reference, not rest-relative delta:

- **Elevation**: upward rotation from anatomical neutral, measured in the shoulder
  plane.
- **Protraction**: forward reach rotation from anatomical neutral, measured
  orthogonal to elevation.

### Side-Aware Sign Application

Signed contributions must be applied based on arm side:

- Left arm uses standard sign orientation.
- Right arm mirrors elevation and protraction signs to maintain symmetric
  anatomical behaviour.

### Placement and Ordering

`ArmIKController` must extend `SkeletonModifier3D` and be a direct child of
`Skeleton3D`.

Shoulder correction must run within each controller before arm `TwoBoneIK3D`
nodes. Required sequence:

1. `ArmIKController` (left)
2. `ArmIKController` (right)
3. `TwoBoneIK3D` (left)
4. `TwoBoneIK3D` (right)
5. `CopyTransformModifier3D`

Each `ArmIKController` instance processes one side only, based on the `Side`
parameter.

### Testability Requirement

`ShoulderCorrectionComputer` must exist as a static helper with no Godot scene-tree
dependencies. It provides pure math helpers for body-basis derivation, shoulder
plane decomposition, signed component application, damping computation, boost
computation, and clamped angle application.

Dedicated C# unit tests in `@tests/src/IK/` must validate known input/output pairs.

### Parameter Export Requirements

All configurable parameters must be exported on `ArmIKController` and be
configurable per instance in the Godot editor. The `Side` parameter must be set
before the node enters the scene tree.

## In Scope

- Shoulder correction inside `ArmIKController` before arm IK solve.
- Anatomical decomposition into elevation and protraction components.
- Side-aware signed contribution with configurable sign conventions.
- Forward elevation damping for forward-reached poses.
- Overhead elevation boost for raised-overhead poses.
- Parameter export for per-instance tuning.
- Shoulder-specific testability and deterministic behaviour.

## Out Of Scope

- Exact threshold numbers or final tuning constants (deferred to implementation
  tuning).
- Runtime arm-side switching after scene tree entry (not required).
- Character-specific parameter adjustment (deferred to character setup).

## Acceptance Criteria

This contract defines details for:

- AC-06, AC-11, AC-12, AC-13, AC-14, AC-15, AC-16

Source-of-truth criteria wording is maintained in [IK-002 Overview](index.md#acceptance-criteria).

## References

- [IK-002 Overview](index.md)
- [Arm IK Contract](arm-ik-contract.md)