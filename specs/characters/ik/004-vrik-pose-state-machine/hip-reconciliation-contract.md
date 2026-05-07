# Hip Reconciliation Contract

## Requirement

Provide a state-dependent hip reconciliation contract that supports coherent
whole-body pose presentation while preserving prerequisite IK ownership.

## Goal

Specify how hip translation reconciliation integrates with pose states and
calibration semantics, without freezing tuning values.

## User Requirements

1. Hip positioning must remain visually coherent as players move between
   standing and non-standing poses.
2. Hip behaviour must not break expected feet behaviour for this phase.
3. Standing-family hip reconciliation must allow more natural stoop and lean
   poses by reducing hip travel perpendicular to hip rest up axis, preserving
   strong vertical crouch response.
4. Players must experience smooth transition between reduced perpendicular and
   full aligned response, with no abrupt discontinuities.
5. Standing-family reconciliation must keep strong vertical hip drop during
   crouch where head remains roughly above hips.
6. Standing-family reconciliation must avoid chasing the head down when head
   leads forward or backward; hips stay upright.
7. Players must be protected from extreme hip deformation beyond a configurable
   state-defined limit envelope, preventing unnatural body proportions.

## Technical Requirements

### Core Architecture

1. Hip reconciliation behaviour must be translation-centric for this phase.
2. Reconciliation logic must support state-dependent behaviour profiles so
   standing, kneeling, stooping, sitting, and crawling can apply distinct
   translation responses.
3. Calibration and reference calculations must use viewpoint-node semantics
   and body-proportion references.
4. Non-standing states must not depend on a standing head-rest pose assumption.
5. Hip reconciliation contracts must preserve IK-003 ownership where feet
   positions remain animation-driven source of truth.
6. Each pose state supplies a hip reconciliation profile that is activated
   together with the state's animation binding; the two must not be configured
   independently.

### Hip Offset Limits

7. Hip reconciliation profiles must support configurable limits on hip offsets
   from the state-defined reference baseline.
8. Limits are represented as normalised directional maxima in skeleton-local
   space, using head rest height as the normalisation basis.
9. A reusable Godot resource type `OffsetLimits3D` in a `Common` namespace
   must represent optional directional limits.
10. Each directional limit is optional; a limit side that is unconfigured or
    unmarked is treated as unbounded.
11. States may specify only the relevant bounds without requiring values for
    irrelevant directions.
12. States may use these limits to clamp the final hip offset from the
    state-defined reference.

### Standing Family Envelope

13. For StandingPoseState, the hip-limit reference and envelope are
    continuum-aware in all directions.
14. Standing vertical clamping is intentionally single-sided per envelope.
15. Only sides authored in both envelopes interpolate across the continuum.

### Head-Target Limiting

16. The system must also constrain head IK target motion earlier in the frame
    so uncompensated head motion does not deform the pose when hip motion has
    hit its limit.
17. Residual must be propagated into head-target limiting before IK solve.
18. Later XR-origin compensation absorbs the remaining mismatch between virtual
    head target and physical headset.

### Standing Family Default Profile

19. For the Standing pose family, the default profile is HeadTrackingHipProfile.
20. The profile must combine four contributions:
    - positional head offset
    - per-axis modulation
    - alignment-based vertical damping
    - rotational offset.
21. Per-axis modulation uses separate weights for vertical, lateral, and
    forward/back axes.
22. Alignment-based vertical damping applies an additional weight based on
    head direction alignment with the hip rest up axis.
23. Rotational contribution drives opposite-direction hip positional
    compensation to mitigate unnatural neck bending.

### Execution Order

24. Hip reconciliation runs inside a SkeletonModifier3D pass that executes
    after the animation player.
25. FootTargetSyncController must run before hip reconciliation; ordering is a
    scene or pipeline authoring contract.

### Profile Contract

26. Hip reconciliation profiles must return an absolute hip target position
    in skeleton-local space as a nullable value.
27. Returning null means do not override the animated hip bone.
28. Profiles must apply epsilon-based jitter suppression to avoid
    micro-translations.
29. Profiles must compute the hip target solely from pose-state-specific
    heuristics combined with the current head position and rig rest-pose
    geometry.
