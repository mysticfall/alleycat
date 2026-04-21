---
title: Leg-Feet IK Contract
---

# Leg-Feet IK Contract

## Purpose

Define the solver and controller contract for IK-003: per-leg `TwoBoneIK3D` solving with knee pole-target prediction
derived from foot orientation.

## Contract Scope

- One `TwoBoneIK3D` chain per leg (left and right), solving upper leg to foot.
- A per-leg controller (`LegIKController`) that updates pole-target position before IK solve.
- Knee pole-direction prediction from the associated foot axes.
- Compressed-state knee pole minimum-offset safeguarding derived from rest leg length.
- Read-only foot target contract: runtime logic consumes target transforms as goals and never mutates them.

## Mechanism

Each leg uses a Godot `TwoBoneIK3D` node configured for upper-leg → lower-leg → foot.

### Foot Target Synchronisation Stage

Before each leg IK solve cycle, a dedicated foot-target sync controller (`FootTargetSyncController`) reads the current animated foot bone transforms
(position + rotation) from the skeleton and copies them to the corresponding foot target nodes. This ensures the downstream pole-target
computation and solve operate on the current animation state, not stale targets from earlier frames.

#### Ordering Contract

`FootTargetSyncController` must sample animated foot transforms **before any modifier mutates them**. It must run
before `HipReconciliationModifier` and any other foot-mutating modifiers in the skeleton modifier pipeline.

This ordering is a **scene/pipeline authoring contract** enforced through scene authoring (node order under `Skeleton3D`).
Runtime auto-reordering is intentionally not used — ordering is managed manually at scene authorship time.

Sync ordering (per tick):

1. Animation player samples pose.
2. **`FootTargetSyncController`** reads animated left/right foot bone transforms and writes to foot target nodes. Runs before any foot-mutating modifier.
3. `HipReconciliationModifier` runs (adjusts hip bone before leg IK solve).
4. `LegIKController` (left/right) computes pole-target positions from the synced foot targets.
5. `TwoBoneIK3D` (left/right) solves towards the synced foot targets.

This ordering guarantees deterministic solve behaviour when animation timing or `TimeSeek` position changes.

`LegIKController` computes a pole-target direction from foot orientation each frame, then writes the resulting pole-target
transform for downstream IK solve.

The solver/controller pipeline must solve towards provided foot targets as inputs. Foot target transforms are read-only
for IK-003 runtime logic and must not be modified by `LegIKController` or companion solver wiring.

## Foot Target Immutability Contract

1. Foot target transforms are input goals for `TwoBoneIK3D` solve behaviour.
2. `LegIKController` must not write position, rotation, or scale to foot target nodes.
3. IK-003 runtime logic must not clamp, offset, or otherwise rewrite foot target transforms before solve.

## Knee Pole Prediction

### Inputs

Per leg, the controller must derive:

- **Leg Direction**: normalised vector from upper-leg joint towards current foot target.
- **Foot Forward Axis**: associated foot forward vector in the same basis as leg direction.
- **Foot Up Axis**: associated foot up vector in the same basis as leg direction.

### Axis Interpolation Requirement

Pole-direction selection must blend between foot forward and foot up axes using forward-dot-driven weighting.

The contract requires:

1. Compute a forward alignment dot value between foot forward and leg direction.
2. Derive interpolation weight(s) from that forward alignment value.
3. Interpolate between foot forward and foot up using the derived weight(s), then produce a normalised blended axis as
   the knee pole-direction seed.

The contract does not require a separate up-axis alignment dot calculation.

Exact remapping curves and clamp constants are implementation-defined, but behaviour must satisfy AC-03 and AC-04.

### Side Consistency Requirement

The resulting pole direction must preserve left/right side consistency so knees do not cross inward unexpectedly during
neutral standing poses.

### Determinism Requirement

Given identical skeleton pose and foot target transforms, knee pole output must be deterministic.

### Compressed Knee-Pole Minimum Offset Safeguard

In compressed crouch-like leg states, the controller must enforce a minimum pole offset floor before writing the
pole-target position.

The floor contract is:

`max(MinimumPoleOffset, (RestLegLength * 0.5) + RestLegHalfPoleOffsetMargin)`

Implementation obligations:

1. `RestLegLength` must be sourced from the leg's rest/bind configuration and treated as the reference length input for
   this safeguard.
2. `MinimumPoleOffset` must remain a tunable lower-bound parameter.
3. `RestLegHalfPoleOffsetMargin` must remain a tunable additive margin parameter.
4. Compression gating that determines when this floor is enforced must remain tunable.
5. This safeguard must not mutate foot target transforms and must operate within the existing read-only target contract.

Exact compression thresholds and final tuning constants are implementation-defined unless constrained by another spec.

## Solve-To-Target Behaviour

IK-003 requires solve-to-target behaviour where each `TwoBoneIK3D` leg solve consumes the provided foot target
transform as the runtime goal input.

This contract does not define an additional runtime bounded-clamp requirement on target correction distance.

Any scene-level tuning (for example solver influence values) is implementation-defined unless constrained by another spec,
and must not violate the read-only foot target contract.

## Placement And Ordering

`LegIKController` extends `SkeletonModifier3D` and must be a direct child of `Skeleton3D`.

Per-frame full pipeline ordering (including IK-004 hip reconciliation):

1. Animation player samples pose.
2. **`FootTargetSyncController`** reads animated foot transforms before any modifier mutates them. Runs before `HipReconciliationModifier` and any foot-mutating modifiers.
3. `HipReconciliationModifier` runs (adjusts hip bone before leg IK solve).
4. `LegIKController` (left)
5. `LegIKController` (right)
6. `TwoBoneIK3D` (left leg)
7. `TwoBoneIK3D` (right leg)

This ordering is a **scene/pipeline authoring contract** enforced through scene authoring (node order under `Skeleton3D`).
Runtime auto-reordering is intentionally not used — ordering is managed manually at scene authorship time.

Additional modifiers are allowed only if they do not break controller-before-solver ordering and preserve the FootTargetSyncController-before-HipReconciliationModifier ordering.

## Naming And Design Consistency With IK-002

- Use per-side controller instances with explicit side assignment.
- Keep contract-first documentation split (overview + dedicated contract pages).
- Keep solver/controller responsibilities separated (controller predicts poles, solver applies IK).

## Acceptance Criteria Coverage

This contract defines details for:

- AC-01
- AC-02
- AC-03
- AC-04
- AC-05
- AC-05a (foot target sync stage)
- AC-06
- AC-12
- AC-13

Source-of-truth criteria wording is maintained in [IK-003 Overview](index.md#acceptance-criteria).

## References

- [IK-003 Overview](index.md)
- [Leg-Feet IK Test Setup Contract](test-setup-contract.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
