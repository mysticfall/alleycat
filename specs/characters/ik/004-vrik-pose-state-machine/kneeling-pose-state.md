---
id: IK-004-Kneeling
title: Kneeling Pose State
---

# Kneeling Pose State

## Requirement

Define the implementation contract for the Kneeling pose state, including animation control, transition triggers from standing, and locomotion permission output.

## Goal

Provide detailed behaviour requirements for the kneeling pose, including the armed-then-retreat transition model, animation state management, and movement restrictions.

## User Requirements

1. Players must be able to transition from crouching to kneeling using an armed-then-retreat trigger model.
2. The Standing→Kneeling transition must require a crouch-depth gate before kneeling becomes reachable.
3. Following any kneeling transition, both transition directions must remain locked until the forward-axis offset returns to near the baseline.
4. Players must have movement restricted while in the kneeling pose (rotation remains allowed).
5. Players must retain rotation capability while kneeling.

## Technical Requirements

### Transition Model

1. The Standing→Kneeling transition uses an armed-then-retreat trigger model measured from the full-crouch baseline:
   - The trigger input is the forward-axis offset from the pose-neutral or full-crouch baseline.
   - The transition becomes armed after sufficient forward travel from the full-crouch baseline.
   - The transition fires only after retreating from the armed peak by a configurable amount.
2. The Standing→Kneeling transition is additionally gated by a crouch-depth threshold that must be satisfied before the transition can trigger.
3. The Kneeling→Standing return transition uses the same armed-then-retreat model, providing bidirectional transitions.
4. Following any kneeling transition, both transition directions remain locked until the forward-axis offset returns near the neutral or full-crouch baseline.
5. Kneeling transition thresholds use normalised ratios derived from rest-pose body measures, not absolute metres.
6. At minimum, the head-height measure from rest pose (`RestHeadHeight`) defines the reference for the normalised crouch-depth gate.

### Animation Control

7. The kneeling pose (KneelingEnter, Kneeling, KneelingExit) is authored as separate AnimationTree state-machine nodes using normal animation playback.
8. The AnimationTree uses authored auto-advance for transition clips rather than per-tick TimeSeek scrubbing.
9. Transition Resources own AnimationTree travel into authored transition states when they fire.

### Transition Lifecycle

10. Transition Resources must support optional lifecycle hooks invoked in this order around a state switch:
    `OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`.
11. The state machine must emit a state-changed observation so consumers can react.

### Locomotion Permissions

12. The kneeling pose returns `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation).
13. The pose-state-machine implements `ILocomotionPermissionSource` and delegates to the active pose state.
14. Each pose state provides `GetLocomotionPermissions(PoseStateContext)` that returns appropriate permissions.

### Parameters

15. Tunable parameters use flexible ratios (for example `0.85 × RestHeadHeight`) rather than fixed absolute values.
16. Configuration parameters include:
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
| AC-K-05 | Kneeling animation uses authored auto-advance for transitions, not TimeSeek. | Technical |
| AC-K-06 | Transition thresholds use normalised ratios from rest-pose body measures. | Technical |
| AC-K-07 | Players can transition from kneeling back to standing using armed-then-retreat model. | User |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn