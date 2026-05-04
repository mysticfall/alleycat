---
id: IK-004
title: VRIK Pose State Machine And Hip Reconciliation
---

# VRIK Pose State Machine And Hip Reconciliation

## Purpose

Define the implementation contract for player pose-state orchestration and hip reconciliation, building on IK-001/002/003 prerequisites while providing locomotion permission contributions to external systems.

This specification defines how the VRIK system selects appropriate full-body poses from headset and controller input, coordinates animation control with hip translation reconciliation, and outputs locomotion permissions for non-standing poses.

## Requirement

Define an implementation-ready specification for player pose-state orchestration and hip reconciliation that builds on
existing IK prerequisites without redefining arm, spine, or leg solver contracts, AND provides locomotion permission contributions to external locomotion systems.

## Goal

Provide a framework-first, extensible contract for MVP pose states and state transitions, plus a state-dependent hip
reconciliation layer, while keeping tuning thresholds flexible for later iteration. Additionally, provide locomotion permission outputs that gate player movement in non-standing poses.

## Background And Motivation

The VRIK system must present immersive full-body poses (crawl, lying, sitting, kneeling) from just headset and two
controllers, not hands-only VR. Head and hand transforms come from XR devices; lower-body transforms are primarily
animation-driven. A naive scheme that offsets the hip by the headset displacement from its rest position cannot
distinguish stooping from crouching, nor support poses far from the animation reference (for example, lying down while
the feet animation is still standing idle). A pose-state layer is therefore required to select the appropriate
animation (or `AnimationTree` parameters) AND apply state-specific hip reconciliation as a single coupled
responsibility.

Additionally, locomotion systems need to gate player movement in poses where walking is not appropriate (for example, kneeling, sitting, all-fours transitioning). The pose-state-machine serves as the canonical source for these permissions.

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

1. Players must see coherent full-body pose behaviour across MVP states: standing (covering standing-to-crouching continuum), kneeling, stooping, sitting, and crawling (all fours).
2. Players must experience continuous pose transitions during player-driven movement, without requiring authored transitions for every intermediate body position.
3. Players must retain stable feet placement behaviour aligned with existing lower-limb constraints.
4. Players must receive predictable calibration behaviour across non-standing poses, without relying on standing-only head-rest assumptions.
5. Pose transitions (Standing↔Kneeling, Standing/Kneeling↔AllFours) use an armed-then-retreat trigger model for intentional player control.
6. Standing-family hip reconciliation must allow natural stoop/lean poses while preserving strong vertical crouch response.
7. Players must be protected from extreme hip deformation beyond configurable state-defined limits.
8. Players must have movement restricted in poses that do not support walking (for example, kneeling, sitting, all-fours transitioning).
9. Players must have movement permitted during the all-fours crawling (crawl-hold) phase while maintaining the all-fours pose.
10. Players must retain rotation capability across all poses for MVP.

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
10. **The pose-state-machine must implement `ILocomotionPermissionSource`** and expose locomotion permissions to external consumers:
    - The machine acts as a single permission source that delegates to the currently active pose state.
    - Each pose state provides its own `GetLocomotionPermissions(PoseStateContext)` implementation.
    - Non-standing poses return `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation), except as noted below.
    - The standing pose family allows movement when its blend is near fully upright.
    - The AllFours pose state's Crawling sub-state permits movement to enable crawl-hold locomotion.

11. **The pose-state-machine must implement `ILocomotionAnimationSource`** to expose locomotion animation-state targets to external locomotion systems:
    - The machine implements `ILocomotionAnimationSource` and provides `LocomotionStateTarget` property.
    - Each tick, the machine delegates to the active pose state's `GetLocomotionStateTarget(PoseStateContext)` method.
    - Pose states optionally return a `LocomotionStateTarget?` (record struct with `IdleStateName` and `MovementStateName`).
    - The AllFours pose state's Crawling sub-state returns the target `(AllFours, AllFoursForward)`, enabling root-motion-driven crawl locomotion.
    - Other pose states (standing family, kneeling, sitting, AllFours transitioning) return `null` by default.

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
| AC-31 | Each pose state provides `GetLocomotionPermissions(PoseStateContext)` that returns appropriate permissions for that pose. | Technical |
| AC-32 | Non-standing poses (kneeling, sitting, AllFours transitioning) return `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation). | User + Technical |
| AC-32a | AllFours Crawling (crawl-hold) sub-state permits movement to enable crawl-hold locomotion. | User + Technical |
| AC-33 | The standing pose family allows movement only when the standing/crouching blend is below a configurable threshold. | User + Technical |
| AC-34 | The movement permission threshold (`MovementAllowedMaximumPoseBlend`) is configurable on `StandingPoseState`. | Technical |
| AC-35 | Pose states may optionally return a `LocomotionStateTarget?` from `GetLocomotionStateTarget(PoseStateContext)`. | Technical |
| AC-36 | The pose-state-machine implements `ILocomotionAnimationSource` with `LocomotionStateTarget` property. | Technical |
| AC-37 | AllFours Crawling sub-state returns `LocomotionStateTarget(AllFours, AllFoursForward)`. | Technical |
| AC-38 | The pose-state-machine delegates to the active pose state each tick to resolve the target. | Technical |

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