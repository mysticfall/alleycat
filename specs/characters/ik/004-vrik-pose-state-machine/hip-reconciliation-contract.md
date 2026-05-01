# Hip Reconciliation Contract

## Purpose

Define IK-004 hip reconciliation responsibilities and boundaries for MVP pose states.

## Requirement

Provide a state-dependent hip reconciliation contract that supports coherent whole-body pose presentation while
preserving prerequisite IK ownership boundaries.

## Goal

Specify how hip translation reconciliation integrates with pose states and calibration semantics, without freezing
final tuning values.

## User Requirements

1. Hip positioning must remain visually coherent as players move between standing and non-standing poses.
2. Hip behaviour must not break expected feet behaviour for this phase.
3. Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion
   perpendicular to the hip rest up axis, while preserving strong vertical crouch response.
4. Players must experience a smooth transition between reduced perpendicular response and full aligned response,
   without abrupt discontinuities.
5. Standing-family reconciliation must keep strong vertical hip drop during crouch where head remains roughly above hips.
6. Standing-family reconciliation must avoid chasing the head down when the head leads forward (stoop) or backward (lean-back);
   hips should stay upright.
7. Players must be protected from extreme hip deformation beyond a configurable state-defined limit envelope,
    preventing unnatural body proportions when head motion exceeds what hip reconciliation can safely compensate.
    The limit reference is defined per-pose-state; the Standing family defines a continuum-aware envelope.

## Technical Requirements

1. Hip reconciliation behaviour must be translation-centric for this phase.
2. Reconciliation logic must support state-dependent behaviour profiles so standing (covering the standing-to-crouching continuum), kneeling, stooping,
   sitting, and crawling can apply distinct translation responses.
3. Calibration and reference calculations must use viewpoint-node semantics and body-proportion references.
4. Non-standing states must not depend on a standing head-rest pose assumption.
5. Hip reconciliation contracts must preserve IK-003 ownership where feet positions remain animation-driven source of
   truth.
6. Hip reconciliation profiles must support configurable limits on hip offsets from the state-defined reference baseline.
    Limits are represented as normalised directional maxima in skeleton-local space, using head rest height as the normalisation basis.
 7. The limit reference is defined per-pose-state through the `PoseState` contract. Each `PoseState` resource
    (or equivalent state-owned contract) resolves the per-tick hip-limit reference semantics for its associated
    hip reconciliation profile. For the Standing family, the reference and envelope are continuum-aware.
8. A reusable Godot resource type `OffsetLimits3D` in a `Common` namespace must represent optional directional limits
     (Up, Down, Left, Right, Forward, Back) for configurable hip offset constraints. Each directional limit is optional;
     a limit side that is unconfigured or unmarked is treated as unbounded (no clamp applied). This allows states
     to specify only the relevant bounds without requiring values for irrelevant directions.
 9. Standing-family and other pose-specific hip reconciliation may use these limits to clamp the **final** hip offset
    from the state-defined reference, not just the positional sub-contribution.
10. For `StandingPoseState`, the hip-limit reference and envelope are continuum-aware in all directions:
     - reference may shift forward/downward as crouch depth increases
     - for sides authored in both envelopes, the envelope may narrow as posture approaches crouching/kneeling; however, single-sided authored bounds remain anchored to their authored reference and do not synthesise the opposite side
     - near crouching/kneeling, lower body remains mostly animation-led with only slight head-driven adjustment
10a. Standing vertical clamping is intentionally single-sided per envelope: when in upright mode, only the upright upper bound
      (`UprightHipOffsetLimits.Up`) applies if authored — the `UprightHipOffsetLimits.Down` side is not synthesised if unconfigured
      and must not impose synthetic clamping. When in crouched mode, only the crouched lower bound (`CrouchedHipOffsetLimits.Down`)
      applies if authored — the `CrouchedHipOffsetLimits.Up` side is not synthesised if unconfigured and must not impose synthetic
      clamping. A side that is authored in only one envelope remains active across the full standing-to-crouching continuum
      and stays anchored to that envelope's reference (upright uses the upright/rest reference, crouched uses the full-crouch reference).
      Only sides authored in both envelopes interpolate across the continuum. Vertical clamping occurs only when exceeding the relevant
      bound for the current continuum position.
11. The system must also constrain head IK target motion earlier in the frame so uncompensated head motion does not deform the pose
    when hip motion has hit its limit.
12. Residual (desired hip offset minus applied/clamped hip offset) must be propagated into head-target limiting before IK solve,
     with later XR-origin compensation in `PlayerVRIK.OnEndStage` absorbing the remaining mismatch between virtual head target and physical headset.
13. Existing directional weights (`LateralPositionWeight`, `ForwardPositionWeight`, etc.) still participate; the architecture must support
     cancelling those weights from the residual when deriving the head-target clamp.
