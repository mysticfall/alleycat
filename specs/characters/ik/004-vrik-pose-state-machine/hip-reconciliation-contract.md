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

## Technical Requirements

1. Hip reconciliation behaviour must be translation-centric for this phase.
2. Reconciliation logic must support state-dependent behaviour profiles so standing (covering the standing-to-crouching continuum), kneeling, stooping,
   sitting, and crawling can apply distinct translation responses.
3. Calibration and reference calculations must use viewpoint-node semantics and body-proportion references.
4. Non-standing states must not depend on a standing head-rest pose assumption.
5. Hip reconciliation contracts must preserve IK-003 ownership where feet positions remain animation-driven source of
   truth.
6. Hip clamping behaviour is intentionally deferred and must not be a required delivery dependency for this phase.
7. The contract must be extensible to future clamp, collision, or locomotion integration without requiring structural
   redesign of state-dependent translation behaviour.
8. Hip reconciliation runs inside a `SkeletonModifier3D` pass that executes after the animation player; bone transforms
   at pass entry reflect the current animation sample.
9. Each pose state supplies a hip reconciliation profile that is activated together with the state's animation binding;
   the two must not be configured independently of pose state.
10. Lower-body bone transforms, including feet, remain animation-driven for this phase; hip reconciliation must not
    override that ownership.
11. Hip reconciliation profiles must return an **absolute hip target position in skeleton-local space** as a nullable
    value (`Vector3?`). Returning `null` means "do not override the animated hip bone" and the
    `HipReconciliationModifier`
    must leave the animated hip pose untouched for that tick. Returned positions are expressed in the skeleton's local
    basis at the time of evaluation, projected from world-space inputs as required. Profiles must apply epsilon-based
    jitter suppression to avoid micro-translations.
12. Hip reconciliation consumes the same per-tick `PoseStateContext` snapshot defined in the
    [Pose State Machine Contract](pose-state-machine-contract.md) (Technical Requirement #13). The pending hip target
    may be produced by a driver node (for example `PoseStateMachine.Tick`) and applied inside a
    `SkeletonModifier3D` (for example `HipReconciliationModifier`) so the modifier pass remains focused on writing the
    final hip pose. This cooperating-node split is permitted but not mandated, provided AC-HR-07 ordering is preserved.
13. Hip reconciliation profiles must compute the hip target **solely** from pose-state-specific heuristics combined with
    the current head position and the rig rest-pose geometry available through the `PoseStateContext`. Profiles MUST
    NOT read or depend on the currently animated hip bone pose. Rationale: `TimeSeek` scrubbing (or any animation)
    moves the animated hip, so depending on it creates a feedback loop between hip reconciliation and animation
    selection that destabilises classifier and transition behaviour.
14. For the Standing pose family (covering the standing-to-crouching continuum), the default profile is `HeadTrackingHipProfile`. The profile must combine
     three contributions: (a) positional head offset, (b) per-axis positional modulation, and (c) rotational offset derived
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

     The rotational contribution drives opposite-direction hip positional compensation to mitigate unnatural neck
     bending. Rotational contribution magnitude must be configurable per profile/resource via `RotationCompensationWeight`,
     with non-negative clamp behaviour (`max(0, weight)`) and no mandatory fixed default value in this spec revision.
     Vertical hip movement for this family is owned by the hip profile, not by the animation clip.
16. Unit-level regression tests must cover:
      (a) the rotational-compensation contract for `HeadTrackingHipProfile` (sign correctness, proportional scaling by weight,
          non-negative clamp behaviour, epsilon-combined snap behaviour, and overload equivalence); and
      (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight application
          correctness, up/down full-response preservation, side-to-side partial-response verification at the authored default,
          forward/back minimal-response verification at the authored default, diagonal/interpolated response continuity,
          and +up/-up symmetry).

## In Scope

- State-dependent hip translation responsibilities.
- Calibration and reference-space constraints tied to viewpoint and body proportions.
- Integration boundary with animation-driven feet ownership.

## Out Of Scope

- Mandatory clamp systems for hips.
- Numeric clamp limits or translation thresholds.
- Collision-based hip correction.
- Locomotion-coupled hip correction.

## Acceptance Criteria

| ID       | Requirement                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | Layer            |
|----------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------|
| AC-HR-01 | The contract defines hip reconciliation as translation-centric and state-dependent for the MVP state set.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | Technical        |
| AC-HR-02 | Calibration/reference semantics are specified to use viewpoint-node and body-proportion signals, without standing-rest-pose dependence in non-standing states.                                                                                                                                                                                                                                                                                                  | User + Technical |
| AC-HR-03 | Animation-driven lower-body bone transforms (including feet) are the source of truth for this phase, and hip reconciliation must not override that ownership.                                                                                                                                                                                                                                                                                                   | User + Technical |
| AC-HR-03a | Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion perpendicular to the hip rest up axis, while preserving strong vertical crouch response; players must experience smooth transition between reduced perpendicular response and full aligned response, without abrupt discontinuities.                                                                                                                                                                                                                                                            | User |
| AC-HR-04 | Hip clamping is explicitly deferred and not required to satisfy IK-004 delivery.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            | Technical        |
| AC-HR-05 | The contract leaves tuning values open while defining architecture and integration boundaries needed for implementation.                                                                                                                                                                                                                                                                                                                                        | Technical        |
| AC-HR-06 | Hip reconciliation profiles are bound to pose states and activated together with the state's animation selection.                                                                                                                                                                                                                                                                                                                                               | Technical        |
| AC-HR-07 | Hip reconciliation executes inside a `SkeletonModifier3D` pass ordered after the animation player so bone transforms at pass entry reflect the current animation sample. FootTargetSyncController must run before hip reconciliation — ordering is a scene/pipeline authoring contract.                                                                                                                                                                 | Technical        |
| AC-HR-08 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value (`Vector3?`); returning `null` leaves the animated hip pose untouched. Profiles apply epsilon-based jitter suppression to avoid micro-translations.                                                                                                                                                                                              | Technical        |
| AC-HR-09 | Hip reconciliation consumes the per-tick `PoseStateContext` snapshot; the pending hip target may be produced by a driver node and applied inside a `SkeletonModifier3D`, provided AC-HR-07 ordering is preserved.                                                                                                                                                                                                                                                     | Technical        |
| AC-HR-10 | Hip reconciliation profiles compute the hip target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry exposed through `PoseStateContext`, and MUST NOT read or depend on the currently animated hip bone pose, so that `TimeSeek` scrubbing and animation changes cannot feed back into hip reconciliation.                                                                                             | Technical        |
| AC-HR-11 | The Standing pose family's default profile is `HeadTrackingHipProfile`, combining (a) positional head offset, (b) per-axis positional modulation decomposed into up/down, side-to-side, and forward/back components in the hip rest local frame, and (c) rotational offset (derived from rest→current head orientation delta plus rest neck→head geometry) with opposite-direction hip compensation to mitigate unnatural neck bending. Per-axis weights are independently configurable: `VerticalPositionWeight` (default `1.0`, full offset), `LateralPositionWeight` (default `0.5`, 50 % offset), and `ForwardPositionWeight` (default `0.1`, 10 % offset). The rest up axis is derived from the hip bone's rest-pose or global-rest basis in skeleton-local space. The combined weights produce strong vertical crouch response and reduced lateral/forward-back hip travel, preserving +up/-up symmetry. All three parameters are clamped to the `[0, 1]` range and independently configurable; the defaults above represent the authored standing profile and may be overridden in derived resources. Rotational contribution magnitude is configurable via `RotationCompensationWeight`, clamped to non-negative values, with no mandatory fixed default in this spec. | Technical |
| AC-HR-12 | Unit-level regression tests cover both: (a) the rotational-compensation contract for `HeadTrackingHipProfile` (sign correctness, proportional weight scaling, non-negative weight clamp behaviour, epsilon-combined snap behaviour, and overload equivalence); and (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight application correctness, up/down full-response preservation, side-to-side partial-response verification at the authored default, forward/back minimal-response verification at the authored default, diagonal/interpolated response continuity, and +up/-up symmetry). | Technical |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md) — see Technical Requirements #7 for the
  motion-sickness and `AnimationNodeBlend2D` rationale behind the linear-clip + `TimeSeek` default.
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
