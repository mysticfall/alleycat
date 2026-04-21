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

## Technical Requirements

1. Hip reconciliation behaviour must be translation-centric for this phase.
2. Reconciliation logic must support state-dependent behaviour profiles so standing, crouching, kneeling, stooping,
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
14. For the Standing/Crouching pose family, the default profile is `HeadTrackingHipProfile`. The profile must combine
    two contributions: (a) positional head offset and (b) rotational offset derived from rest→current head orientation
    delta using rest neck→head geometry. The resulting rotational contribution must drive opposite-direction hip
    positional compensation to mitigate unnatural neck bending. Rotational contribution magnitude must be configurable
    per profile/resource via `RotationCompensationWeight`, with non-negative clamp behaviour (`max(0, weight)`) and no
    mandatory fixed default value in this spec revision. This supersedes any earlier "lateral-only" framing for this
    family; vertical hip movement is owned by the hip profile, not by the animation clip. Other pose families MAY
    diverge by supplying a different profile.
15. Unit-level regression tests must cover the weighted rotational-compensation contract for
    `HeadTrackingHipProfile`, including sign correctness, proportional scaling by weight, non-negative clamp behaviour,
    epsilon-combined snap behaviour, and overload equivalence for profile evaluation entry points.

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

| ID       | Requirement                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      | Layer            |
|----------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------|
| AC-HR-01 | The contract defines hip reconciliation as translation-centric and state-dependent for the MVP state set.                                                                                                                                                                                                                                                                                                                                                                                        | Technical        |
| AC-HR-02 | Calibration/reference semantics are specified to use viewpoint-node and body-proportion signals, without standing-rest-pose dependence in non-standing states.                                                                                                                                                                                                                                                                                                                                   | User + Technical |
| AC-HR-03 | Animation-driven lower-body bone transforms (including feet) are the source of truth for this phase, and hip reconciliation must not override that ownership.                                                                                                                                                                                                                                                                                                                                    | User + Technical |
| AC-HR-04 | Hip clamping is explicitly deferred and not required to satisfy IK-004 delivery.                                                                                                                                                                                                                                                                                                                                                                                                                 | Technical        |
| AC-HR-05 | The contract leaves tuning values open while defining architecture and integration boundaries needed for implementation.                                                                                                                                                                                                                                                                                                                                                                         | Technical        |
| AC-HR-06 | Hip reconciliation profiles are bound to pose states and activated together with the state's animation selection.                                                                                                                                                                                                                                                                                                                                                                                | Technical        |
| AC-HR-07 | Hip reconciliation executes inside a `SkeletonModifier3D` pass ordered after the animation player so bone transforms at pass entry reflect the current animation sample. FootTargetSyncController must run before hip reconciliation — ordering is a scene/pipeline authoring contract.                                                                                                                                                                                                                      | Technical        |
| AC-HR-08 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value (`Vector3?`); returning `null` leaves the animated hip pose untouched. Profiles apply epsilon-based jitter suppression to avoid micro-translations.                                                                                                                                                                                                                               | Technical        |
| AC-HR-09 | Hip reconciliation consumes the per-tick `PoseStateContext` snapshot; the pending hip target may be produced by a driver node and applied inside a `SkeletonModifier3D`, provided AC-HR-07 ordering is preserved.                                                                                                                                                                                                                                                                                | Technical        |
| AC-HR-10 | Hip reconciliation profiles compute the hip target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry exposed through `PoseStateContext`, and MUST NOT read or depend on the currently animated hip bone pose, so that `TimeSeek` scrubbing and animation changes cannot feed back into hip reconciliation.                                                                                                                                    | Technical        |
| AC-HR-11 | The Standing/Crouching pose family's default profile is `HeadTrackingHipProfile`, combining positional head offset and rotational offset (derived from rest→current head orientation delta plus rest neck→head geometry), with opposite-direction hip positional compensation to mitigate unnatural neck bending; rotational contribution magnitude is configurable via `RotationCompensationWeight`, clamped to non-negative values, and not fixed to a mandatory numeric default by this spec. | Technical        |
| AC-HR-12 | Unit-level regression tests cover the weighted `HeadTrackingHipProfile` rotational-compensation contract, including sign correctness, proportional weight scaling, non-negative weight clamp behaviour, epsilon-combined snap behaviour, and overload equivalence.                                                                                                                                                                                                                               | Technical        |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md) — see Technical Requirements #7 for the
  motion-sickness and `AnimationNodeBlend2D` rationale behind the linear-clip + `TimeSeek` default.
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
