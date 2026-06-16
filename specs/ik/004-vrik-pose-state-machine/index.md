---
id: IK-004
title: VRIK Pose State Machine And Hip Reconciliation
---

# VRIK Pose State Machine And Hip Reconciliation

## Requirement

Define an implementation-ready specification for player pose-state orchestration and hip reconciliation that builds on
existing IK prerequisites without redefining arm, spine, or leg solver contracts, and provides locomotion permission
contributions to external locomotion systems.

## Goal

Provide a framework-first, extensible contract for MVP pose states and state transitions, plus a state-dependent hip
reconciliation layer, while keeping tuning thresholds flexible for later iteration. Additionally, provide locomotion
permission outputs that gate player movement in non-standing poses.

## User Requirements

1. Players must see coherent full-body pose behaviour across MVP states: standing, kneeling, and crawling.
2. Players must experience continuous pose transitions during player-driven movement.
3. Players must retain stable feet placement aligned with existing lower-limb constraints.
4. Players must receive predictable calibration behaviour across non-standing poses.
5. Pose transitions use an armed-then-retreat trigger model for intentional player control.
6. Standing-family hip reconciliation must allow natural stoop and lean while preserving strong vertical crouch.
7. Players must be protected from extreme hip deformation beyond configurable state-defined limits.
8. Players must have movement restricted in poses that do not support walking.
9. Players must have movement permitted during the all-fours crawling phase.
10. Players must retain rotation capability across all poses for MVP.

## Technical Requirements

1. IK-001, IK-002, and IK-003 are normative prerequisites and must be referenced as dependencies.
2. Pose states and transitions must be implemented as Godot `Resource` definitions with customisable properties.
3. Runtime state selection must use head and hand IK-target transforms plus internal or animation-derived values only.
   - **Provider influence distinction**: Pose-state inputs are separate from provider influence. IK-target transforms
     used for pose detection are the downstream result of provider processing, not the raw provider intent itself.
     The pose-state machine observes the resolved target transforms after provider influence has been applied.
4. Collision-derived or locomotion-system-derived inputs must not be required for IK-004 delivery.
5. Calibration must use viewpoint-node semantics and body-proportion signals without standing-only head-rest.
6. Feet positions from animation remain the source of truth; pose-state and hip reconciliation must not replace that.
7. Each pose state must drive both animation control and the hip reconciliation profile as coupled responsibilities.
8. State selection must be inferred from IK-target transforms and animation or runtime signals.
9. The state machine must evaluate per tick from an immutable read-only context snapshot.
10. The pose-state-machine must expose locomotion permissions to external consumers as a single permission source that
    delegates to the active pose state. Non-standing poses return rotation-only permissions except all-fours crawling.
11. The pose-state-machine must expose locomotion animation-state targets by delegating to the active pose state each
    tick, with all-fours crawling returning the forward movement target.
12. Provider influence gating (for example disabling arm IK when provider influence is 0) must not interfere with
    pose-state detection. The pose-state machine observes final IK target transforms after provider processing;
    if a provider deactivates an arm, the pose-state machine sees the resulting rest or fallback transform.

## In Scope

- MVP state implementation: standing, kneeling, and crawling.
- Framework coverage for stooping and sitting for future expansion.
- State detection and transition architecture using extensible Godot `Resource` definitions.
- State-dependent hip translation reconciliation.
- Locomotion permission source integration for movement gating in non-standing poses.
- Reusable character logic composition via template-authored exported references, not
  through deep inheritance of character-specific scenes.

## Out Of Scope

- Re-defining solver-specific behaviour owned by IK-001/002/003.
- Collision and locomotion signal integration as required state inputs.
- Final numeric thresholds, curve tuning constants, and strict performance budgets.
- Optional expansion states beyond the MVP set.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-00 | The specification separates user-visible behaviour from implementation contracts. | User + Technical |
| AC-01 | IK-004 identifies standing, kneeling, and crawling as required MVP pose states. | User |
| AC-02 | IK-004 references IK-001/002/003 as prerequisites without duplicating solver contracts. | Technical |
| AC-03 | Pose states and transitions are specified as extensible `Resource`-driven contracts. | Technical |
| AC-30 | The pose-state-machine serves as a locomotion permission provider. | Technical |
| AC-31 | Each pose state provides locomotion permissions appropriate for that pose. | Technical |
| AC-32 | Non-standing poses return rotation-only permissions. | User + Technical |
| AC-32a | AllFours crawling sub-state permits movement. | User + Technical |
| AC-33 | Standing pose family allows movement only when blend is below a configurable threshold. | User + Technical |
| AC-34 | Movement permission threshold is configurable on `StandingPoseState`. | Technical |
| AC-35 | Pose states may optionally return a locomotion state target. | Technical |
| AC-36 | The pose-state-machine exposes locomotion state targets. | Technical |
| AC-37 | AllFours crawling sub-state returns forward movement target. | Technical |
| AC-38 | The pose-state-machine delegates to the active pose state each tick. | Technical |
| AC-39 | Pose-state observes resolved transforms after provider processing. | Technical |
| AC-40 | Provider gating does not interfere with pose detection; uses rest/fallback transforms. | Technical |

## References

- [VRIK System](../index.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- @game/assets/characters/templates/ik/vrik.tscn
- @game/assets/characters/templates/animation/animation_tree_root_player.tres
- @game/assets/characters/templates/reference_female/reference_female_player.tscn
- @game/assets/characters/reference/player.tscn
