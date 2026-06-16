---
title: Leg-Feet IK Contract
---

# Leg-Feet IK Contract

## Requirement

Define the technical contract for leg `TwoBoneIK3D` solving with knee pole-target prediction.

## Goal

Per-leg IK solve delivering natural bending behaviour through animated-geometry pole derivation,
with stable handling of degenerate poses.

## User Requirements

1. **Animated-geometry pole direction**: Knee pole points using current animation geometry,
   producing natural bending.
2. **Degenerate-pose stability**: When leg geometry collapses or inverts, pole target persists
   from previous frame — no popping.
3. **Foot target read-only**: Foot target transforms are input goals, never mutated by runtime IK.
4. **Solve-to-target**: Each leg solve moves towards the foot target position and orientation.

## Technical Requirements

### Chain Topology

- One `TwoBoneIK3D` chain per leg (left, right), solving upper leg to foot.
- Per-leg controller (`LegIKController`) updates pole-target position before IK solve.

### Pole Direction Contract

- Derive knee pole direction from current animation geometry each frame.
- Use foot target position as reference point; ignore foot target rotation.
- When pole vector is degenerate (zero magnitude after normalisation attempts), do not update pole
  target for that frame — prior state persists.
- Enforce unconditional minimum pole offset floor from rest leg length.

### Foot Target Synchronisation

- `FootTargetSyncController` reads animated foot transforms from skeleton before any modifier
  mutates them.
- Copies transforms to foot target nodes as input for downstream pole computation and solve.
- Ordering contract (per tick): sync → hip reconciliation → pole computation → IK solve.
- Scene authoring enforces this ordering via node order under `Skeleton3D`.

### Foot Target Immutability

- Foot target transforms are read-only input goals for `TwoBoneIK3D`.
- Runtime IK logic must not write position, rotation, or scale to foot target nodes.
- IK solve consumes targets as provided.

### Placement

- `LegIKController` extends `Skeleton3DModifier3D` and must be a direct child of `Skeleton3D`.

## In Scope

- Per-leg pole-target prediction from animated geometry.
- Degenerate-pose handling (persistence strategy).
- Foot target synchronisation ordering.
- Read-only foot target contract.
- Minimum pole offset floor enforcement.

## Out Of Scope

- Scene-level solver influence tuning.
- Specific minimum-offset distance values.
- Runtime target clamping or offset strategies beyond floor enforcement.
- IK-004 hip reconciliation implementation details.

## Acceptance Criteria

| ID | Description |
|----|-------------|
| AC-01 | Leg chain solver topology matches specification. |
| AC-02 | Pole-target persistence during degenerate poses. |
| AC-03 | Pole direction derived from animation geometry. |
| AC-06 | Solve moves towards foot target as provided. |
| AC-06a | Foot target sync runs before hip reconciliation. |
| AC-07 | Foot target transforms are read-only. |
| AC-12 | Left/right side consistency preserved. |
| AC-13 | Deterministic pole output for identical inputs. |
| AC-14 | Unconditional minimum offset floor enforced. |

Source-of-truth criteria wording is maintained in [IK-003 Overview](index.md).

## References

- [IK-003 Overview](index.md)
- [Leg-Feet IK Test Setup Contract](test-setup-contract.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)