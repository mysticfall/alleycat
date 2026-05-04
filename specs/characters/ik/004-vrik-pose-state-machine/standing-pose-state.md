---
id: IK-004-Standing
title: Standing Pose State
---

# Standing Pose State

## Requirement

Define the implementation contract for the Standing pose state, covering the standing-to-crouching continuum with continuous animation control and hip reconciliation profile binding.

## Goal

Provide detailed behaviour requirements for the standing pose family, including animation control, transition triggers, locomotion permissions, and the default hip reconciliation profile.

## User Requirements

1. Players must experience a continuous standing-to-crouching pose continuum driven by head position relative to the standing baseline.
2. Players must see coherent full-body pose behaviour spanning from upright standing through to full crouch.
3. The standing pose must allow natural stoop/lean poses while preserving strong vertical crouch response.
4. Players must have movement allowed only when near fully upright; crouching must restrict movement.
5. Players must retain rotation capability across the full standing-to-crouching continuum.
6. Players must be protected from extreme hip deformation beyond configurable state-defined limits.

## Technical Requirements

### Animation Control

1. The Standing pose family uses a single `AnimationTree` state, `StandingCrouching`, whose sub-graph continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")`.
2. The `StandingPoseState` resource maps to this `AnimationTree` state — there is no separate framework-level `CrouchingPoseState`.
3. The standing-to-crouching continuum is covered by one standing-family `PoseState` resource.
4. The `Idle` clip remains in the animation library for future layering but is not wired into the tree for MVP.

### Hip Reconciliation Profile

5. The default hip reconciliation profile for the standing family is `HeadTrackingHipProfile`.
6. The profile must combine four contributions:
   - Positional head offset
   - Per-axis positional modulation (up/down, side-to-side, forward/back)
   - Alignment-based vertical damping
   - Rotational offset derived from rest→current head orientation delta

### Per-Axis Positional Modulation

7. The profile decomposes head-position offset into three components in the hip rest local frame:

| Axis | Configurable Parameter | Authored Default |
|------|----------------------|------------------|
| Up/Down | `VerticalPositionWeight` | `1.0` (full offset) |
| Side-to-Side | `LateralPositionWeight` | `0.5` (50% offset) |
| Forward/Back | `ForwardPositionWeight` | `0.1` (10% offset) |

8. Motion aligned with the hip rest up axis (vertical crouch) retains full hip travel; side-to-side and forward/back components are attenuated.
9. All parameters are independently configurable and clamped to the `[0, 1]` range.

### Alignment-Based Vertical Damping

10. Alignment-based vertical damping applies an additional weight to the vertical component based on head direction alignment with the hip rest up axis.
11. The alignment computation uses head offset from rest as the basis:
    - `headOffsetLocal = currentHeadLocal - restHeadLocal`
    - `headDirection = normalise(headOffsetLocal)`
    - `alignment = |dot(headDirection, hipRestUpLocal)|`
    - `alignmentWeight = Mathf.Lerp(MinimumAlignmentWeight, 1.0, alignment)`
    - `verticalScaled = verticalComponent * VerticalPositionWeight * alignmentWeight`

12. The `MinimumAlignmentWeight` has an authored default of `0.1`, clamped to the `[0, 1]` range.
13. If either `headOffsetLocal` or `hipRestUpLocal` is near zero (degenerate case), fall back to `alignment = 1.0`.

### Rotational Contribution

14. Rotational contribution drives opposite-direction hip positional compensation to mitigate unnatural neck bending.
15. Rotational contribution magnitude is configurable via `RotationCompensationWeight` with non-negative clamp behaviour.

### Locomotion Permissions

16. The pose-state-machine implements `ILocomotionPermissionSource` and delegates to the active pose state.
17. `StandingPoseState` provides `GetLocomotionPermissions(PoseStateContext)`:
    - Allows movement only when the standing/crouching blend is at or below `MovementAllowedMaximumPoseBlend`.
    - Default threshold is `0.1` (near fully upright).
    - Allows rotation in all cases.

### Hip Offset Limits

18. Standing-family hip reconciliation must support configurable limits via `OffsetLimits3D` resource type.
19. The standing family uses a continuum-aware envelope:
    - Reference may shift forward/downward as crouch depth increases.
    - Near crouching/kneeling, lower body remains mostly animation-led.
20. Standing vertical clamping is intentionally single-sided per envelope:
    - Upright mode applies only the upright upper bound if authored.
    - Crouched mode applies only the crouched lower bound if authored.
    - Sides authored in both envelopes interpolate across the continuum.

### Limits Reference

21. The limit reference is defined per-pose-state through the `PoseState` contract.
22. For the standing family, the reference and envelope are continuum-aware in all directions:
    - reference may shift forward/downward as crouch depth increases
    - near crouching/kneeling, lower body remains mostly animation-led

## In Scope

- Standing-to-crouching continuum animation control via `StandingCrouching` AnimationTree state.
- `StandingPoseState` resource with lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`).
- `HeadTrackingHipProfile` with positional modulation, alignment damping, and rotational compensation.
- Configurable per-axis weights and alignment parameters.
- Locomotion permission output for standing poses.
- Configurable hip offset limits with continuum-aware envelope.
- Standing-to-kneeling and standing-to-all-fours transition trigger contracts.

## Out Of Scope

- Final numeric thresholds and curve tuning constants beyond defined configuration points.
- Per-state animation authoring details.
- Networked replication behaviour.
- Collision-based or locomotion-coupled hip correction beyond permission output.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-S-01 | Standing pose family uses single `StandingCrouching` AnimationTree state with continuous TimeSeek control. | Technical |
| AC-S-02 | `StandingPoseState` resource drives animation via lifecycle callbacks. | Technical |
| AC-S-03 | `HeadTrackingHipProfile` combines positional offset, per-axis modulation, alignment damping, and rotational compensation. | Technical |
| AC-S-04 | Per-axis weights (`VerticalPositionWeight`, `LateralPositionWeight`, `ForwardPositionWeight`) are independently configurable. | Technical |
| AC-S-05 | Alignment damping uses `MinimumAlignmentWeight` to scale vertical response based on head-hip alignment. | Technical |
| AC-S-06 | Locomotion permissions allow movement only when pose blend is below configurable threshold. | User + Technical |
| AC-S-07 | Hip offset limits use `OffsetLimits3D` with continuum-aware envelope for standing family. | Technical |
| AC-S-08 | Players experience natural stoop/lean while preserving strong vertical crouch response. | User |
| AC-S-09 | Players are protected from extreme hip deformation via configurable limits. | User |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- @game/assets/characters/ik/vrik.tscn
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn