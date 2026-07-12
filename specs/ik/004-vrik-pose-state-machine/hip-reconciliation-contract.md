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
   poses by reducing horizontal hip correction while preserving strong vertical
   crouch response.
4. Players must experience smooth transition between reduced horizontal and
   full vertical response, with no abrupt discontinuities.
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
7. Each pose state also owns hip translation authority for the active frame.
   Authority is resolved through the active `PoseState` and passed through the
   pose-state-machine to hip reconciliation.

### Hip Offset Limits

8. Hip reconciliation profiles must support configurable limits on hip offsets
   from the state-defined reference baseline.
9. Limits are represented as normalised directional maxima in the same
   avatar-semantic skeleton-local frame used by hip translation authority.
   Reference or rest geometry may provide normalisation inputs, but limit
   application must not require replacing the sampled animated pose as the
   blend origin.
10. A reusable Godot resource type `OffsetLimits3D` in a `Common` namespace
    must represent optional directional limits.
11. Each directional limit is optional; a limit side that is unconfigured or
    unmarked is treated as unbounded.
12. States may specify only the relevant bounds without requiring values for
    irrelevant directions.
13. States may use these limits to clamp the final hip offset from the
    state-defined reference.

### Standing Family Envelope

14. For StandingPoseState, the hip-limit reference and envelope are
    continuum-aware in all directions.
15. Standing vertical clamping is intentionally single-sided per envelope.
16. Only sides authored in both envelopes interpolate across the continuum.

### Head-Target Limiting

17. The system must also constrain head IK target motion earlier in the frame
    so uncompensated head motion does not deform the pose when hip motion has
    hit its limit.
18. Residual must be propagated into head-target limiting before IK solve.
19. Later XR-origin compensation absorbs the remaining mismatch between virtual
    head target and physical headset.

### Standing Family Default Profile

20. For the Standing pose family, the default profile is HeadTrackingHipProfile.
21. The profile must combine four contributions:
    - positional head offset
    - per-axis modulation
    - alignment-based vertical damping
    - rotational offset.
22. Per-axis modulation uses separate weights for vertical, lateral, and
    forward/back axes.
23. Alignment-based vertical damping applies an additional weight based on
    head direction alignment with the avatar-semantic vertical axis.
24. Rotational contribution drives opposite-direction hip positional
    compensation to mitigate unnatural neck bending.

### Hip Translation Authority

25. Hip translation authority is pose-state-owned, not locomotion-owned.
26. `HipTranslationAuthority` expresses independent `Lateral`, `Vertical`, and
    `Forward` authority values in the 0 to 1 range.
27. Authority axes use the same avatar-semantic skeleton-local frame used by
    hip limits.
28. `PoseState.ResolveHipTranslationAuthority(PoseStateContext)` defaults to
    full authority for compatibility with states that do not override it.
29. `PoseStateMachine` must resolve the active state's authority each tick and
    carry it to `HipReconciliationModifier` with the active profile output.
30. `HipReconciliationModifier` must sample the animated hip pose at modifier
    entry, then blend each semantic axis from that animated pose toward the
    reconciled and clamped target using the resolved authority value.
31. Authority affects translation only. Actual hip bone rotation control is a
    deferred contract; existing rotation-derived compensation remains a
    positional contribution to the translation target.

### Standing Family Authority Defaults

32. `StandingPoseState` owns exported defaults for reduced horizontal authority
    and full vertical authority.
33. Standing defaults must preserve walk-clip horizontal hip motion while
    retaining vertical crouch response.
34. Standing hip translation authority must not depend on locomotion state,
    locomotion permission, or locomotion-system signals.

### Execution Order

35. Hip reconciliation runs inside a SkeletonModifier3D pass that executes
    after the animation player.
36. FootTargetSyncController must run before hip reconciliation; ordering is a
    scene or pipeline authoring contract.

### Profile Contract

37. Hip reconciliation profiles must return an absolute hip target position
    in skeleton-local space as a nullable value.
38. Returning null means do not override the animated hip bone.
39. Profiles must apply epsilon-based jitter suppression to avoid
    micro-translations.
40. Profiles must compute the hip target solely from pose-state-specific
    heuristics combined with the current head position and rig rest-pose
    geometry.
41. Profiles MUST NOT read or depend on the currently animated hip bone pose;
    only the modifier-stage authority blend samples the animated hip pose.

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
- Pose-state-owned hip translation authority with per-axis semantic blending.
- Continuum-aware envelope for standing-family limits.
- Per-state profile child pages: StandingPoseState, KneelingPoseState,
  AllFoursPoseState.

## Out Of Scope

- Collision-based hip correction.
- Locomotion-coupled hip correction.
- Final numeric threshold values beyond defined configuration parameters.
- Actual hip bone rotation control; existing rotation-derived compensation is
  translation-only positional compensation for this phase.

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
  natural stoop and lean poses by reducing horizontal hip correction while
  preserving strong vertical crouch response.
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
- **AC-HR-08a** (Technical): The active pose state owns hip translation
  authority, resolves `Lateral`, `Vertical`, and `Forward` values in the
  semantic skeleton-local frame, and passes them through `PoseStateMachine` to
  `HipReconciliationModifier`.
- **AC-HR-08b** (Technical): `PoseState.ResolveHipTranslationAuthority` defaults
  to full authority when a state does not override it.
- **AC-HR-08c** (User + Technical): Standing defaults reduce horizontal
  authority and retain full vertical authority without depending on locomotion,
  preserving walk-clip horizontal hip motion and vertical crouch response.
- **AC-HR-08d** (Technical): `HipReconciliationModifier` samples the animated
  hip pose at modifier entry and blends each semantic axis from that pose toward
  the reconciled and clamped target by the resolved authority.
- **AC-HR-08e** (Technical): Hip reconciliation remains translation-only;
  actual hip bone rotation control is deferred, and rotation-derived
  compensation remains positional.

### Pose State Context Consumption

- **AC-HR-09** (Technical): Hip reconciliation consumes the per-tick
  PoseStateContext snapshot; the pending hip target may be produced by a
  driver node and applied inside a SkeletonModifier3D, provided AC-HR-07
  ordering is preserved.
- **AC-HR-10** (Technical): Hip reconciliation profiles compute the hip target
  solely from pose-state-specific heuristics plus the current head position
  and rig rest-pose geometry exposed through PoseStateContext, and MUST NOT
  read or depend on the currently animated hip bone pose. Modifier-stage
  authority blending is the only contract that samples the animated hip pose.

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
