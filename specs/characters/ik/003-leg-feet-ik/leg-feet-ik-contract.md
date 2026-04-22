---
title: Leg-Feet IK Contract
---

# Leg-Feet IK Contract

## Purpose

Define the solver and controller contract for IK-003: per-leg `TwoBoneIK3D` solving with knee pole-target prediction
derived from current animation geometry (primary method) with foot orientation-based fallback for degenerate cases.

## Contract Scope

- One `TwoBoneIK3D` chain per leg (left and right), solving upper leg to foot.
- A per-leg controller (`LegIKController`) that updates pole-target position before IK solve.
- **Primary geometric method**: Knee pole direction derived from current animation geometry:
  - `o1` = midpoint(upper leg origin, foot IK target position)
  - `o2` = midpoint(upper leg origin, current foot bone position) (line origin for pole-target placement)
  - Primary pole direction = normalise(lower leg origin - o2)
- **Fallback method**: Foot orientation-based pole direction when animation-derived direction is degenerate.
- Unconditional knee pole minimum-offset safeguarding derived from rest leg length.
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

`LegIKController` computes a pole-target direction using the primary geometric method each frame, with fallback to foot orientation
when the animation-derived direction is degenerate, then writes the resulting pole-target transform for downstream IK solve.

The solver/controller pipeline must solve towards provided foot targets as inputs. Foot target transforms are read-only
for IK-003 runtime logic and must not be modified by `LegIKController` or companion solver wiring.

## Foot Target Immutability Contract

1. Foot target transforms are input goals for `TwoBoneIK3D` solve behaviour.
2. `LegIKController` must not write position, rotation, or scale to foot target nodes.
3. IK-003 runtime logic must not clamp, offset, or otherwise rewrite foot target transforms before solve.

## Knee Pole Prediction

The knee pole prediction uses a primary geometric method with fallback to foot orientation for degenerate cases.

### Primary Geometric Method

The controller computes the knee pole direction from current animation geometry:

1. **Compute reference points**:
   - `o1` = midpoint(upper leg origin position, foot IK target position)
   - `o2` = midpoint(upper leg origin position, current foot bone position)

2. **Compute primary pole direction**:
   - Vector from `o2` to lower leg origin = lower leg origin - `o2`
   - Primary pole direction = normalise(lower leg origin - `o2`)

3. **Line origin for pole-target placement**:
   - The pole-target line passes through `o2` along the primary pole direction.
   - Offset distance logic remains unchanged (ratio/minimum/floor as defined in Technical Requirements).

### Fallback Trigger And Method

When the animation-derived direction is degenerate, the system must fall back to foot orientation-based pole direction:

1. **Fallback trigger**: The direction from `o2` to lower leg origin is degenerate when:
   - Magnitude is too short (below implementation-defined threshold), OR
   - Direction is near-zero (near-zero magnitude after normalisation attempts)

2. **Fallback method**: Use foot IK target orientation to derive pole direction:
   - Uses the existing foot forward/up axis interpolation contract.
   - Foot forward axis and foot up axis are extracted from foot IK target rotation.
   - The forward/up interpolation weighting uses the dot product between foot forward and leg direction.

3. **Fallback pole direction**: Same interpolation formula as the original contract, applied to foot IK target orientation.

### Primary-to-Fallback Transition

The transition between primary and fallback methods must be:

1. **Deterministic**: Given identical skeleton pose, the same method is always selected.
2. **Smooth**: No discontinuous jumps when switching from primary to fallback.
3. **Deterministic threshold**: Fallback trigger threshold is an implementation-defined internal constant (not exposed as a tunable parameter).

### Offset Distance Logic

The offset distance computation remains unchanged from the original contract:

- Uses ratio-based offset (for example 0.5 * leg length)
- Enforces minimum floor unconditionally: `max(MinimumPoleOffset, (RestLegLength * 0.5) + RestLegHalfPoleOffsetMargin)`
- Line origin for placement uses `o2` (not `o1`)

### Side Consistency Requirement

The resulting pole direction must preserve left/right side consistency so knees do not cross inward unexpectedly during
neutral standing poses, regardless of whether primary or fallback method is used.

### Determinism Requirement

Given identical skeleton pose and foot target transforms, knee pole output must be deterministic, including the
determination of whether primary or fallback method is active.

### Unconditional Knee-Pole Minimum Offset Safeguard

The controller must enforce a minimum pole offset floor unconditionally before writing the pole-target position.

The floor contract is:

`max(MinimumPoleOffset, (RestLegLength * 0.5) + RestLegHalfPoleOffsetMargin)`

Implementation obligations:

1. `RestLegLength` must be sourced from the leg's rest/bind configuration and treated as the reference length input for this safeguard.
2. `MinimumPoleOffset` must remain a tunable lower-bound parameter.
3. `RestLegHalfPoleOffsetMargin` must remain a tunable additive margin parameter.
4. This safeguard applies unconditionally and is not compression-gated.
5. This safeguard must not mutate foot target transforms and must operate within the existing read-only target contract.

Exact tuning constants are implementation-defined unless constrained by another spec.

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
- AC-03 (primary geometric method)
- AC-04 (fallback trigger and method)
- AC-05 (primary-to-fallback transition)
- AC-06 (solve-to-target)
- AC-06a (foot target sync stage)
- AC-07
- AC-12
- AC-13
- AC-14 (unconditional minimum offset)

Source-of-truth criteria wording is maintained in [IK-003 Overview](index.md#acceptance-criteria).

## References

- [IK-003 Overview](index.md)
- [Leg-Feet IK Test Setup Contract](test-setup-contract.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
