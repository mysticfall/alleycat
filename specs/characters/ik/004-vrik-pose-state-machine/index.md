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

1. Players must see coherent full-body pose behaviour across these MVP states: standing, kneeling,
   stooping, sitting, and crawling (all fours), with standing covering a continuous standing-to-crouching range.
2. Players must experience continuous pose transitions during player-driven movement, without requiring authored
   transitions for every intermediate body position.
3. Players must retain stable feet placement behaviour aligned with existing lower-limb constraints for this phase. See IK-003
   foot-target synchronisation contract for the deterministic sync stage that re-synchronises foot IK targets from animated
   foot transforms at the beginning of each IK solve cycle.
4. Players must receive predictable calibration behaviour across non-standing poses, without relying on standing-only
   head-rest assumptions.
5. Players must be able to transition from the Standing pose to kneeling by leaning forward beyond a configurable depth threshold
   from the full-crouch baseline, without requiring explicit button input.
6. Players must see the kneeling pose seek forward from the full-crouch baseline, with the forward travel distance being short and
   tunable rather than a fixed offset from standing.
7. Players must be able to transition from kneeling back to a crouched/upright posture within the Standing continuum by leaning back or upright beyond a configurable reverse
   threshold, without requiring explicit button input. This return transition is bidirectional with the forward kneeling transition.
8. Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion
   perpendicular to the hip rest up axis, while preserving strong vertical crouch response.
9. Players must experience a smooth transition between reduced perpendicular response and full aligned response,
   without abrupt discontinuities.

## Technical Requirements

