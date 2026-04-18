---
id: IK-004
title: VRIK Pose State Machine And Hip Reconciliation
---

# VRIK Pose State Machine And Hip Reconciliation

## Requirement

Define an implementation-ready specification for player pose-state orchestration and hip reconciliation that builds on
existing IK prerequisites without redefining arm, spine, or leg solver contracts.

## Goal

Provide a framework-first, extensible contract for MVP pose states and state transitions, plus a state-dependent hip
reconciliation layer, while keeping tuning thresholds flexible for later iteration.

## Background And Motivation

The VRIK system must present immersive full-body poses (crawl, lying, sitting, kneeling) from just headset and two
controllers, not hands-only VR. Head and hand transforms come from XR devices; lower-body transforms are primarily
animation-driven. A naive scheme that offsets the hip by the headset displacement from its rest position cannot
distinguish stooping from crouching, nor support poses far from the animation reference (for example, lying down while
the feet animation is still standing idle). A pose-state layer is therefore required to select the appropriate
animation (or `AnimationTree` parameters) AND apply state-specific hip reconciliation as a single coupled
responsibility.

## User Requirements

1. Players must see coherent full-body pose behaviour across these MVP states: standing, crouching, kneeling,
   stooping, sitting, and crawling (all fours).
2. Players must experience continuous pose transitions during player-driven movement, without requiring authored
   transitions for every intermediate body position.
3. Players must retain stable feet placement behaviour aligned with existing lower-limb constraints for this phase.
4. Players must receive predictable calibration behaviour across non-standing poses, without relying on standing-only
   head-rest assumptions.

## Technical Requirements

1. IK-001, IK-002, and IK-003 are normative prerequisites and must be referenced as dependencies, not re-specified:
   - [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
   - [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
   - [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
2. Pose states must be implemented as Godot `Resource` definitions with customisable properties.
3. Transition definitions must support `Resource`-based configuration so state-specific transition logic can expand
   without redesigning the state-machine core.
4. Runtime state selection must use headset, left/right controller, and internal or animation-derived values only for
   this phase.
5. Collision-derived or locomotion-system-derived inputs must not be required for IK-004 delivery.
6. Calibration and reference-space interpretation must use viewpoint-node semantics and body-proportion signals; the
   implementation must not assume standing head rest pose as the baseline for non-standing states.
7. Feet positions from animation remain the source of truth for this phase; pose-state and hip reconciliation logic
   must not replace that ownership model.
8. Hip reconciliation must be translation-centric with state-dependent behaviour contracts; clamp constraints are
   intentionally deferred for this phase.
9. Transition orchestration must default to linear clip progression with `TimeSeek` for long, continuous,
   player-driven transitions. Rationale: player motion is non-linear; a non-linear transition clip would desynchronise
   the avatar from the headset and risks motion sickness. `AnimationNodeBlend2D` is unsuitable for such transitions
   because feet are parented under the hip, so blend-based hip shifts induce foot float/slide that downstream foot IK
   cannot correct (animated feet remain the source of truth). State-specific non-linear exceptions are permitted when
   justified.
10. The architecture must permit ambiguous-input handling when needed, but no mandatory catch-all ambiguity state is
    required in this phase.
11. This specification revision must focus on requirements and implementation contracts, not fixed numeric thresholds
    or performance metrics.
12. Each pose state must drive BOTH (a) animation selection or `AnimationTree` parameter control and (b) the hip
    reconciliation profile for that state. Animation and hip behaviour are a coupled per-state responsibility.
13. State selection must be inferred from XR device transforms and animation/runtime signals (for example headset pitch
    and controller height). Explicit controller-button input for pose switching must be avoided unless no automatic
    signal is viable for a specific transition.
14. State and transition `Resource` types must expose a documented public extension surface (subclassable base resources
    and pluggable classifier/evaluator interfaces) so new states, transitions, and classifiers can be added from
    consumer code without modifying core state-machine source.
15. The state machine must evaluate per tick from an immutable read-only context snapshot (suggested name
    `PoseStateContext`) that bundles XR inputs and skeleton/runtime signals. The context is the canonical input surface
    for classifiers, transitions, animation bindings, and hip reconciliation profiles, including
    `CameraTransform` (current XR camera global transform) and `ViewpointGlobalRest` (viewpoint-node global rest
    transform). Detailed composition is defined in the [Pose State Machine Contract](pose-state-machine-contract.md).
16. Runtime responsibilities may be split across two cooperating nodes — a `PoseStateMachine` node that runs `Tick`
    per frame and a `HipReconciliationModifier` (`SkeletonModifier3D`) that applies the pending hip translation inside
    the skeleton modifier pipeline. `Tick` must run after follower updates provide current tracked transforms and
    before downstream consumers read pending hip translation. This may be driven by a non-modifier runtime node, or by
    begin-stage modifier-callback flow (for example `PlayerVRIK` begin-stage callback invoked by `StageModifier`).
    This split is permitted as the canonical pattern but is not mandated; any equivalent topology that preserves this
    ordering and AC-HR-07 ordering is acceptable.
17. Transition Resources must support optional lifecycle hooks invoked around each state switch in this order:
    `OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`. The state machine must emit a
    state-changed observation (signal or event) so consumers can react.
18. State and transition identifiers are authored as `StringName` in editor contexts for designer ergonomics. The
    internal selection layer may use either `StringName` or `string` for testability, provided identity semantics are
    preserved.
19. Hip reconciliation profiles must return an **absolute hip target position in skeleton-local space** as a nullable
    value (`Vector3?`). Returning `null` means "do not override the animated hip bone". Profiles must compute the hip
    target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry
    exposed through `PoseStateContext`, and must not read or depend on the currently animated hip bone pose, so that
    `TimeSeek` scrubbing and animation changes cannot feed back into hip reconciliation. Detailed contract lives in
    the [Hip Reconciliation Contract](hip-reconciliation-contract.md).
20. For the Standing/Crouching pose family, the default profile is `HeadTrackingHipProfile`. The profile must combine
    (a) positional head-offset contribution and (b) rotational-offset contribution derived from rest→current head
    orientation delta and rest neck→head geometry, then apply opposite-direction hip compensation to mitigate
    unnatural neck bending. Rotational contribution magnitude must be configurable per profile/resource via
    `RotationCompensationWeight`, with non-negative clamp behaviour (`max(0, weight)`) and no mandatory fixed default
    value in this spec revision. Vertical hip movement for this family is owned by the hip profile, not by the
    animation clip. Other pose families may diverge.
21. Unit-level regression tests must cover the weighted rotational-compensation contract for
    `HeadTrackingHipProfile`, including sign correctness, proportional scaling by weight, non-negative clamp behaviour,
    epsilon-combined snap behaviour, and overload equivalence for profile evaluation entry points.
22. The Standing/Crouching pose family is backed by a single `AnimationTree` state, `StandingCrouching`, whose
    sub-graph continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")` driven by a normalised scalar.
    Multiple framework-level `PoseState` resources may map to the same `AnimationTree` state when they share animation
    behaviour. The `Idle` clip remains in the animation library as a deferred-but-supported extension point (for
    example, additive breathing layering) and is not wired into the tree for MVP.

## Incremental Delivery

IK-004 is delivered incrementally. The following plan is descriptive (not normative) and may be refined by future
delivery work:

- **Increment 1** — Framework abstractions (`PoseStateContext`, base `PoseState`/`PoseTransition` resources, state
  machine core, hip reconciliation modifier skeleton) and a basic concrete Standing state. No scene or `PlayerVRIK`
  integration.
- **Increment 2** — `AnimationTree` authoring, concrete pose set expansion across the MVP state coverage,
  `PlayerVRIK` and scene integration, and ordering relative to existing IK modifiers.

## Specification Structure

This page is the authoritative overview for IK-004. Detailed implementation contracts are split into focused pages:

- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)

Both linked pages are normative dependencies for implementation.

## In Scope

- MVP state coverage contracts for standing, crouching, kneeling, stooping, sitting, and crawling (all fours).
- Framework-first state detection and transition architecture using extensible Godot `Resource` definitions.
- State-specific disambiguation between overlapping poses (for example stoop vs crouch) as a permanent classifier
  responsibility resolved via auxiliary XR signals such as headset pitch and controller height; tuning values remain
  open for later iteration.
- State-dependent hip translation reconciliation behaviour.
- Integration boundaries to IK-001/002/003 prerequisite contracts.

## Out Of Scope

- Re-defining solver-specific behaviour already owned by IK-001/002/003.
- Collision and locomotion signal integration as required state inputs.
- Final numeric thresholds, curve tuning constants, and strict performance budgets.
- Optional expansion states beyond the MVP set.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
| --- | --- | --- |
| AC-00 | The specification separates user-visible behaviour requirements from implementation contracts and keeps both layers normative. | User + Technical |
| AC-01 | IK-004 identifies standing, crouching, kneeling, stooping, sitting, and crawling as required MVP pose states. | User |
| AC-02 | IK-004 references IK-001/002/003 as prerequisites without duplicating their solver contracts. | Technical |
| AC-03 | Pose states and transitions are specified as extensible `Resource`-driven contracts, including framework-first state-specific transition conditions. | Technical |
| AC-04 | Input contracts for this phase are restricted to headset, controllers, and internal or animation-derived values, with collision/locomotion inputs explicitly deferred. | Technical |
| AC-05 | Calibration contracts require viewpoint-node semantics and body-proportion references, and forbid standing-rest-pose assumptions for non-standing states. | Technical |
| AC-06 | Feet positions from animation are explicitly defined as source of truth for this phase. | User + Technical |
| AC-07 | Hip reconciliation is specified as translation-centric, state-dependent behaviour, with clamping deferred. | Technical |
| AC-08 | Transition contracts define linear clip + `TimeSeek` as the default for long continuous transitions and permit state-specific non-linear exceptions. | Technical |
| AC-09 | The spec does not require a mandatory catch-all ambiguity state for this phase. | Technical |
| AC-10 | Across supported MVP movement scenarios, players see coherent visible full-body pose continuity while moving between required pose states. | User |
| AC-11 | Each pose state binds both animation selection (or AnimationTree parameter control) and hip reconciliation behaviour as a coupled responsibility. | Technical |
| AC-12 | State switching relies on inferred signals from XR devices and runtime state; explicit button-based pose switching is avoided by default. | User + Technical |
| AC-13 | State and transition Resources expose a public extension surface permitting developer-supplied states and classifiers without editing core source. | Technical |
| AC-14 | The state machine evaluates per tick from an immutable read-only context snapshot that is the canonical input surface for classifiers, transitions, animation bindings, and hip reconciliation profiles, including `CameraTransform` and `ViewpointGlobalRest` as required context fields. | Technical |
| AC-15 | Runtime responsibilities may be split into a `PoseStateMachine` node and a `HipReconciliationModifier` (`SkeletonModifier3D`) as the canonical pattern, without mandating that specific split, provided AC-HR-07 ordering is preserved and `Tick` runs after follower updates, before downstream consumers read pending hip translation, and may use begin-stage modifier-callback flow (for example `PlayerVRIK` via `StageModifier`) or equivalent topology preserving this ordering. | Technical |
| AC-16 | Transition Resources support optional lifecycle hooks (`OnTransitionEnter` → `OnExit` → `OnEnter` → `OnTransitionExit`) and the state machine emits a state-changed observation. | Technical |
| AC-17 | State and transition identifiers are authored as `StringName`; internal selection may use `StringName` or `string` provided identity semantics are preserved. | Technical |
| AC-18 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value (`Vector3?`); returning `null` leaves the animated hip pose untouched. | Technical |
| AC-19 | Hip reconciliation profiles compute the hip target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry, and must not read or depend on the currently animated hip bone pose. | Technical |
| AC-20 | The Standing/Crouching pose family's default profile is `HeadTrackingHipProfile`, combining positional head offset and rotational offset (from rest→current head orientation delta plus rest neck→head geometry) with opposite-direction hip compensation to mitigate unnatural neck bending; rotational contribution magnitude is configurable via `RotationCompensationWeight`, clamped to non-negative values, and not fixed to a mandatory numeric default by this spec. | Technical |
| AC-21 | Unit-level regression tests cover the weighted `HeadTrackingHipProfile` rotational-compensation contract, including sign correctness, proportional weight scaling, non-negative weight clamp behaviour, epsilon-combined snap behaviour, and overload equivalence. | Technical |
| AC-22 | The Standing/Crouching pose family is backed by a single `AnimationTree` state (`StandingCrouching`) running `TimeSeek → AnimationNodeAnimation("Crouch-seek")`, with multiple framework-level `PoseState` resources permitted to share one `AnimationTree` state. | Technical |
| AC-23 | The `Idle` clip remains in the animation library as a deferred-but-supported extension point for future layering (for example additive breathing) and is not wired into the `AnimationTree` for MVP. | Technical |

## Code-Spec Sync Note

Increment 2.1 is delivered alongside this specification state. The shipped implementation includes the
`PoseStateMachine` wiring on the player, the `TimeSeekAnimationBinding` animation binding targeting the single
`StandingCrouching` `AnimationTree` state (sub-graph `TimeSeek → AnimationNodeAnimation("Crouch-seek")`), the
`HeadVerticalOffsetPoseTransition` transition resource, the `HeadTrackingHipProfile` hip reconciliation profile
(replacing the deprecated `LateralHeadOffsetHipReconciliationProfile`) with rotational hip correction added for the
Standing/Crouching path alongside positional head offset and configurable `RotationCompensationWeight`
(non-negative clamp), the concrete `CrouchingPoseState`, and the
`HipReconciliationModifier` ordering in `player.tscn` placed between `VRIKBeginStage` and the existing IK modifier
chain so AC-HR-07 ordering is preserved. In this wiring, `PoseStateMachine.Tick` executes in begin-stage flow after
IK follower adjustments have produced current tracked transforms. `PoseStateContext` also consolidates camera and
viewpoint current-transform duplication into `CameraTransform` while preserving `ViewpointGlobalRest`. Hip
reconciliation profiles now return an absolute hip target position in skeleton-local space (`Vector3?`), with `null`
meaning "do not override the animated hip bone". Unit regression coverage now includes weighted rotational
compensation maths (sign, scaling, non-negative clamp, epsilon-combined snap, and overload equivalence).

Known deferrals tracked against this revision:

- `HeadHeightPoseClassifier` is not yet shipped (permitted by AC-PS-14, which treats classifier plug-ins as an
  extensibility surface rather than a required Increment 2 artefact).
- The `Idle` clip is retained in the animation library for future additive layering (for example breathing) on top of
  the `StandingCrouching` sub-graph, but is not wired into the `AnimationTree` for MVP (AC-23).
- Godot-runtime integration tests for the pose state machine are deferred.
- VR-runtime visual verification of the Standing ↔ Crouching transition is deferred pending headset access.

## References

- [Player VRIK System](../index.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
