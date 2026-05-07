---
id: IK-004-AllFours
title: AllFours Pose State
---

# AllFours Pose State

## Requirement

Define the implementation contract for the AllFours (crawling) pose state.

## Goal

Provide behaviour requirements for the AllFours pose, including the internal dual-phase
state machine, head-driven animation seeking, and return transitions.

## User Requirements

1. Players must be able to transition from Standing to AllFours by moving the
   head forward beyond a configurable threshold.
2. Players must be able to transition from Kneeling to AllFours by moving the
   head forward beyond a configurable threshold.
3. Players must experience smooth transition animation from the entering
   position to the crawling hold pose, driven by head position.
4. While crawling on all fours, if the player raises their head vertically
   above a threshold, they must transition to prepare for return to standing.
5. Players must have forward movement restricted while in the AllFours pose,
   but rotation remains allowed.
6. Players must retain rotation capability while crawling.

## Technical Requirements

The AllFours pose implements a dual-phase state machine that drives head-driven animation seeking.

### Entry Trigger

The entry trigger uses head forward offset as the primary signal. This signal is normalised
from the skeleton-local origin and mapped to an animation seek window.

- Entry becomes armed when forward offset exceeds a configurable entry threshold.
- Entry fires when the player continues forward past the armed point by a configurable margin.
- AllFours is reachable from both Standing and Kneeling poses.

### Animation Control

- **Transitioning phase**: drives entry animation via head-driven seeking using forward offset.
- **Crawling phase**: plays looping crawl animation when forward offset
  exceeds a configurable threshold.

### Return Transitions

- **Crawling to transitioning**: when vertical offset exceeds a configurable threshold.
- **Transitioning to Standing**: when forward offset drops below the entry threshold
  minus a configurable return margin. Destination is always Standing.

### Locomotion Permissions

The AllFours pose returns `LocomotionPermissions.RotationOnly` (blocks
movement, allows rotation).

## In Scope

- AllFours pose animation control with dual-phase internal state machine.
- Head forward offset-driven entry animation seeking.
- Looping crawl animation in `crawling` sub-state.
- Transition triggers from Standing and Kneeling poses.
- Return transition to Standing based on head forward offset decrease.
- Return to transitioning from crawling based on head vertical offset increase.
- Locomotion permission output.

## Out Of Scope

- Final numeric threshold values.
- Per-state animation authoring details.
- Collision-based or locomotion-coupled state detection.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-AF-01 | AllFours implements dual sub-states: transitioning and crawling. | Technical |
| AC-AF-02 | Entry uses head forward offset-driven animation seek. | Technical |
| AC-AF-03 | Crawl sub-state plays looping crawl animation. | Technical |
| AC-AF-04 | AllFours reachable from both Standing and Kneeling poses. | User + Technical |
| AC-AF-05 | Return to Standing when forward offset drops below threshold. | User + Technical |
| AC-AF-06 | Return to transitioning when vertical offset exceeds threshold. | User + Technical |
| AC-AF-07 | AllFours returns LocomotionPermissions.RotationOnly. | User + Technical |
| AC-AF-08 | Threshold parameters are configurable. | Technical |
| AC-AF-09 | Players experience smooth entry animation driven by head position. | User |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)