30. Profiles MUST NOT read or depend on the currently animated hip bone pose.

## In Scope

- State-dependent hip translation framework and architecture.
- Calibration and reference-space constraints tied to viewpoint and body
  proportions.
- Integration boundary with animation-driven feet ownership.
- Configurable hip offset hard limits via OffsetLimits3D resource.
- Head-target limiting to propagate residual hip offset when hip hits hard
  limit.
- XR-origin compensation for remaining virtual-to-physical mismatch.
- Standing-family default profile with positional modulation, alignment
  damping, and rotational compensation.
- Continuum-aware envelope for standing-family limits.
- Per-state profile child pages: StandingPoseState, KneelingPoseState,
  AllFoursPoseState.

## Out Of Scope

- Collision-based hip correction.
- Locomotion-coupled hip correction.
- Final numeric threshold values beyond defined configuration parameters.

## Acceptance Criteria

### Hip Reconciliation Scope

- **AC-HR-01** (Technical): The contract defines hip reconciliation as
  translation-centric and state-dependent for the MVP state set.
- **AC-HR-02** (User + Technical): Calibration and reference semantics are
  specified to use viewpoint-node and body-proportion signals, without
  standing-rest-pose dependence in non-standing states.
- **AC-HR-03** (User + Technical): Animation-driven lower-body bone transforms
  are the source of truth for this phase, and hip reconciliation must not
  override that ownership.

### Standing-Family Hip Behaviour

- **AC-HR-03a** (User): Standing-family hip reconciliation must allow more
  natural stoop and lean poses by reducing hip travel for motion perpendicular
  to the hip rest up axis while preserving strong vertical crouch response.
- **AC-HR-03b** (User): Standing-family reconciliation must keep strong
  vertical hip drop during crouch where head remains roughly above hips.
- **AC-HR-03c** (User): Standing-family reconciliation must avoid chasing the
  head down when the head leads forward or backward; hips should stay upright.

### Offset Limits

- **AC-HR-04** (Technical): Hip reconciliation profiles must support
  configurable limits on hip offsets from the state-defined reference
  baseline using an OffsetLimits3D resource type.
- **AC-HR-04d** (Technical): Standing vertical clamping is intentionally
  single-sided per envelope as specified.
- **AC-HR-04a** (Technical): The system must clamp the final hip offset from
  the state-defined reference, not just sub-contributions, when limits are
  exceeded.
- **AC-HR-04b** (Technical): When hip offset is limited, the residual must
  be propagated to head-target limiting before IK solve, with XR-origin
  compensation later absorbing remaining mismatch.
- **AC-HR-04c** (User): Players are protected from extreme hip deformation
  beyond configurable state-defined limits, preventing unnatural body
  proportions.

### Profile Binding and Execution

- **AC-HR-06** (Technical): Hip reconciliation profiles are bound to pose
  states and activated together with the state's animation selection.
- **AC-HR-07** (Technical): Hip reconciliation executes inside a
  SkeletonModifier3D pass ordered after the animation player.
  FootTargetSyncController must run before hip reconciliation.
- **AC-HR-08** (Technical): Hip reconciliation profiles return an absolute hip
  target position in skeleton-local space as a nullable value; returning null
  leaves the animated hip pose untouched. Profiles apply epsilon-based jitter
  suppression.

### Pose State Context Consumption

- **AC-HR-09** (Technical): Hip reconciliation consumes the per-tick
  PoseStateContext snapshot; the pending hip target may be produced by a
  driver node and applied inside a SkeletonModifier3D, provided AC-HR-07
  ordering is preserved.
- **AC-HR-10** (Technical): Hip reconciliation profiles compute the hip target
  solely from pose-state-specific heuristics plus the current head position
  and rig rest-pose geometry exposed through PoseStateContext, and MUST NOT
  read or depend on the currently animated hip bone pose.

### HeadTrackingHipProfile

- **AC-HR-11** (Technical): The Standing pose family's default profile is
  HeadTrackingHipProfile, combining positional head offset, per-axis
  positional modulation, alignment-based vertical damping, and rotational
  offset.

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