14. The baseline for the limited hip offset is the state-defined reference for the current pose/tick, with configured limits
     chosen heuristically.
15. Hip reconciliation runs inside a `SkeletonModifier3D` pass that executes after the animation player; bone transforms
    at pass entry reflect the current animation sample.
16. Each pose state supplies a hip reconciliation profile that is activated together with the state's animation binding;
    the two must not be configured independently of pose state.
17. Lower-body bone transforms, including feet, remain animation-driven for this phase; hip reconciliation must not
    override that ownership.
18. Hip reconciliation profiles must return an **absolute hip target position in skeleton-local space** as a nullable
    value (`Vector3?`). Returning `null` means "do not override the animated hip bone" and the
    `HipReconciliationModifier`
    must leave the animated hip pose untouched for that tick. Returned positions are expressed in the skeleton's local
    basis at the time of evaluation, projected from world-space inputs as required. Profiles must apply epsilon-based
    jitter suppression to avoid micro-translations.
19. Hip reconciliation consumes the same per-tick `PoseStateContext` snapshot defined in the
    [Pose State Machine Contract](pose-state-machine-contract.md) (Technical Requirement #13). The pending hip target
    may be produced by a driver node (for example `PoseStateMachine.Tick`) and applied inside a
    `SkeletonModifier3D` (for example `HipReconciliationModifier`) so the modifier pass remains focused on writing the
    final hip pose. This cooperating-node split is permitted but not mandated, provided AC-HR-07 ordering is preserved.
20. Hip reconciliation profiles must compute the hip target **solely** from pose-state-specific heuristics combined with
    the current head position and the rig rest-pose geometry available through the `PoseStateContext`. Profiles MUST
    NOT read or depend on the currently animated hip bone pose. Rationale: `TimeSeek` scrubbing (or any animation)
    moves the animated hip, so depending on it creates a feedback loop between hip reconciliation and animation
    selection that destabilises classifier and transition behaviour.
21. For the Standing pose family (covering the standing-to-crouching continuum), the default profile is `HeadTrackingHipProfile`. The profile must combine
     four contributions: (a) positional head offset, (b) per-axis positional modulation, (c) alignment-based vertical damping, and (d) rotational offset derived
     from rest→current head orientation delta using rest neck→head geometry.

     **Per-axis positional modulation** decomposes the head-position offset into three components in the hip rest
     local frame: up/down, side-to-side, and forward/back. Each axis applies its own configurable scalar weight:

| Axis         | Configurable parameter                | Authored standing-profile default |
|--------------|---------------------------------------|------------------------------------|
| Up / Down    | `VerticalPositionWeight`         | `1.0` (full offset)                |
| Side-to-Side | `LateralPositionWeight`         | `0.5` (50 % offset)                |
| Forward/Back | `ForwardPositionWeight`         | `0.1` (10 % offset)                |

     The rest up axis is derived from the hip bone's rest-pose or global-rest basis in skeleton-local space.
     Motion aligned with the rest up axis (vertical crouch) retains full hip travel; side-to-side and forward/back
     components are attenuated by their respective weights. Together, these weights produce stronger vertical
     crouch response and reduced lateral/forward-back hip travel, enabling natural stooping and leaning without
     abrupt discontinuities. The weighting preserves symmetry for +up and -up directions along the hip rest up axis.
     All three parameters are independently configurable, clamped to the `[0, 1]` range, and carry no mandatory
     fixed defaults in this spec revision beyond the standing-profile authored defaults listed above. Other pose
     families MAY diverge by supplying a different profile.

**Alignment-based vertical damping** applies an additional weight to the vertical component based on how aligned
      the head direction is with the hip rest up axis. This dampens the vertical hip response when the head is tilted
      forward (stoop) or backward (lean-back) while preserving full vertical response during pure vertical crouch.
      The alignment computation uses **head offset from rest** as the basis, not the absolute current hip-to-head position:

      - `headOffsetLocal = currentHeadLocal - restHeadLocal` — current head position offset from rest in skeleton-local space
      - `headDirection = normalise(headOffsetLocal)` — unit vector of the head offset from rest
      - `alignment = |dot(headDirection, hipRestUpLocal)|` — absolute alignment with the hip rest up axis
      - `alignmentWeight = Mathf.Lerp(MinimumAlignmentWeight, 1.0, alignment)` — interpolated weight
      - `verticalScaled = verticalComponent * VerticalPositionWeight * alignmentWeight`

      The `MinimumAlignmentWeight` is a configurable parameter with an authored default of `0.1`, clamped to the `[0, 1]`
      range. This provides extra damping on the vertical hip response when the head leads forward (stoop) or backward
      (lean-back), so the hips do not chase the head down. During pure vertical crouch (alignment ≈ 1), the full
      vertical response is preserved. The weighting uses `Mathf.Abs` so both +up and -up directions are damped
      symmetrically. If either `headOffsetLocal` produces a near-zero `headDirection` or `hipRestUpLocal` is near zero
      (degenerate case), the implementation must fall back to `alignment = 1.0` to avoid division-by-zero or unstable
      results.

     The rotational contribution drives opposite-direction hip positional compensation to mitigate unnatural neck
     bending. Rotational contribution magnitude must be configurable per profile/resource via `RotationCompensationWeight`,
     with non-negative clamp behaviour (`max(0, weight)`) and no mandatory fixed default value in this spec revision.
     Vertical hip movement for this family is owned by the hip profile, not by the animation clip.
22. Unit-level regression tests must cover:
      (a) the rotational-compensation contract for `HeadTrackingHipProfile` (sign correctness, proportional scaling by weight,
          non-negative clamp behaviour, epsilon-combined snap behaviour, and overload equivalence); and
      (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight application
          correctness, up/down full-response preservation, side-to-side partial-response verification at the authored default,
          forward/back minimal-response verification at the authored default, diagonal/interpolated response continuity,
          and +up/-up symmetry).
23. Unit-level regression tests must also cover the hip-offset limiting contract: configurable `OffsetLimits3D` limits
    are applied to the final hip offset from state-defined reference, residual computation correctly propagates clamped-vs-desired difference into head-target limiting,
    head-target limiting prevents pose deformation when hip is at limit, and XR-origin compensation absorbs the remaining
    virtual-vs-physical mismatch.

## In Scope

- State-dependent hip translation responsibilities.
- Calibration and reference-space constraints tied to viewpoint and body proportions.
- Integration boundary with animation-driven feet ownership.
- Configurable hip offset hard limits via `OffsetLimits3D` resource.
- Head-target limiting to propagate residual hip offset when hip hits hard limit.
- XR-origin compensation for remaining virtual-to-physical mismatch.

## Out Of Scope

- Collision-based hip correction.
- Locomotion-coupled hip correction.

## Acceptance Criteria

| ID       | Requirement                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | Layer            |
|----------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------|
| AC-HR-01 | The contract defines hip reconciliation as translation-centric and state-dependent for the MVP state set.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | Technical        |
| AC-HR-02 | Calibration/reference semantics are specified to use viewpoint-node and body-proportion signals, without standing-rest-pose dependence in non-standing states.                                                                                                                                                                                                                                                                                                  | User + Technical |
| AC-HR-03 | Animation-driven lower-body bone transforms (including feet) are the source of truth for this phase, and hip reconciliation must not override that ownership.                                                                                                                                                                                                                                                                                                   | User + Technical |
| AC-HR-03a | Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion perpendicular to the hip rest up axis, while preserving strong vertical crouch response; players must experience smooth transition between reduced perpendicular response and full aligned response, without abrupt discontinuities. | User |
| AC-HR-03b | Standing-family reconciliation must keep strong vertical hip drop during crouch where head remains roughly above hips. | User |
| AC-HR-03c | Standing-family reconciliation must avoid chasing the head down when the head leads forward (stoop) or backward (lean-back); hips should stay upright. | User |
| AC-HR-04 | Hip reconciliation profiles must support configurable limits on hip offsets from the state-defined reference baseline using an `OffsetLimits3D` resource type representing optional directional limits. Each directional limit is optional; unconfigured or unmarked limits are treated as unbounded. | Technical |
| AC-HR-04d | Standing vertical clamping is intentionally single-sided per envelope: when in upright mode, only the upright upper bound (`UprightHipOffsetLimits.Up`) applies if authored — the `UprightHipOffsetLimits.Down` side is not synthesised if unconfigured and must not impose synthetic clamping. When in crouched mode, only the crouched lower bound (`CrouchedHipOffsetLimits.Down`) applies if authored — the `CrouchedHipOffsetLimits.Up` side is not synthesised if unconfigured and must not impose synthetic clamping. A side that is authored in only one envelope remains active across the full standing-to-crouching continuum and stays anchored to that envelope's reference (upright uses the upright/rest reference, crouched uses the full-crouch reference). Only sides authored in both envelopes interpolate across the continuum. Vertical clamping occurs only when exceeding the relevant bound for the current continuum position. | Technical |
| AC-HR-04a | The system must limit (clamp) the final hip offset from the state-defined reference, not just sub-contributions, when limits are exceeded. | Technical |
| AC-HR-04b | When hip offset is limited, the residual (desired minus applied) must be propagated to head-target limiting before IK solve, with XR-origin compensation later absorbing remaining mismatch. | Technical |
| AC-HR-04c | Players are protected from extreme hip deformation beyond configurable state-defined limits, preventing unnatural body proportions. | User |
| AC-HR-05 | The contract leaves tuning values open while defining architecture and integration boundaries needed for implementation.                                                                                                                                                                                                                                                                                                                                        | Technical        |
| AC-HR-06 | Hip reconciliation profiles are bound to pose states and activated together with the state's animation selection.                                                                                                                                                                                                                                                                                                                                               | Technical        |
| AC-HR-07 | Hip reconciliation executes inside a `SkeletonModifier3D` pass ordered after the animation player so bone transforms at pass entry reflect the current animation sample. FootTargetSyncController must run before hip reconciliation — ordering is a scene/pipeline authoring contract.                                                                                                                                                                 | Technical        |
| AC-HR-08 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value (`Vector3?`); returning `null` leaves the animated hip pose untouched. Profiles apply epsilon-based jitter suppression to avoid micro-translations.                                                                                                                                                                                              | Technical        |
| AC-HR-09 | Hip reconciliation consumes the per-tick `PoseStateContext` snapshot; the pending hip target may be produced by a driver node and applied inside a `SkeletonModifier3D`, provided AC-HR-07 ordering is preserved.                                                                                                                                                                                                                                                     | Technical        |
| AC-HR-10 | Hip reconciliation profiles compute the hip target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry exposed through `PoseStateContext`, and MUST NOT read or depend on the currently animated hip bone pose, so that `TimeSeek` scrubbing and animation changes cannot feed back into hip reconciliation.                                                                                             | Technical        |
| AC-HR-11 | The Standing pose family's default profile is `HeadTrackingHipProfile`, combining (a) positional head offset, (b) per-axis positional modulation decomposed into up/down, side-to-side, and forward/back components in the hip rest local frame, (c) alignment-based vertical damping applied to the vertical component, and (d) rotational offset (derived from rest→current head orientation delta plus rest neck→head geometry) with opposite-direction hip compensation to mitigate unnatural neck bending. Per-axis weights are independently configurable: `VerticalPositionWeight` (default `1.0`, full offset), `LateralPositionWeight` (default `0.5`, 50 % offset), and `ForwardPositionWeight` (default `0.1`, 10 % offset). The alignment-based vertical damping applies a configurable `MinimumAlignmentWeight` (default `0.1`, clamped to `[0,1]`) that scales the vertical component when the head is not vertically aligned with the hips: `alignment = |dot(normalise(headDirection), hipRestUpLocal)|` and `verticalScaled = verticalComponent * VerticalPositionWeight * Mathf.Lerp(MinimumAlignmentWeight, 1.0, alignment)`. Pure vertical crouch (alignment ≈ 1) preserves full vertical response; forward lean or backward lean reduces vertical hip travel to avoid chasing the head down. The alignment uses `Mathf.Abs` for +up/-up symmetry. Degenerate case: if headDirection or hipRestUpLocal is near zero, fall back to alignment = 1.0. The rest up axis is derived from the hip bone's rest-pose or global-rest basis in skeleton-local space. The combined weights produce strong vertical crouch response and reduced lateral/forward-back hip travel, preserving +up/-up symmetry. All parameters are clamped to the `[0, 1]` range and independently configurable; the defaults above represent the authored standing profile and may be overridden in derived resources. Rotational contribution magnitude is configurable via `RotationCompensationWeight`, clamped to non-negative values, with no mandatory fixed default in this spec. | Technical |
| AC-HR-12 | Unit-level regression tests cover both: (a) the rotational-compensation contract for `HeadTrackingHipProfile` (sign correctness, proportional weight scaling, non-negative weight clamp behaviour, epsilon-combined snap behaviour, and overload equivalence); and (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight application correctness, up/down full-response preservation, side-to-side partial-response verification at the authored default, forward/back minimal-response verification at the authored default, diagonal/interpolated response continuity, and +up/-up symmetry). | Technical |
| AC-HR-12b | Unit-level regression tests cover the alignment-damping contract: high-alignment (pure vertical crouch) preserves full vertical response, low-alignment (forward stoop or lean-back) applies vertical damping down to `MinimumAlignmentWeight`, `MinimumAlignmentWeight` is clamped to `[0,1]`, diagonal offsets preserve per-axis weighting alongside alignment damping, and +up/-up symmetry under alignment damping is verified. | Technical |
| AC-HR-12c | Unit-level regression tests cover the hip-offset limiting contract: configurable `OffsetLimits3D` limits are applied to the final hip offset from state-defined reference, residual computation correctly propagates clamped-vs-desired difference into head-target limiting, head-target limiting prevents pose deformation when hip is at limit, and XR-origin compensation absorbs the remaining virtual-vs-physical mismatch. | Technical |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md) — see Technical Requirements #7 for the
  motion-sickness and `AnimationNodeBlend2D` rationale behind the linear-clip + `TimeSeek` default.
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
