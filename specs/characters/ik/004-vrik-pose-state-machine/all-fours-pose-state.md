---
id: IK-004-AllFours
title: AllFours Pose State
---

# AllFours Pose State

## Requirement

Define the implementation contract for the AllFours (crawling) pose state, including the internal transitioning/crawling sub-state machine, animation control, transition triggers, and locomotion permission output.

## Goal

Provide detailed behaviour requirements for the AllFours pose, including the dual-phase internal state machine, head-driven animation seeking, and return transitions.

## User Requirements

1. Players must be able to transition from the Standing pose to an all-fours crawling pose by moving the head forward beyond a threshold.
2. Players must be able to transition from the Kneeling pose to an all-fours crawling pose by moving the head forward beyond a threshold.
3. Players must experience a smooth transition animation from the entering position to the crawling hold pose, driven by head position.
4. While crawling on all fours, if the player raises their head vertically above a threshold, they must transition back to prepare for a return to standing.
5. Players must have movement restricted while in the AllFours pose (rotation remains allowed).
6. Players must retain rotation capability while crawling.

## Technical Requirements

### Internal State Machine

1. `AllFoursPoseState` implements two internal sub-states:
   - `transitioning`: active immediately upon entering AllFours, drives entry animation via `AnimationNodeTimeSeek`.
   - `crawling`: active after entry animation completes, holds the crawl pose.

### Transition Triggers

2. The AllFours entry trigger uses head forward offset as the primary signal:
   - The transition becomes armed when the head's normalised forward offset exceeds a configurable threshold (default range start: 0.42).
   - The transition fires when the player continues forward past the armed point by a configurable margin.
3. The forward offset is normalised using rest-pose body measures, mapping the range 0.42 to 0.73 to the animation seek window.
4. AllFours is reachable from both `StandingPoseState` and `KneelingPoseState`.

### Animation Control - Transitioning

5. In the `transitioning` sub-state, the state drives an `AnimationNodeTimeSeek` node using animation `All Fours-enter`.
6. The seek window spans 1.2 seconds to 3.5417 seconds.
7. The seek position is calculated from head's normalised forward offset:
   - Normalise: `seekPosition = (headForwardOffset - 0.42) / (0.73 - 0.42)`
   - Map to seek window duration [1.2, 3.5417] seconds.

### Animation Control - Crawling

8. When head forward offset exceeds 0.73, the internal state transitions from `transitioning` to `crawling`.
9. The `crawling` sub-state plays the looping animation `All Fours`.
10. The AllFours entry and crawl loops use different patterns: entry uses TimeSeek scrubbing, crawl holds use standard playback.

### Return Transitions

11. While in the `crawling` sub-state, if the head's vertical offset increases above a configurable threshold (default: 0.3 as normalised ratio of rest head height), the state returns to `transitioning`.
12. While in the `transitioning` sub-state, if the head's forward offset decreases below 0.42 minus a configurable return margin, the framework transitions back to `StandingPoseState`.
13. The return destination is always Standing, not source-dependent.

### Configuration Parameters

14. AllFours threshold parameters must be configurable on the `AllFoursPoseState` or its associated transition resources:
    - Forward offset entry threshold (default: 0.42)
    - Forward offset return threshold (margin below entry)
    - Vertical offset climb threshold (default: 0.3)
    - Forward continue margin
    - Return margin

### Locomotion Permissions

15. The AllFours pose returns `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation).
16. The pose-state-machine implements `ILocomotionPermissionSource` and delegates to the active pose state.
17. Each pose state provides `GetLocomotionPermissions(PoseStateContext)` that returns appropriate permissions.

## In Scope

- AllFours pose animation control with dual-phase internal state machine (`transitioning`, `crawling`).
- Head forward offset-driven entry animation TimeSeek.
- Looping crawl animation in `crawling` sub-state.
- Transition triggers from Standing and Kneeling poses.
- Return transition to Standing based on head forward offset decrease.
- Return to transitioning from crawling based on head vertical offset increase.
- Locomotion permission output (rotation-only for AllFours).
- Configurable threshold parameters.

## Out Of Scope

- Final numeric threshold values beyond defined defaults and configuration points.
- Per-state animation authoring details.
- Collision-based or locomotion-coupled state detection.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-AF-01 | AllFours implements dual sub-states: `transitioning` and `crawling`. | Technical |
| AC-AF-02 | Entry uses head forward offset-driven TimeSeek with configurable thresholds. | Technical |
| AC-AF-03 | Crawl sub-state plays looping `All Fours` animation. | Technical |
| AC-AF-04 | AllFours reachable from both Standing and Kneeling poses. | User + Technical |
| AC-AF-05 | Return to Standing triggers when forward offset drops below return threshold. | User + Technical |
| AC-AF-06 | Return to transitioning triggers when vertical offset exceeds climb threshold. | User + Technical |
| AC-AF-07 | AllFours returns `LocomotionPermissions.RotationOnly`. | User + Technical |
| AC-AF-08 | Threshold parameters are configurable on AllFoursPoseState or transitions. | Technical |
| AC-AF-09 | Players experience smooth entry animation driven by head position. | User |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn