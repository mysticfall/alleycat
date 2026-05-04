---
id: IK-004
title: VRIK Pose State Machine And Hip Reconciliation
---

# VRIK Pose State Machine And Hip Reconciliation

## Purpose

Define the implementation contract for player pose-state orchestration and hip reconciliation, building on IK-001/002/003 prerequisites while providing locomotion permission contributions to external systems.

This specification defines how the VRIK system selects appropriate full-body poses from headset and controller input, coordinates animation control with hip translation reconciliation, and outputs locomotion permissions for non-standing poses.

## Requirement

Provide a framework-first, extensible contract for pose states and state transitions, plus a state-dependent hip reconciliation layer. Keep tuning thresholds flexible for later iteration while delivering locomotion permission outputs that gate player movement in non-standing poses.

## Specification Structure

This page is the authoritative overview for IK-004. Detailed implementation contracts are distributed across focused subpages:

| Document | Purpose |
|----------|---------|
| [Pose State Machine Contract](pose-state-machine-contract.md) | State orchestration framework, transitions, pose classification, AnimationTree binding, and locomotion permission architecture |
| [Hip Reconciliation Contract](hip-reconciliation-contract.md) | State-dependent hip translation reconciliation, standing-family profile, and offset limiting |
| [Standing Pose State](standing-pose-state.md) | Standing-to-crouching continuum (MVP implemented) |
| [Kneeling Pose State](kneeling-pose-state.md) | Kneeling animation (MVP implemented) |
| [AllFours Pose State](all-fours-pose-state.md) | Crawling pose (MVP implemented) |

All linked pages are normative dependencies for implementation.

## User Requirements

1. Players must see coherent full-body pose behaviour across MVP states: standing (standing-to-crouching continuum), kneeling, and crawling (all fours).
2. Pose transitions (Standing↔Kneeling, Standing/Kneeling↔AllFours) use an armed-then-retreat trigger model for intentional player control.
3. Standing-family hip reconciliation must allow natural stoop/lean poses while preserving strong vertical crouch response.
4. Players must have movement restricted in poses that do not support walking (kneeling, sitting, crawling).
5. Players must retain rotation capability across all poses for MVP.

## Technical Requirements

1. IK-001, IK-002, and IK-003 are normative prerequisites and must be referenced as dependencies, not re-specified.
2. Pose states and transitions are implemented as Godot `Resource` definitions with customisable properties.
3. Runtime state selection uses head and hand IK-target transforms plus internal or animation-derived values only.
4. Collision-derived or locomotion-system-derived inputs are not required for IK-004 delivery.
5. Calibration uses viewpoint-node semantics and body-proportion signals without standing head-rest assumptions for non-standing states.
6. Each pose state drives both animation control AND the hip reconciliation profile as coupled responsibilities.
7. The state machine evaluates per tick from an immutable read-only context snapshot (`PoseStateContext`).
8. **The pose-state-machine implements `ILocomotionPermissionSource`** and exposes locomotion permissions to external consumers.

## Integration Boundaries

- **XR-to-IK**: Runtime XR-to-IK integration is defined in [XR-001: XRManager](../../xr/001-xr-manager/index.md).
- **IK Prerequisite Contracts**: Upper-body (IK-001), spine (IK-002), and lower-limb (IK-003) solvers remain prerequisites.
- **Locomotion Output**: Pose-state-machine provides `ILocomotionPermissionSource` contract to [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md).

## In Scope

- MVP state implementation: standing (standing-to-crouching continuum), kneeling, crawling (all fours).
- Framework coverage for stooping and sitting (extensible for future expansion).
- Framework-first state detection and transition architecture using extensible Godot `Resource` definitions.
- State-dependent hip translation reconciliation behaviour.
- Locomotion permission source integration for movement gating in non-standing poses.
- Reusable scene composition via separate reusable components in `player.tscn`.

## Out Of Scope

- Re-defining solver-specific behaviour already owned by IK-001/002/003.
- Collision and locomotion signal integration as required state inputs.
- Final numeric thresholds, curve tuning constants, and strict performance budgets.
- Optional expansion states beyond the MVP set.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-00 | The specification separates user-visible behaviour requirements from implementation contracts and keeps both layers normative. | User + Technical |
| AC-01 | IK-004 identifies standing, kneeling, and crawling as required MVP pose states. | User |
| AC-02 | IK-004 references IK-001/002/003 as prerequisites without duplicating their solver contracts. | Technical |
| AC-03 | Pose states and transitions are specified as extensible `Resource`-driven contracts. | Technical |
| AC-30 | The pose-state-machine implements `ILocomotionPermissionSource` and serves as a locomotion permission provider. | Technical |
| AC-32 | Non-standing poses return `LocomotionPermissions.RotationOnly`. | User + Technical |

## References

- [Player VRIK System](../index.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- @game/assets/characters/ik/vrik.tscn
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn
- @game/assets/characters/reference/female/reference_female_ik.tscn
- @game/assets/characters/reference/player.tscn

## Code-Spec Sync Note

This revision is specification-only reorganisation. No implementation changes required.