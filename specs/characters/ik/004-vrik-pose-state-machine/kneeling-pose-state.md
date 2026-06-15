---
id: IK-004-Kneeling
title: Kneeling Pose State
---

# Kneeling Pose State

## Requirement

Define the implementation contract for the kneeling pose state: trigger model, animation control, and locomotion output.

## Goal

Deliver kneeling behaviour: armed-then-retreat trigger model, transition constraints, movement limits.

## User Requirements

1. Players must be able to transition from crouching to kneeling using an armed-then-retreat trigger model.
2. The Standing→Kneeling transition must require a crouch-depth gate before kneeling becomes reachable.
3. After kneeling, both transition directions stay locked until forward-axis returns to baseline.
4. Players must have movement restricted while in the kneeling pose (rotation remains allowed).
5. Players must retain rotation capability while kneeling.

## Technical Requirements

### Transition Model

1. The Standing→Kneeling transition uses an armed-then-retreat trigger model measured from the full-crouch baseline:
   - The trigger input is the forward-axis offset from the pose-neutral or full-crouch baseline.
   - The transition becomes armed after sufficient forward travel from the full-crouch baseline.
   - The transition fires only after retreating from the armed peak by a configurable amount.
2. The Standing→Kneeling transition additionally requires a crouch-depth threshold before triggering.
3. The Kneeling→Standing return transition uses the same armed-then-retreat model, providing bidirectional
   transitions.
4. After kneeling, transitions stay locked until forward-axis returns near the baseline.
5. Kneeling transition thresholds use normalised ratios derived from rest-pose body measures, not absolute metres.
6. The crouch-depth gate uses at minimum the head-height measure from rest pose (`RestHeadHeight`).

### Animation Control

7. The kneeling pose uses AnimationTree state-machine nodes with authored auto-advance transitions.
8. The pose state machine emits state-changed observations for consumer reaction.

### Locomotion Permissions

9. Kneeling returns `LocomotionPermissions.RotationOnly`: blocks movement, allows rotation.
10. The pose-state-machine exposes locomotion permissions by delegating to the active pose state.

### Parameters

11. Tunable parameters use flexible ratios (for example `0.85 × RestHeadHeight`) rather than fixed absolute values.
12. Configuration parameters include:
    - Armed threshold for forward travel
    - Retreat threshold for firing
    - Neutral return max offset ratio for transition lockout
    - Crouch depth gate threshold

## In Scope

- Kneeling pose animation control via AnimationTree state-machine nodes.
- Armed-then-retreat transition model from standing (covering standing-to-crouching continuum).
- Crouch-depth gate requirement before kneeling transition becomes reachable.
- Transition lockout after kneeling until forward-axis returns to baseline.
- Locomotion permission output (rotation-only for kneeling).
- Bidirectional standing↔kneeling transitions.

## Out Of Scope

- Final numeric threshold values beyond defined configuration points.
- Per-state animation authoring details.
- Collision-based or locomotion-coupled state detection.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-K-01 | Standing→Kneeling uses armed-then-retreat trigger model from full-crouch baseline. | User + Technical |
| AC-K-02 | Crouch-depth gate must be satisfied before kneeling transition triggers. | User + Technical |
| AC-K-03 | Transition lockout persists until forward-axis returns to baseline. | User + Technical |
| AC-K-04 | Kneeling returns `LocomotionPermissions.RotationOnly`. | User + Technical |
| AC-K-05 | Kneeling animation uses AnimationTree state-machine with authored auto-advance. | Technical |
| AC-K-06 | Transition thresholds use normalised ratios from rest-pose body measures. | Technical |
| AC-K-07 | Players can transition from kneeling back to standing using armed-then-retreat model. | User |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- @game/assets/characters/templates/animation/animation_tree_root_player.tres
- @game/assets/characters/templates/reference_female/reference_female_player.tscn
