---
id: IK-004-Standing
title: Standing Pose State
---

# Standing Pose State

## Requirement

Define the implementation contract for the Standing pose state, covering
the standing-to-crouching continuum with continuous animation control and
hip reconciliation profile binding.

## Goal

Provide detailed behaviour requirements for the standing pose family,
including animation control, transition triggers, locomotion permissions,
and the default hip reconciliation profile.

## User Requirements

1. Players experience a continuous standing-to-crouching pose continuum
   driven by head position relative to standing baseline.
2. Players see coherent full-body pose behaviour spanning from upright
   standing through to full crouch.
3. The standing pose allows natural stoop and lean while preserving strong
   vertical crouch response.
4. Movement is allowed only when near fully upright; crouching restricts
   movement.
5. Rotation capability remains available across the full standing-to-crouching
   continuum.
6. Players are protected from extreme hip deformation via configurable
   state-defined limits.

## Technical Requirements

### Animation Control

1. The standing pose family uses a single AnimationTree state with continuous
   seek-based control for the standing-to-crouching continuum.
2. One standing-family PoseState resource covers the full continuum.
3. The Idle clip remains in the animation library for future layering but
   is not wired into the tree for MVP.

### Hip Reconciliation Profile

4. The default hip reconciliation profile for the standing family combines
   positional head offset, per-axis positional modulation, alignment-based
   vertical damping, and rotational offset derived from head orientation
   delta.

### Per-Axis Positional Modulation

5. The profile decomposes head-position offset into three components in the
   hip rest local frame: vertical, lateral, and forward.
6. Vertical component retains full hip travel; lateral and forward components
   are attenuated independently.
7. Each axis weight is independently configurable within the 0 to 1 range.

### Alignment-Based Vertical Damping

8. Alignment-based vertical damping applies an additional weight to the
   vertical component based on head direction alignment with the hip rest
   up axis.
9. A configurable minimum alignment weight prevents collapse during extreme
   head angles.
10. Degenerate cases where head offset is near zero fall back to full
    alignment response.

### Rotational Contribution

11. Rotational contribution drives opposite-direction hip positional
    compensation to mitigate unnatural neck bending.
12. Rotational compensation magnitude is configurable with non-negative
    clamp behaviour.

### Locomotion Permissions

13. The pose-state-machine implements ILocomotionPermissionSource and
    delegates to the active pose state.
14. StandingPoseState allows movement only when the standing/crouching
    blend is at or below a configurable threshold, defaulting to near fully
    upright.
15. Rotation is allowed in all cases for the standing family.

### Hip Offset Limits

16. Standing-family hip reconciliation supports configurable limits via
    an OffsetLimits resource type.
17. The standing family uses a continuum-aware envelope where the reference
    may shift forward and downward as crouch depth increases.
18. Near crouching and kneeling, lower body remains mostly animation-led.
19. Vertical clamping applies appropriate upper or lower bounds based on
    the current pose on the continuum.

## In Scope

- Standing-to-crouching continuum animation control via single AnimationTree
  state with continuous seek.
- StandingPoseState resource with lifecycle callbacks.
- HeadTrackingHipProfile with positional modulation, alignment damping, and
  rotational compensation.
- Configurable per-axis weights and alignment parameters.
- Locomotion permission output for standing poses.
- Configurable hip offset limits with continuum-aware envelope.
- Standing-to-kneeling and standing-to-all-fours transition trigger contracts.

## Out Of Scope

- Final numeric thresholds and curve tuning constants beyond defined
  configuration points.
- Per-state animation authoring details.
- Networked replication behaviour.
- Collision-based or locomotion-coupled hip correction beyond permission
  output.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-S-01 | Standing pose family uses a single AnimationTree state with continuous seek control. | Technical |
| AC-S-02 | StandingPoseState resource drives animation via lifecycle callbacks. | Technical |
| AC-S-03 | HeadTrackingHipProfile combines positional offset, alignment damping, and rotational offset. | Technical |
| AC-S-04 | Per-axis weights for vertical, lateral, and forward components are independently configurable. | Technical |
| AC-S-05 | Alignment damping uses a configurable minimum alignment weight to scale vertical response. | Technical |
| AC-S-06 | Locomotion permissions allow movement below a configurable pose-blend threshold. | User + Technical |
| AC-S-07 | Hip offset limits use a continuum-aware envelope for the standing family. | Technical |
| AC-S-08 | Players experience natural stoop and lean while preserving strong vertical crouch response. | User |
| AC-S-09 | Players are protected from extreme hip deformation via configurable limits. | User |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- @game/assets/characters/ik/vrik.tscn
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn