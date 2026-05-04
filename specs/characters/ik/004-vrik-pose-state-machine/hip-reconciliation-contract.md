# Hip Reconciliation Contract

## Purpose

Define IK-004 hip reconciliation responsibilities and boundaries for the framework and standing-family shared contracts.

This contract covers cross-state reconciliation architecture, the standing-family default profile, and offset limiting. Per-state profile details are defined in dedicated child pages.

## Requirement

Provide a state-dependent hip reconciliation contract that supports coherent whole-body pose presentation while preserving prerequisite IK ownership boundaries.

## Goal

Specify how hip translation reconciliation integrates with pose states and calibration semantics, without freezing final tuning values.

## Specification Scope

This contract covers framework-level and standing-family contracts. Detailed per-state profiles are defined in dedicated child pages:

- [Standing Pose State](standing-pose-state.md) — covers standing-family profile details
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)

## User Requirements

1. Hip positioning must remain visually coherent as players move between standing and non-standing poses.
2. Hip behaviour must not break expected feet behaviour for this phase.
3. Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion perpendicular to the hip rest up axis, while preserving strong vertical crouch response.
4. Players must experience a smooth transition between reduced perpendicular response and full aligned response, without abrupt discontinuities.
5. Standing-family reconciliation must keep strong vertical hip drop during crouch where head remains roughly above hips.
6. Standing-family reconciliation must avoid chasing the head down when the head leads forward (stoop) or backward (lean-back); hips should stay upright.
7. Players must be protected from extreme hip deformation beyond a configurable state-defined limit envelope, preventing unnatural body proportions.

## Technical Requirements

### Core Architecture

1. Hip reconciliation behaviour must be translation-centric for this phase.
2. Reconciliation logic must support state-dependent behaviour profiles so standing, kneeling, stooping, sitting, and crawling can apply distinct translation responses.
3. Calibration and reference calculations must use viewpoint-node semantics and body-proportion references.
4. Non-standing states must not depend on a standing head-rest pose assumption.
5. Hip reconciliation contracts must preserve IK-003 ownership where feet positions remain animation-driven source of truth.
6. Each pose state supplies a hip reconciliation profile that is activated together with the state's animation binding; the two must not be configured independently.

### Hip Offset Limits

7. Hip reconciliation profiles must support configurable limits on hip offsets from the state-defined reference baseline.
8. Limits are represented as normalised directional maxima in skeleton-local space, using head rest height as the normalisation basis.
9. A reusable Godot resource type `OffsetLimits3D` in a `Common` namespace must represent optional directional limits (Up, Down, Left, Right, Forward, Back).
10. Each directional limit is optional; a limit side that is unconfigured or unmarked is treated as unbounded.
11. States may specify only the relevant bounds without requiring values for irrelevant directions.
12. Standing-family and other pose-specific hip reconciliation may use these limits to clamp the **final** hip offset from the state-defined reference, not just the positional sub-contribution.

### Standing Family Envelope

13. For `StandingPoseState`, the hip-limit reference and envelope are continuum-aware in all directions:
    - reference may shift forward/downward as crouch depth increases
    - for sides authored in both envelopes, the envelope may narrow as posture approaches crouching/kneeling
    - near crouching/kneeling, lower body remains mostly animation-led
14. Standing vertical clamping is intentionally single-sided per envelope:
    - when in upright mode, only the upright upper bound applies if authored
    - when in crouched mode, only the crouched lower bound applies if authored
    - a side authored in only one envelope remains active across the full continuum and stays anchored to that envelope's reference
    - only sides authored in both envelopes interpolate across the continuum

### Head-Target Limiting

15. The system must also constrain head IK target motion earlier in the frame so uncompensated head motion does not deform the pose when hip motion has hit its limit.
16. Residual (desired hip offset minus applied/clamped hip offset) must be propagated into head-target limiting before IK solve.
17. Later XR-origin compensation in `PlayerVRIK.OnEndStage` absorbs the remaining mismatch between virtual head target and physical headset.

### Standing Family Default Profile

18. For the Standing pose family (covering the standing-to-crouching continuum), the default profile is `HeadTrackingHipProfile`.
19. The profile must combine four contributions:
    - Positional head offset
    - Per-axis positional modulation
    - Alignment-based vertical damping
    - Rotational offset derived from rest→current head orientation delta

20. **Per-axis positional modulation** decomposes the head-position offset into three components in the hip rest local frame:
    - Up/Down: `VerticalPositionWeight` (default `1.0`)
    - Side-to-side: `LateralPositionWeight` (default `0.5`)
    - Forward/Back: `ForwardPositionWeight` (default `0.1`)

