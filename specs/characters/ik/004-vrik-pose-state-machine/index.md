---
id: IK-004
title: VRIK Pose State Machine And Hip Reconciliation
---

# VRIK Pose State Machine And Hip Reconciliation

## Requirement

Define an implementation-ready specification for player pose-state orchestration and hip reconciliation that builds on
existing IK prerequisites without redefining arm, spine, or leg solver contracts.

## Goal

Provide a framework-first, extensible contract for MVP pose states and state transitions, plus a state-dependent hip
reconciliation layer, while keeping tuning thresholds flexible for later iteration.

## Background And Motivation

The VRIK system must present immersive full-body poses (crawl, lying, sitting, kneeling) from just headset and two
controllers, not hands-only VR. Head and hand transforms come from XR devices; lower-body transforms are primarily
animation-driven. A naive scheme that offsets the hip by the headset displacement from its rest position cannot
distinguish stooping from crouching, nor support poses far from the animation reference (for example, lying down while
the feet animation is still standing idle). A pose-state layer is therefore required to select the appropriate
animation (or `AnimationTree` parameters) AND apply state-specific hip reconciliation as a single coupled
responsibility.

## Specification Structure

This page is the authoritative overview for IK-004. Detailed implementation contracts are split into focused pages:

| Document | Purpose |
|----------|---------|
| [Pose State Machine Contract](pose-state-machine-contract.md) | State orchestration, transitions, pose classification, and AnimationTree binding |
| [Hip Reconciliation Contract](hip-reconciliation-contract.md) | State-dependent hip translation reconciliation and limiting |

Both linked pages are normative dependencies for implementation.

## User Requirements

1. Players must see coherent full-body pose behaviour across MVP states: standing (covering standing-to-crouching continuum), kneeling, stooping, sitting, and crawling (all fours).
2. Players must experience continuous pose transitions during player-driven movement, without requiring authored transitions for every intermediate body position.
3. Players must retain stable feet placement behaviour aligned with existing lower-limb constraints.
4. Players must receive predictable calibration behaviour across non-standing poses, without relying on standing-only head-rest assumptions.
5. Pose transitions (Standing↔Kneeling, Standing/Kneeling↔AllFours) use an armed-then-retreat trigger model for intentional player control.
6. Standing-family hip reconciliation must allow natural stoop/lean poses while preserving strong vertical crouch response.
7. Players must be protected from extreme hip deformation beyond configurable state-defined limits.

## Technical Requirements

1. IK-001, IK-002, and IK-003 are normative prerequisites and must be referenced as dependencies, not re-specified.
2. Pose states and transitions must be implemented as Godot `Resource` definitions with customisable properties.
3. Runtime state selection must use head and hand IK-target transforms (`HeadTargetTransform`, `LeftHandTargetTransform`, `RightHandTargetTransform`) plus internal or animation-derived values only.
4. Collision-derived or locomotion-system-derived inputs must not be required for IK-004 delivery.
5. Calibration must use viewpoint-node semantics and body-proportion signals; no standing head-rest assumptions for non-standing states.
6. Feet positions from animation remain the source of truth; pose-state and hip reconciliation must not replace that ownership.
7. Each pose state must drive BOTH animation control AND the hip reconciliation profile as coupled responsibilities.
8. State selection must be inferred from IK-target transforms and animation/runtime signals; explicit button input avoided unless no automatic signal is viable.
9. The state machine must evaluate per tick from an immutable read-only context snapshot (`PoseStateContext`).

## In Scope

- MVP state coverage: standing (covering standing-to-crouching continuum), kneeling, stooping, sitting, and crawling (all fours).
- Framework-first state detection and transition architecture using extensible Godot `Resource` definitions.
- State-specific disambiguation between overlapping poses (stoop vs crouch) as a permanent classifier responsibility.
- State-dependent hip translation reconciliation behaviour.
- Integration boundaries to IK-001/002/003 prerequisite contracts.
- **Reusable scene composition**: `player.tscn` instances separate reusable components.

## Out Of Scope

- Re-defining solver-specific behaviour already owned by IK-001/002/003.
- Collision and locomotion signal integration as required state inputs.
- Final numeric thresholds, curve tuning constants, and strict performance budgets.
- Optional expansion states beyond the MVP set.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement                                                                                                                                                           | Layer |
| --- |-----------------------------------------------------------------------------------------------------------------------------------------------------------------------| --- |
| AC-00 | The specification separates user-visible behaviour requirements from implementation contracts and keeps both layers normative.                                        | User + Technical |
| AC-01 | IK-004 identifies standing (covering standing-to-crouching continuum), kneeling, stooping, sitting, and crawling as required MVP pose states.                         | User |
| AC-02 | IK-004 references IK-001/002/003 as prerequisites without duplicating their solver contracts.                                                                         | Technical |
| AC-03 | Pose states and transitions are specified as extensible `Resource`-driven contracts.                                                                                  | Technical |
| AC-04 | Input contracts for this phase are restricted to head and hand IK-target transforms and internal/animation-derived values, with collision/locomotion inputs deferred. | Technical |
| AC-05 | Calibration contracts require viewpoint-node semantics and body-proportion references, without standing-rest-pose assumptions for non-standing states.                | Technical |
| AC-06 | Feet positions from animation are explicitly defined as source of truth for this phase.                                                                               | User + Technical |
| AC-07 | Hip reconciliation is specified as translation-centric, state-dependent behaviour with configurable limits via `OffsetLimits3D` resource type.                         | Technical |
| AC-10 | Players see coherent visible full-body pose continuity while moving between required pose states.                                                                     | User |
| AC-11 | Each pose state binds both animation selection and hip reconciliation behaviour as a coupled responsibility.                                                           | Technical |
| AC-12 | State switching relies on inferred signals from IK-target transforms and runtime state; explicit button-based pose switching avoided by default.                      | User + Technical |
| AC-14 | The state machine evaluates per tick from an immutable `PoseStateContext` snapshot.                                                                                   | Technical |
| AC-16 | Animation control for pose states is owned by `PoseState` resources with lifecycle callbacks.                                                                         | Technical |
| AC-18 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value.                                                       | Technical |
| AC-27 | The state machine includes an `AllFoursPoseState` resource with `transitioning` and `crawling` sub-states.                                                            | Technical |

## References

- [Player VRIK System](../index.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- @game/assets/characters/ik/vrik.tscn
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn
- @game/assets/characters/reference/female/reference_female_ik.tscn
- @game/assets/characters/reference/player.tscn