1. IK-001, IK-002, and IK-003 are normative prerequisites and must be referenced as dependencies, not re-specified:
   - [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
   - [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
   - [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
2. Pose states must be implemented as Godot `Resource` definitions with customisable properties.
3. Transition definitions must support `Resource`-based configuration so state-specific transition logic can expand
   without redesigning the state-machine core.
4. Runtime state selection must use head and hand IK-target transforms (`HeadTargetTransform`,
   `LeftHandTargetTransform`, `RightHandTargetTransform`) plus internal or animation-derived values only for
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
13. State selection must be inferred from IK-target transforms and animation/runtime signals (for example head pitch
     and hand height from `PoseStateContext`). Explicit button input for pose switching must be avoided unless no automatic
     signal is viable for a specific transition.
14. State and transition `Resource` types must expose a documented public extension surface (subclassable base resources
     and pluggable classifier/evaluator interfaces) so new states, transitions, and classifiers can be added from
     consumer code without modifying core state-machine source.
15. The state machine must evaluate per tick from an immutable read-only context snapshot (suggested name
     `PoseStateContext`) that bundles IK-target inputs and skeleton/runtime signals. The context is the canonical
     input surface for classifiers, transitions, animation bindings, and hip reconciliation profiles, including
     `HeadTargetTransform` (current head IK-target global transform), `HeadTargetRestTransform` (head IK-target
     global rest transform), `LeftHandTargetTransform`, and `RightHandTargetTransform`. Detailed composition is
     defined in the [Pose State Machine Contract](pose-state-machine-contract.md).
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
20. For the Standing pose family, the default profile is `HeadTrackingHipProfile`. The profile must combine
     three contributions: (a) positional head-offset contribution, (b) per-axis positional modulation, and (c)
     rotational-offset contribution.

     **Per-axis positional modulation** decomposes the head-position offset into three components in the hip
     rest local frame: up/down, side-to-side, and forward/back. Each axis applies its own configurable scalar weight:

| Axis         | Configurable parameter                | Authored standing-profile default |
|--------------|---------------------------------------|------------------------------------|
| Up / Down    | `VerticalPositionWeight`         | `1.0` (full offset)                |
| Side-to-Side | `LateralPositionWeight`         | `0.5` (50 % offset)                |
| Forward/Back | `ForwardPositionWeight`         | `0.1` (10 % offset)                |

     The rest up axis is derived from the hip bone's rest-pose or global-rest basis in skeleton-local space.
     The combined weights produce strong vertical crouch response and reduced lateral/forward-back hip travel,
     enabling natural stooping and leaning while preserving +up/-up symmetry. All three parameters are
     independently configurable and clamped to the `[0, 1]` range; the defaults above represent the authored
     standing profile and may be overridden in derived resources. The rotational-offset contribution is derived
     from rest→current head orientation delta and rest neck→head geometry, then applies opposite-direction hip
     compensation to mitigate unnatural neck bending. Rotational contribution magnitude must be configurable
     per profile/resource via `RotationCompensationWeight`, with non-negative clamp behaviour (`max(0, weight)`)
     and no mandatory fixed default value in this spec revision. Vertical hip movement for this family is
     owned by the hip profile, not by the animation clip. Other pose families may diverge.
21. Unit-level regression tests must cover both: (a) the weighted rotational-compensation contract for
      `HeadTrackingHipProfile` (sign correctness, proportional scaling by weight, non-negative clamp behaviour,
      epsilon-combined snap behaviour, and overload equivalence for profile evaluation entry points); and
      (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight
      application correctness, up/down full-response preservation, side-to-side partial-response verification
      at the authored default, forward/back minimal-response verification at the authored default,
      diagonal/interpolated response continuity, and +up/-up symmetry).
22. The Standing pose family is backed by a single `AnimationTree` state, `StandingCrouching`, whose
     sub-graph continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")` driven by a normalised scalar
     representing the full standing-to-crouching continuum. A single framework-level `StandingPoseState` resource
     maps to this `AnimationTree` state — there is no separate framework-level `CrouchingPoseState`. The `Idle` clip
     remains in the animation library as a deferred-but-supported extension point (for example, additive breathing
     layering) and is not wired into the tree for MVP.
23. The Standing→Kneeling transition is gated by a configurable crouch-depth threshold that must be satisfied before the
     transition can trigger. The gate requires the player to be at or near full crouch depth on the standing-to-crouching continuum before kneeling becomes reachable.
24. The Standing→Kneeling transition trigger and the kneeling pose seek are both measured from the full-crouch baseline (the
     head position when fully crouching on the continuum), not from the standing baseline. Forward lean beyond the full-crouch baseline drives
     the transition.
25. The kneeling forward travel range (how far the kneeling pose seeks forward from the full-crouch baseline) is short and
     tunable via a configuration parameter. The range is measured in head-offset space from the full-crouch position.
26. The kneeling transition thresholds must use normalised ratios derived from rest-pose body measures, not absolute metres.
     At minimum, the head-height measure from rest pose must define the reference for the normalised crouch-depth gate.
     Tunable parameters in this spec use flexible ratios (for example 0.85 × rest-head-height) rather than fixed absolute values.
27. The Kneeling→Standing return transition is gated by a configurable reverse-threshold that is also expressed as a normalised
     ratio from the full-crouch baseline. The return transition uses the same full-crouch baseline as the forward transition but
     in the reverse direction (leaning back or upright from kneeling).

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

- MVP state coverage contracts for standing (covering standing-to-crouching continuum), kneeling, stooping, sitting, and crawling (all fours).
- Framework-first state detection and transition architecture using extensible Godot `Resource` definitions.
- State-specific disambiguation between overlapping poses (for example stoop vs crouch) as a permanent classifier
  responsibility resolved via auxiliary IK-target signals such as head pitch and hand height from the pose-state
  context; tuning values remain open for later iteration.
- State-dependent hip translation reconciliation behaviour.
- Integration boundaries to IK-001/002/003 prerequisite contracts.
- **Reusable scene composition**: `player.tscn` instances `reference_female_ik.tscn` (which contains arm/leg IK), `vrik.tscn` (PoseStateMachine + PlayerVRIK), and `animation_tree_player.tscn` (AnimationTree) as separate reusable components rather than carrying inline IK/AnimationTree.

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
| AC-01 | IK-004 identifies standing (covering the standing-to-crouching continuum), kneeling, stooping, sitting, and crawling as required MVP pose states. | User |
| AC-02 | IK-004 references IK-001/002/003 as prerequisites without duplicating their solver contracts. | Technical |
| AC-03 | Pose states and transitions are specified as extensible `Resource`-driven contracts, including framework-first state-specific transition conditions. | Technical |
| AC-04 | Input contracts for this phase are restricted to head and hand IK-target transforms (`HeadTargetTransform`, `LeftHandTargetTransform`, `RightHandTargetTransform`) and internal or animation-derived values, with collision/locomotion inputs explicitly deferred. | Technical |
| AC-05 | Calibration contracts require viewpoint-node semantics and body-proportion references, and forbid standing-rest-pose assumptions for non-standing states. | Technical |
| AC-06 | Feet positions from animation are explicitly defined as source of truth for this phase. | User + Technical |
| AC-07 | Hip reconciliation is specified as translation-centric, state-dependent behaviour, with clamping deferred. | Technical |
| AC-08 | Transition contracts define linear clip + `TimeSeek` as the default for long continuous transitions and permit state-specific non-linear exceptions. | Technical |
| AC-09 | The spec does not require a mandatory catch-all ambiguity state for this phase. | Technical |
| AC-10 | Across supported MVP movement scenarios, players see coherent visible full-body pose continuity while moving between required pose states. | User |
| AC-10b | Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion perpendicular to the hip rest up axis, while preserving strong vertical crouch response; players must experience smooth transition between reduced perpendicular response and full aligned response, without abrupt discontinuities. | User |
| AC-11 | Each pose state binds both animation selection (or AnimationTree parameter control) and hip reconciliation behaviour as a coupled responsibility. | Technical |
| AC-12 | State switching relies on inferred signals from IK-target transforms and runtime state; explicit button-based pose switching is avoided by default. | User + Technical |
| AC-13 | State and transition Resources expose a public extension surface permitting developer-supplied states and classifiers without editing core source. | Technical |
| AC-14 | The state machine evaluates per tick from an immutable read-only context snapshot that is the canonical input surface for classifiers, transitions, animation bindings, and hip reconciliation profiles, including `HeadTargetTransform`, `HeadTargetRestTransform`, `LeftHandTargetTransform`, and `RightHandTargetTransform` as required context fields. | Technical |
| AC-15 | Runtime responsibilities may be split into a `PoseStateMachine` node and a `HipReconciliationModifier` (`SkeletonModifier3D`) as the canonical pattern, without mandating that specific split, provided AC-HR-07 ordering is preserved and `Tick` runs after follower updates, before downstream consumers read pending hip translation, and may use begin-stage modifier-callback flow (for example `PlayerVRIK` via `StageModifier`) or equivalent topology preserving this ordering. | Technical |
| AC-16 | Transition Resources support optional lifecycle hooks (`OnTransitionEnter` → `OnExit` → `OnEnter` → `OnTransitionExit`) and the state machine emits a state-changed observation. | Technical |
| AC-17 | State and transition identifiers are authored as `StringName`; internal selection may use `StringName` or `string` provided identity semantics are preserved. | Technical |
| AC-18 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value (`Vector3?`); returning `null` leaves the animated hip pose untouched. | Technical |
| AC-19 | Hip reconciliation profiles compute the hip target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry, and must not read or depend on the currently animated hip bone pose. | Technical |
| AC-20 | For the Standing pose family's default profile is `HeadTrackingHipProfile`, combining (a) positional head offset, (b) per-axis positional modulation decomposed into up/down, side-to-side, and forward/back components in the hip rest local frame, and (c) rotational offset (from rest→current head orientation delta plus rest neck→head geometry) with opposite-direction hip compensation to mitigate unnatural neck bending. Per-axis weights are independently configurable: `VerticalPositionWeight` (default `1.0`, full offset), `LateralPositionWeight` (default `0.5`, 50 % offset), and `ForwardPositionWeight` (default `0.1`, 10 % offset). The rest up axis is derived from the hip bone's rest-pose or global-rest basis in skeleton-local space. The combined weights produce strong vertical crouch response and reduced lateral/forward-back hip travel, preserving +up/-up symmetry. All three parameters are clamped to the `[0, 1]` range and independently configurable; the defaults above represent the authored standing profile and may be overridden in derived resources. Rotational contribution magnitude is configurable via `RotationCompensationWeight`, clamped to non-negative values, with no mandatory fixed default in this spec. | Technical |
| AC-21 | Unit-level regression tests cover both: (a) the weighted `HeadTrackingHipProfile` rotational-compensation contract (sign correctness, proportional weight scaling, non-negative weight clamp behaviour, epsilon-combined snap behaviour, and overload equivalence); and (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight application correctness, up/down full-response preservation, side-to-side partial-response verification at the authored default, forward/back minimal-response verification at the authored default, diagonal/interpolated response continuity, and +up/-up symmetry). | Technical |
| AC-22 | The Standing pose family is backed by a single `AnimationTree` state (`StandingCrouching`) running `TimeSeek → AnimationNodeAnimation("Crouch-seek")`, with a single framework-level `StandingPoseState` resource mapping to this `AnimationTree` state. There is no separate framework-level CrouchingPoseState; the standing-to-crouching continuum is covered by one StandingPoseState. | Technical |
| AC-23 | The `Idle` clip remains in the animation library as a deferred-but-supported extension point for future layering (for example additive breathing) and is not wired into the `AnimationTree` for MVP. | Technical |
| AC-24 | The Standing→Kneeling transition is gated by a configurable crouch-depth threshold that must be satisfied before the transition can trigger, requiring the player to be at or near full crouch depth on the standing-to-crouching continuum (tunable gate). | Technical |
| AC-25 | The Standing→Kneeling transition trigger and kneeling pose seek are measured from the full-crouch baseline (head position at full crouch on the continuum), not from the standing baseline. Forward lean from the full-crouch baseline drives the transition. | Technical |
| AC-26 | The kneeling forward travel range is short and tunable via a configuration parameter, measured from the full-crouch baseline in head-offset space. | Technical |
| AC-26a | The kneeling transition thresholds use normalised ratios derived from rest-pose body measures, not absolute metres. At minimum, the head-height measure from rest pose defines the reference for the normalised crouch-depth gate. | Technical |
| AC-26b | The Kneeling→Standing return transition is gated by a configurable reverse-threshold expressed as a normalised ratio from the full-crouch baseline, providing bidirectional standing-continuum↔kneel transitions. | Technical |
| AC-27 | Foot-target synchronisation stage re-synchronises foot IK targets from animated foot transforms (position + rotation) at the beginning of each leg IK solve cycle, before pole-target computation. `FootTargetSyncController` must run before `HipReconciliationModifier` and any other foot-mutating modifiers. This ensures deterministic solve behaviour when animation timing or `TimeSeek` position changes. Ordering is a scene/pipeline authoring contract — runtime auto-reordering is intentionally not used. See IK-003 AC-05a. | User + Technical |

## Code-Spec Sync Note

Increment 2.2 is delivered alongside this specification state. The shipped implementation includes all content from
Increment 2.1 plus the Standing→Kneeling bidirectional transition with the following contracts:

- The `KneelingPoseState` concrete state resource is now shipped.
- The Standing→Kneeling transition is gated by a configurable crouch-depth threshold (`CrouchDepthGate`) that requires
  the player to be at or near full crouch depth on the standing-to-crouching continuum before kneeling becomes reachable.
- The Standing→Kneeling transition trigger and kneeling pose seek are measured from the full-crouch baseline (head position at full crouch on the continuum),
  not from the standing baseline. Forward lean from the full-crouch baseline drives the transition.
- The kneeling forward travel range (`KneelingForwardRange`) is short and tunable via a configuration parameter, measured
  in head-offset space from the full-crouch baseline.
- The return transition now provides bidirectional standing-continuum↔kneel transitions, gated by a configurable
  reverse-threshold (expressed as a normalised ratio from the full-crouch baseline).
- Kneeling transition thresholds now use normalised ratios derived from rest-pose body measures, not absolute metres. At minimum,
  the head-height measure from rest pose (`RestHeadHeight`) defines the reference for the normalised crouch-depth gate. Tunable parameters use flexible ratios
  (for example `0.85 × RestHeadHeight`) rather than fixed absolute values.
- The `StandingToKneelingPoseTransition` transition resource implements the forward contracts, and
  `KneelingToStandingPoseTransition` implements the reverse contracts.

The implementation also includes the `PoseStateMachine` wiring on the player, the `StandingCrouchingSeekAnimationBinding` animation
binding targeting the single `StandingCrouching` `AnimationTree` state (sub-graph `TimeSeek → AnimationNodeAnimation
("Crouch-seek")`), the `HeadTrackingHipProfile` hip reconciliation profile (replacing the deprecated
`LateralHeadOffsetHipReconciliationProfile`) with per-axis positional modulation using the hip rest local frame —
`VerticalPositionWeight` (default `1.0`, full offset), `LateralPositionWeight` (default `0.5`, 50 % offset), and
`ForwardPositionWeight` (default `0.1`, 10 % offset) — plus rotational hip correction with configurable
`RotationCompensationWeight` (non-negative clamp), the concrete `StandingPoseState` resource
(covering the full standing-to-crouching continuum — there is no separate `CrouchingPoseState`), the
`StandingToKneelingPoseTransition`/`KneelingToStandingPoseTransition` pair for bidirectional kneel gating from the standing continuum,
and the `HipReconciliationModifier` ordering in `player.tscn` placed between
`VRIKBeginStage` and the existing IK modifier chain so AC-HR-07 ordering is preserved. In this wiring,
`PoseStateMachine.Tick` executes in begin-stage flow after IK follower adjustments have produced current tracked transforms.
`PoseStateContext` now standardises head IK-target transforms as `HeadTargetTransform` and `HeadTargetRestTransform`.
`PoseStateContext` additionally exposes rest-pose body measures (for example `RestHeadHeight`) for ratio-based threshold computation.
- The standing/crouching seek blend now uses `FullCrouchDepthRatio` normalised by `RestHeadHeight` as the body-proportion-safe reference, replacing any absolute-metre-based crouch depth parameter. This ensures the seek curve scales correctly across different avatar proportions.
Hip reconciliation profiles now return an absolute hip target position in skeleton-local
space (`Vector3?`), with `null` meaning "do not override the animated hip bone". Unit regression coverage now includes
per-axis positional modulation (rest-up-axis derivation, per-axis weight application, up/down full response, side-to-side at
authored default, forward/back at authored default, diagonal continuity, +up/-up symmetry)
and weighted rotational compensation maths (sign, scaling, non-negative clamp, epsilon-combined snap, and overload equivalence).

**Spec-to-code sync note — hip reconciliation positional weighting:** This specification revision specifies per-axis
configurable weights (`VerticalPositionWeight`, `LateralPositionWeight`, `ForwardPositionWeight`) applied to the
head-position offset decomposed in the hip rest local frame. Authored defaults for the standing profile are
`1.0` / `0.5` / `0.1` respectively. The combined weights produce strong vertical crouch response and reduced
lateral/forward-back hip travel, no dependency on the currently animated hip bone, and no mandatory fixed numeric defaults
beyond the authored standing-profile values listed above.

Known deferrals tracked against this revision:

- `HeadHeightPoseClassifier` is not yet shipped (permitted by AC-PS-14, which treats classifier plug-ins as an
  extensibility surface rather than a required Increment 2 artefact).
- The `Idle` clip is retained in the animation library for future additive layering (for example breathing) on top of
  the `StandingCrouching` sub-graph, but is not wired into the `AnimationTree` for MVP (AC-23).
- VR-runtime visual verification of the standing-to-crouching continuum and Kneeling transitions is deferred pending headset access.

## References

- [Player VRIK System](../index.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
- [Pose State Machine Contract](pose-state-machine-contract.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- @game/assets/characters/ik/vrik.tscn
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn
- @game/assets/characters/reference/female/reference_female_ik.tscn
- @game/assets/characters/reference/player.tscn