21. **Alignment-based vertical damping** applies an additional weight to the vertical component based on head direction alignment with the hip rest up axis:
    - Uses head offset from rest as basis
    - Computes `alignment = |dot(normalise(headDirection), hipRestUpLocal)|`
    - Applies `alignmentWeight = Mathf.Lerp(MinimumAlignmentWeight, 1.0, alignment)` where `MinimumAlignmentWeight` defaults to `0.1`
    - Degenerate case falls back to `alignment = 1.0`

22. **Rotational contribution** drives opposite-direction hip positional compensation to mitigate unnatural neck bending, configurable via `RotationCompensationWeight`.

### Execution Order

23. Hip reconciliation runs inside a `SkeletonModifier3D` pass that executes after the animation player; bone transforms at pass entry reflect the current animation sample.
24. FootTargetSyncController must run before hip reconciliation — ordering is a scene/pipeline authoring contract.

### Profile Contract

25. Hip reconciliation profiles must return an **absolute hip target position in skeleton-local space** as a nullable value (`Vector3?`).
26. Returning `null` means "do not override the animated hip bone" and the modifier must leave the animated hip pose untouched.
27. Profiles must apply epsilon-based jitter suppression to avoid micro-translations.
28. Hip reconciliation profiles must compute the hip target **solely** from pose-state-specific heuristics combined with the current head position and rig rest-pose geometry available through `PoseStateContext`.
29. Profiles MUST NOT read or depend on the currently animated hip bone pose.

## In Scope

- State-dependent hip translation framework and architecture.
- Calibration and reference-space constraints tied to viewpoint and body proportions.
- Integration boundary with animation-driven feet ownership.
- Configurable hip offset hard limits via `OffsetLimits3D` resource.
- Head-target limiting to propagate residual hip offset when hip hits hard limit.
- XR-origin compensation for remaining virtual-to-physical mismatch.
- Standing-family default profile (`HeadTrackingHipProfile`) with positional modulation, alignment damping, and rotational compensation.
- Continuum-aware envelope for standing-family limits.

## Out Of Scope

- Detailed per-state profile specifications (see dedicated child pages).
- Collision-based hip correction.
- Locomotion-coupled hip correction.
- Final numeric threshold values beyond defined configuration parameters.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-HR-01 | The contract defines hip reconciliation as translation-centric and state-dependent for the MVP state set. | Technical |
| AC-HR-02 | Calibration/reference semantics are specified to use viewpoint-node and body-proportion signals, without standing-rest-pose dependence in non-standing states. | User + Technical |
| AC-HR-03 | Animation-driven lower-body bone transforms (including feet) are the source of truth for this phase, and hip reconciliation must not override that ownership. | User + Technical |
| AC-HR-03a | Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion perpendicular to the hip rest up axis, while preserving strong vertical crouch response; players must experience smooth transition between reduced perpendicular response and full aligned response, without abrupt discontinuities. | User |
| AC-HR-03b | Standing-family reconciliation must keep strong vertical hip drop during crouch where head remains roughly above hips. | User |
| AC-HR-03c | Standing-family reconciliation must avoid chasing the head down when the head leads forward (stoop) or backward (lean-back); hips should stay upright. | User |
| AC-HR-04 | Hip reconciliation profiles must support configurable limits on hip offsets from the state-defined reference baseline using an `OffsetLimits3D` resource type representing optional directional limits. Each directional limit is optional; unconfigured or unmarked limits are treated as unbounded. | Technical |
| AC-HR-04d | Standing vertical clamping is intentionally single-sided per envelope as specified. | Technical |
| AC-HR-04a | The system must limit (clamp) the final hip offset from the state-defined reference, not just sub-contributions, when limits are exceeded. | Technical |
| AC-HR-04b | When hip offset is limited, the residual must be propagated to head-target limiting before IK solve, with XR-origin compensation later absorbing remaining mismatch. | Technical |
| AC-HR-04c | Players are protected from extreme hip deformation beyond configurable state-defined limits, preventing unnatural body proportions. | User |
| AC-HR-06 | Hip reconciliation profiles are bound to pose states and activated together with the state's animation selection. | Technical |
| AC-HR-07 | Hip reconciliation executes inside a `SkeletonModifier3D` pass ordered after the animation player. FootTargetSyncController must run before hip reconciliation. | Technical |
| AC-HR-08 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value; returning `null` leaves the animated hip pose untouched. Profiles apply epsilon-based jitter suppression. | Technical |
| AC-HR-09 | Hip reconciliation consumes the per-tick `PoseStateContext` snapshot; the pending hip target may be produced by a driver node and applied inside a `SkeletonModifier3D`, provided AC-HR-07 ordering is preserved. | Technical |
| AC-HR-10 | Hip reconciliation profiles compute the hip target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry exposed through `PoseStateContext`, and MUST NOT read or depend on the currently animated hip bone pose. | Technical |
| AC-HR-11 | The Standing pose family's default profile is `HeadTrackingHipProfile`, combining positional head offset, per-axis positional modulation, alignment-based vertical damping, and rotational offset with configurable parameters. | Technical |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)