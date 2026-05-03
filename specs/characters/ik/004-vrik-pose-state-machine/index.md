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
5. Players must be able to transition from the Standing pose to kneeling using an armed-then-retreat trigger model:
   the transition initiates from the full-crouch baseline, becomes armed after sufficient forward travel from that baseline (measured in head-offset space),
   and fires only after retreating from the armed peak by a configurable amount. This prevents accidental firing and provides intentional player control.
6. The Standing→Kneeling transition must require a crouch-depth gate—near-full crouch on the standing-to-crouching continuum—before kneeling becomes reachable.
7. Players must be able to transition from kneeling back to a crouched or upright posture within the Standing continuum using the same armed-then-retreat model:
   the return path is measured from the full-crouch baseline, arms after sufficient forward travel from that baseline, and fires only after retreating from the armed peak by a configurable amount.
   This return transition is bidirectional with the forward kneeling transition.
8. Following any kneeling transition (forward or reverse), both transition directions must remain locked until the forward-axis offset returns to near the neutral or full-crouch baseline.
   This prevents immediate bounce-back and re-triggering.
9. Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion
   perpendicular to the hip rest up axis, while preserving strong vertical crouch response.
10. Players must experience a smooth transition between reduced perpendicular response and full aligned response,
    without abrupt discontinuities.
11. Standing-family reconciliation must keep strong vertical hip drop during crouch where head remains roughly above hips.
12. Standing-family reconciliation must avoid chasing the head down when the head leads forward (stoop) or backward (lean-back);
    hips should stay upright.
13. Players must be protected from extreme hip deformation beyond a configurable state-defined limit envelope,
    preventing unnatural body proportions when head motion exceeds what hip reconciliation can safely compensate.
    The limit reference is defined per-pose-state; the Standing family defines a continuum-aware envelope.
14. Players must be able to transition from the Standing pose to an all-fours crawling pose by moving the head forward
   beyond a head forward offset threshold, where the transition becomes armed once the head's forward offset reaches a
   configurable threshold and fires when the player continues forward past that armed point by a configurable additional margin.
15. Players must be able to transition from the Kneeling pose to an all-fours crawling pose by moving the head forward
   beyond a head forward offset threshold, where the transition becomes armed once the head's forward offset reaches a
   configurable threshold and fires when the player continues forward past that armed point by a configurable additional margin.
16. When in AllFours, players must experience a smooth transition animation from the entering position to the crawling hold pose,
    driven by the head's forward offset position.
17. While crawling on all fours, if the player raises their head vertically above a configurable threshold, they must
    transition back to the entering (transitioning) state to prepare for a return to standing.
18. While in the AllFours transitioning state, if the player's head forward offset drops below a configurable return threshold,
    the pose must automatically transition back to the Standing pose (crouching baseline) rather than to kneeling.

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
8. Hip reconciliation must be translation-centric with state-dependent behaviour contracts, including configurable
    limits on hip offsets from the state-defined reference baseline.
9. The architecture must support configurable limits on hip offsets via a reusable Godot resource type
     `OffsetLimits3D` in a `Common` namespace, representing optional directional limits (Up, Down, Left, Right, Forward, Back).
     Each directional limit is optional; a limit side that is unconfigured or unmarked is treated as unbounded (no clamp applied).
     This allows states to specify only the relevant bounds without requiring values for irrelevant directions.
10. The limit reference is defined per-pose-state through the `PoseState` contract. Each `PoseState` resource
    (or equivalent state-owned contract) resolves the per-tick hip-limit reference semantics for its associated
    hip reconciliation profile. For the Standing family, the reference and envelope are continuum-aware.
11. Hip reconciliation must clamp the final hip offset from the state-defined reference, not just sub-contributions.
12. Residual (desired hip offset minus applied/clamped hip offset) must be propagated into head-target limiting before IK solve,
     with XR-origin compensation in `PlayerVRIK.OnEndStage` absorbing remaining mismatch between virtual head target and physical headset.
13. Existing directional weights (`LateralPositionWeight`, `ForwardPositionWeight`, etc.) still participate; the architecture must
     support cancelling those weights from the residual when deriving the head-target clamp.
14. For `StandingPoseState`, the hip-limit reference and envelope are continuum-aware in all directions:
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
15. The baseline for the limited hip offset is the state-defined reference for the current pose/tick, with configured limits chosen heuristically.
16. The architecture must permit ambiguous-input handling when needed, but no mandatory catch-all ambiguity state is
    required in this phase.
17. This specification revision must focus on requirements and implementation contracts, not fixed numeric thresholds
     or performance metrics.
18. Each pose state must drive BOTH (a) animation control via the `PoseState` resource and (b) the hip
     reconciliation profile for that state. Animation and hip behaviour are a coupled per-state responsibility.
19. State selection must be inferred from IK-target transforms and animation/runtime signals (for example head pitch
     and hand height from `PoseStateContext`). Explicit button input for pose switching must be avoided unless no automatic
     signal is viable for a specific transition.
20. State and transition `Resource` types must expose a documented public extension surface (subclassable base resources
     and pluggable classifier/evaluator interfaces) so new states, transitions, and classifiers can be added from
     consumer code without modifying core state-machine source.
21. The state machine must evaluate per tick from an immutable read-only context snapshot (`PoseStateContext`) that bundles
     IK-target inputs, skeleton signals, and runtime services. The context is the canonical input surface for classifiers,
     transitions, states, and hip reconciliation profiles, including:
     - `HeadTargetTransform` (current head IK-target global transform)
     - `HeadTargetRestTransform` (head IK-target global rest transform)
     - `LeftHandTargetTransform` (left hand IK-target global transform)
     - `RightHandTargetTransform` (right hand IK-target global transform)
     - `AnimationTree` (the runtime animation tree for this player, enabling transition/state logic to
       drive animation or access debugging helpers from context)
     - Rest-pose body measures (for example `RestHeadHeight`) for ratio-based threshold computation
     - Tick delta
     - Auxiliary-signals lookup for extensible computed values

     Detailed composition is defined in the [Pose State Machine Contract](pose-state-machine-contract.md).

      > **Debug overlay note:** `PlayerVRIK` owns the aggregated debug-overlay output path for play-test instrumentation. Pose states may surface state-specific animation debug lines through that output path. Debug overlay follows the existing toggle-driven pattern; pose-state implementations that add debug output must route through `PlayerVRIK`'s debug-aggregation contract.

22. Runtime responsibilities may be split across two cooperating nodes — a `PoseStateMachine` node that runs `Tick`
     per frame and a `HipReconciliationModifier` (`SkeletonModifier3D`) that applies the pending hip translation inside
     the skeleton modifier pipeline. `Tick` must run after follower updates provide current tracked transforms and
     before downstream consumers read pending hip translation. This may be driven by a non-modifier runtime node, or by
     begin-stage modifier-callback flow (for example `PlayerVRIK` begin-stage callback invoked by `StageModifier`).
     This split is permitted as the canonical pattern but is not mandated; any equivalent topology that preserves this
     ordering and AC-HR-07 ordering is acceptable.
23. Animation control for pose states is now owned by `PoseState` resources, not by a separate animation-binding abstraction.
     Each `PoseState` resource provides lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) driven by the
     state-machine runtime, plus a startup-only `Start(AnimationTree)` method that is called only once after
     initial-state resolution to seed the initial animation state. The standing pose family uses this pattern.
17. Animation control for transitions is owned by `PoseTransition` resources. Each `PoseTransition` resource
    provides lifecycle hooks that may own AnimationTree travel into authored transition states when they fire.
    `PoseTransition` resources may drive AnimationTree state changes directly.
18. `PoseStateMachine` permits a one-time startup exception: after resolving `InitialStateId`, it may call
    `PoseState.Start(AnimationTree)` once to seed the initial authored state. This startup path is exclusive
    to initial state setup and is not used during normal tick evaluation.
19. Transition Resources must support optional lifecycle hooks invoked around each state switch in this order:
    `OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`. The state machine must emit a
    state-changed observation (signal or event) so consumers can react.
20. State and transition identifiers are authored as `StringName` in editor contexts for designer ergonomics. The
    internal selection layer may use either `StringName` or `string` for testability, provided identity semantics are
    preserved.
21. Hip reconciliation profiles must return an **absolute hip target position in skeleton-local space** as a nullable
    value (`Vector3?`). Returning `null` means "do not override the animated hip bone". Profiles must compute the hip
    target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry
exposed through `PoseStateContext`, and must not read or depend on the currently animated hip bone pose, so that
authored state-machine transitions and animation changes cannot feed back into hip reconciliation. Detailed contract lives in
     the [Hip Reconciliation Contract](hip-reconciliation-contract.md).
22. For the Standing pose family, the default profile is `HeadTrackingHipProfile`. The profile must combine
       four contributions: (a) positional head-offset contribution, (b) per-axis positional modulation, (c) alignment-based vertical damping, and (d)
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
     standing profile and may be overridden in derived resources.

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

     The rotational-offset contribution is derived from rest→current head orientation delta and rest neck→head geometry,
     then applies opposite-direction hip compensation to mitigate unnatural neck bending. Rotational contribution magnitude
     must be configurable per profile/resource via `RotationCompensationWeight`, with non-negative clamp behaviour (`max(0, weight)`)
     and no mandatory fixed default value in this spec revision. Vertical hip movement for this family is
owned by the hip profile, not by the animation clip. Other pose families may diverge.
23. Unit-level regression tests must cover both: (a) the weighted rotational-compensation contract for
     `HeadTrackingHipProfile` (sign correctness, proportional scaling by weight, non-negative clamp behaviour,
     epsilon-combined snap behaviour, and overload equivalence for profile evaluation entry points); and
     (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight
     application correctness, up/down full-response preservation, side-to-side partial-response verification
     at the authored default, forward/back minimal-response verification at the authored default,
     diagonal/interpolated response continuity, and +up/-up symmetry).
24. The Standing pose family is backed by a single `AnimationTree` state, `StandingCrouching`, whose
     sub-graph continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")` driven by a normalised scalar
     representing the full standing-to-crouching continuum. A single framework-level `StandingPoseState` resource
     maps to this `AnimationTree` state — there is no separate framework-level `CrouchingPoseState`. The `Idle` clip
     remains in the animation library as a deferred-but-supported extension point (for example, additive breathing
     layering) and is not wired into the tree for MVP.
25. The Standing→Kneeling transition uses an armed-then-retreat trigger model measured from the full-crouch baseline:
     - The trigger input is the forward-axis offset from the pose-neutral or full-crouch baseline, not total 3D head-offset magnitude.
     - The transition becomes armed after sufficient forward travel from the full-crouch baseline.
     - The transition fires only after retreating from the armed peak by a configurable amount (configurable `ArmedRetreatThreshold` style parameter).
     - This model prevents accidental firing during normal crouching and provides intentional player control.
26. The Standing→Kneeling transition is additionally gated by a crouch-depth threshold that must be satisfied before the
     transition can trigger. The gate requires the player to be at or near full crouch depth on the standing-to-crouching continuum before
     kneeling becomes reachable.
27. The kneeling pose (KneelingEnter, Kneeling, KneelingExit) is authored as separate AnimationTree state-machine nodes
     using normal animation playback. The AnimationTree uses authored auto-advance for transition clips (for example
     `KneelingEnter → Kneeling`, `KneelingExit → StandingCrouching`) rather than per-tick TimeSeek scrubbing.
     Transition Resources may own AnimationTree travel into their authored transition states when they fire.
28. Following any kneeling transition, both transition directions (Standing→Kneeling and Kneeling→Standing) remain locked
     until the forward-axis offset returns near the neutral or full-crouch baseline (configurable `NeutralReturnMaxOffsetRatio`
     style contract). This prevents immediate bounce-back and re-triggering.
29. The kneeling transition thresholds use normalised ratios derived from rest-pose body measures, not absolute metres.
     At minimum, the head-height measure from rest pose (`RestHeadHeight`) defines the reference for normalised crouch-depth gates.
     Tunable parameters use flexible ratios (for example 0.85 × rest-head-height) rather than fixed absolute values.
30. The Kneeling→Standing return transition uses the same armed-then-retreat model:
     - measured from the full-crouch baseline,
     - arms after sufficient forward travel from that baseline,
     - fires after retreating from the armed peak by a configurable amount.
     This provides bidirectional standing-continuum↔kneel transitions.
31. The state machine must include a new `AllFoursPoseState` resource that provides the all-fours crawling pose behaviour.
    This state is distinct from the previously defined crawling (all fours) in that it implements a structured internal
    state machine with two phases: `transitioning` and `crawling`.
32. The AllFours pose must be reachable as a transition target from both `StandingPoseState` and `KneelingPoseState`.
    Both transitions use a forward-travel-beyond-armed-point model with the forward-axis offset as the trigger signal,
    but with a distinct threshold range calibrated for the all-fours entry gesture.
33. The AllFours entry trigger uses head forward offset as the primary signal:
    - The transition becomes armed when the head's normalised forward offset from the skeleton's local origin exceeds a
      configurable threshold (default range start: 0.42).
    - The transition fires when the player continues forward past the armed point by a configurable additional margin
      (configurable `AllFoursForwardContinueThreshold`).
    - The forward offset is normalised using rest-pose body measures, mapping the range 0.42 to 0.73 to the animation seek
      window.
34. `AllFoursPoseState` implements two internal sub-states:
    - `transitioning`: active immediately upon entering AllFours, drives the entry animation via `AnimationNodeTimeSeek`.
    - `crawling`: active after the entry animation completes, holds the crawl pose.
35. In the `transitioning` sub-state, the state drives an `AnimationNodeTimeSeek` node using animation `All Fours-enter`
    with a custom seek window spanning 1.2 seconds to 3.5417 seconds.
36. The seek position in the entry animation is calculated from the head's normalised forward offset:
    - Normalise the forward offset to the range [0.42, 0.73] using the formula:
      `seekPosition = (headForwardOffset - 0.42) / (0.73 - 0.42)`.
    - Map the normalised value to the seek window duration [1.2, 3.5417] seconds.
37. When the head forward offset exceeds 0.73, the internal state transitions from `transitioning` to `crawling`.
    The `crawling` sub-state plays the looping animation `All Fours`.
38. While in the `crawling` sub-state, if the head's vertical offset increases above a configurable threshold
    (default: 0.3, expressed as a normalised ratio of rest head height), the state returns to `transitioning`.
39. While in the `transitioning` sub-state, if the head's forward offset decreases below 0.42 minus a configurable
    return margin (default margin: configurable `AllFoursReturnMargin`), the framework transitions back to
    `StandingPoseState` (the crouching baseline). The return destination is always Standing, not source-dependent.
40. AllFours threshold parameters (forward offset entry threshold, forward offset return threshold, vertical offset
    climb threshold, forward continue margin, return margin) must be configurable properties on the `AllFoursPoseState`
    or its associated transition resources.
41. The AllFours animation entry and crawl loops are authored as part of the AnimationTree, separate from the
    standing-family `TimeSeek` pattern. The entry uses `TimeSeek` scrubbing to match head position; the crawl
    hold uses standard animation playback.
42. AllFours hip reconciliation uses the state-defined reference baseline per the generic hip reconciliation contract.
    The AllFours pose family may supply a distinct hip reconciliation profile or derive from existing profiles
    as appropriate for the crawling posture.

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
| AC-07 | Hip reconciliation is specified as translation-centric, state-dependent behaviour with configurable limits on hip offsets from the state-defined reference baseline using an `OffsetLimits3D` resource type representing optional directional limits. Each directional limit is optional; unconfigured or unmarked limits are treated as unbounded. The architecture must support applying only the relevant bounds without requiring values for irrelevant directions. | Technical |
| AC-07e | Standing vertical clamping is intentionally single-sided per envelope: when in upright mode, only the upright upper bound (`UprightHipOffsetLimits.Up`) applies if authored — the `UprightHipOffsetLimits.Down` side is not synthesised if unconfigured and must not impose synthetic clamping. When in crouched mode, only the crouched lower bound (`CrouchedHipOffsetLimits.Down`) applies if authored — the `CrouchedHipOffsetLimits.Up` side is not synthesised if unconfigured and must not impose synthetic clamping. A side that is authored in only one envelope remains active across the full standing-to-crouching continuum and stays anchored to that envelope's reference (upright uses the upright/rest reference, crouched uses the full-crouch reference). Only sides authored in both envelopes interpolate across the continuum. Vertical clamping occurs only when exceeding the relevant bound for the current continuum position. | Technical |
| AC-07a | Hip reconciliation profiles may use configurable `OffsetLimits3D` limits to limit (clamp) the final hip offset from the state-defined reference, not just sub-contributions. | Technical |
| AC-07b | Residual (desired hip offset minus applied/clamped hip offset) must be propagated into head-target limiting before IK solve. | Technical |
| AC-07c | XR-origin compensation in `PlayerVRIK.OnEndStage` must absorb the remaining mismatch between virtual head target and physical headset when hip limiting reduces head-target motion. | Technical |
| AC-07d | Players are protected from extreme hip deformation beyond configurable state-defined limits, preventing unnatural body proportions. | User |
| AC-08 | Transition contracts define linear clip + `TimeSeek` as the default for long continuous transitions and permit state-specific non-linear exceptions. | Technical |
| AC-09 | The spec does not require a mandatory catch-all ambiguity state for this phase. | Technical |
| AC-10 | Across supported MVP movement scenarios, players see coherent visible full-body pose continuity while moving between required pose states. | User |
| AC-10b | Standing-family hip reconciliation must allow more natural stoop/lean poses by reducing hip travel for motion perpendicular to the hip rest up axis, while preserving strong vertical crouch response; players must experience smooth transition between reduced perpendicular response and full aligned response, without abrupt discontinuities. | User |
| AC-10c | Standing-family reconciliation must keep strong vertical hip drop during crouch where head remains roughly above hips. | User |
| AC-10d | Standing-family reconciliation must avoid chasing the head down when the head leads forward (stoop) or backward (lean-back); hips should stay upright. | User |
| AC-11 | Each pose state binds both animation selection (or AnimationTree parameter control) and hip reconciliation behaviour as a coupled responsibility. | Technical |
| AC-12 | State switching relies on inferred signals from IK-target transforms and runtime state; explicit button-based pose switching is avoided by default. | User + Technical |
| AC-13 | State and transition Resources expose a public extension surface permitting developer-supplied states and classifiers without editing core source. | Technical |
| AC-14 | The state machine evaluates per tick from an immutable read-only context snapshot that is the canonical input surface for classifiers, transitions, states, and hip reconciliation profiles, including `HeadTargetTransform`, `HeadTargetRestTransform`, `LeftHandTargetTransform`, `RightHandTargetTransform`, `AnimationTree`, rest-pose body measures (for example `RestHeadHeight`), tick delta, and auxiliary-signals lookup. The context carries the runtime `AnimationTree` so transition/state logic may access debugging helpers and animation services. | Technical |
| AC-15 | Runtime responsibilities may be split into a `PoseStateMachine` node and a `HipReconciliationModifier` (`SkeletonModifier3D`) as the canonical pattern, without mandating that specific split, provided AC-HR-07 ordering is preserved and `Tick` runs after follower updates, before downstream consumers read pending hip translation, and may use begin-stage modifier-callback flow (for example `PlayerVRIK` via `StageModifier`) or equivalent topology preserving this ordering. | Technical |
| AC-16 | Animation control for pose states is owned by `PoseState` resources (not a separate animation-binding abstraction). Each `PoseState` resource provides lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) driven by the state-machine runtime, plus a startup-only `Start(AnimationTree)` method that is called only once after initial-state resolution to seed the initial animation state. Per-frame state animation work flows through lifecycle callbacks, not through `Tick(PoseStateContext)`. | Technical |
| AC-17 | State and transition identifiers are authored as `StringName`; internal selection may use `StringName` or `string` provided identity semantics are preserved. | Technical |
| AC-18 | Hip reconciliation profiles return an absolute hip target position in skeleton-local space as a nullable value (`Vector3?`); returning `null` leaves the animated hip pose untouched. | Technical |
| AC-19 | Hip reconciliation profiles compute the hip target solely from pose-state-specific heuristics plus the current head position and rig rest-pose geometry, and must not read or depend on the currently animated hip bone pose. | Technical |
| AC-20 | For the Standing pose family's default profile is `HeadTrackingHipProfile`, combining (a) positional head offset, (b) per-axis positional modulation decomposed into up/down, side-to-side, and forward/back components in the hip rest local frame, (c) alignment-based vertical damping applied to the vertical component, and (d) rotational offset (from rest→current head orientation delta plus rest neck→head geometry) with opposite-direction hip compensation to mitigate unnatural neck bending. Per-axis weights are independently configurable: `VerticalPositionWeight` (default `1.0`, full offset), `LateralPositionWeight` (default `0.5`, 50 % offset), and `ForwardPositionWeight` (default `0.1`, 10 % offset). The alignment-based vertical damping applies a configurable `MinimumAlignmentWeight` (default `0.1`, clamped to `[0,1]`) that scales the vertical component when the head is not vertically aligned with the hips: `alignment = |dot(normalise(headDirection), hipRestUpLocal)|` and `verticalScaled = verticalComponent * VerticalPositionWeight * Mathf.Lerp(MinimumAlignmentWeight, 1.0, alignment)`. Pure vertical crouch (alignment ≈ 1) preserves full vertical response; forward lean or backward lean reduces vertical hip travel to avoid chasing the head down. The alignment uses `Mathf.Abs` for +up/-up symmetry. Degenerate case: if headDirection or hipRestUpLocal is near zero, fall back to alignment = 1.0. The rest up axis is derived from the hip bone's rest-pose or global-rest basis in skeleton-local space. The combined weights produce strong vertical crouch response and reduced lateral/forward-back hip travel, preserving +up/-up symmetry. All parameters are clamped to the `[0, 1]` range and independently configurable; the defaults above represent the authored standing profile and may be overridden in derived resources. Rotational contribution magnitude is configurable via `RotationCompensationWeight`, clamped to non-negative values, with no mandatory fixed default in this spec. | Technical |
| AC-21 | Unit-level regression tests cover both: (a) the weighted `HeadTrackingHipProfile` rotational-compensation contract (sign correctness, proportional weight scaling, non-negative weight clamp behaviour, epsilon-combined snap behaviour, and overload equivalence); and (b) the per-axis positional-modulation contract (rest-up-axis derivation correctness, per-axis weight application correctness, up/down full-response preservation, side-to-side partial-response verification at the authored default, forward/back minimal-response verification at the authored default, diagonal/interpolated response continuity, and +up/-up symmetry). | Technical |
| AC-21b | Unit-level regression tests cover the alignment-damping contract: high-alignment (pure vertical crouch) preserves full vertical response, low-alignment (forward stoop or lean-back) applies vertical damping down to `MinimumAlignmentWeight`, `MinimumAlignmentWeight` is clamped to `[0,1]`, diagonal offsets preserve per-axis weighting alongside alignment damping, and +up/-up symmetry under alignment damping is verified. | Technical |
| AC-22 | The Standing pose family is backed by a single `AnimationTree` state (`StandingCrouching`) running `TimeSeek → AnimationNodeAnimation("Crouch-seek")`, with a single framework-level `StandingPoseState` resource mapping to this `AnimationTree` state. There is no separate framework-level CrouchingPoseState; the standing-to-crouching continuum is covered by one StandingPoseState. | Technical |
| AC-23 | The `Idle` clip remains in the animation library as a deferred-but-supported extension point for future layering (for example additive breathing) and is not wired into the `AnimationTree` for MVP. | Technical |
| AC-24 | The Standing→Kneeling transition uses an armed-then-retreat trigger model measured from the full-crouch baseline: the transition becomes armed after sufficient forward travel from the baseline, and fires only after retreating from the armed peak by a configurable amount. Both directions use the same model. | User |
| AC-24b | The Standing→Kneeling transition is additionally gated by a crouch-depth threshold that requires near-full crouch on the standing-to-crouching continuum before kneeling becomes reachable. | Technical |
| AC-25 | The kneeling pose (KneelingEnter, Kneeling, KneelingExit) is authored as separate AnimationTree state-machine nodes using normal animation playback. The AnimationTree uses authored auto-advance for transition clips (for example KneelingEnter → Kneeling, KneelingExit → StandingCrouching) rather than per-tick TimeSeek scrubbing. | Technical |
| AC-26 | Following any kneeling transition, both transition directions remain locked until the forward-axis offset returns near the neutral or full-crouch baseline, preventing immediate bounce-back and re-triggering. | User + Technical |
| AC-26a | The kneeling transition thresholds use normalised ratios derived from rest-pose body measures, not absolute metres. At minimum, the head-height measure from rest pose defines the reference for the normalised crouch-depth gate. Tunable parameters use flexible ratios (for example 0.85 × RestHeadHeight) rather than fixed absolute values. | Technical |
| AC-26b | The Kneeling→Standing return transition uses the same armed-then-retreat model: measured from the full-crouch baseline, arms after sufficient forward travel from that baseline, fires after retreating from the armed peak by a configurable amount, providing bidirectional standing-continuum↔kneel transitions. | Technical |
| AC-27 | Foot-target synchronisation stage re-synchronises foot IK targets from animated foot transforms (position + rotation) at the beginning of each leg IK solve cycle, before pole-target computation. `FootTargetSyncController` must run before `HipReconciliationModifier` and any other foot-mutating modifiers. This ensures deterministic solve behaviour when animation timing or `TimeSeek` position changes. Ordering is a scene/pipeline authoring contract — runtime auto-reordering is intentionally not used. See IK-003 AC-05a. | User + Technical |
| AC-27a | The state machine includes an `AllFoursPoseState` resource providing all-fours crawling behaviour with two internal sub-states: `transitioning` and `crawling`. | Technical |
| AC-27b | AllFours pose is reachable via transition from both `StandingPoseState` and `KneelingPoseState` using a forward-travel-beyond-armed-point model triggered by head forward offset. | User |
| AC-27c | In the `transitioning` sub-state, the state drives an `AnimationNodeTimeSeek` node using animation `All Fours-enter` with a custom seek window spanning 1.2s to 3.5417s, where seek position is calculated from the head's normalised forward offset mapping the range 0.42 to 0.73. | Technical |
| AC-27d | When the head forward offset exceeds 0.73, the internal state changes from `transitioning` to `crawling`, playing the looping animation `All Fours`. | User + Technical |
| AC-27e | While in `crawling`, if the head's vertical offset rises above a configurable threshold (default 0.3 as normalised ratio), the state returns to `transitioning`. | User + Technical |
| AC-27f | While in `transitioning`, if the head's forward offset drops below 0.42 minus a configurable return margin, the pose transitions back to `StandingPoseState`. The return destination is always Standing regardless of source state. | User + Technical |
| AC-27g | AllFours threshold parameters (forward offset entry threshold 0.42, forward offset exit threshold 0.73, vertical offset climb threshold 0.3, forward continue margin, return margin) are configurable via `AllFoursPoseState` or transition resource properties. | Technical |

## Code-Spec Sync Note

Increment 2.3 is delivered alongside this specification revision. The shipped implementation includes all content from previous increments plus the following architectural contracts that this revision codifies:

### Animation Ownership Architecture

- `AnimationBinding` abstraction has been removed.
- Animation control for pose states is now owned by `PoseState` resources. Each `PoseState` resource provides lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) driven by the state-machine runtime, plus a startup-only `Start(AnimationTree)` method that is called only once after initial-state resolution to seed the initial animation state. Per-frame state animation work flows through lifecycle callbacks, not through `Tick(PoseStateContext)`.
- Animation control for transitions is owned by `PoseTransition` resources. Each `PoseTransition` resource provides lifecycle hooks that may own AnimationTree travel into authored transition states when they fire.
- `PoseStateMachine` permits a one-time startup exception: after resolving `InitialStateId`, it may call `PoseState.Start(AnimationTree)` once to seed the initial authored state. This startup path is exclusive to initial state setup and is not used during normal tick evaluation.
- Per-frame state animation work now flows through lifecycle callbacks (`OnEnter`/`OnUpdate`/`OnExit`), not through concrete-only post-processing. `PoseStateContext` carries the runtime `AnimationTree` as the canonical per-tick input surface for transitions, states, hip reconciliation, and debugging helpers.

### Standing/Crouching Animation Authoring

- Standing family remains a single `StandingPoseState` mapped to the `StandingCrouching` AnimationTree state.
- That state uses `TimeSeek → AnimationNodeAnimation("Crouch-seek")` for the continuous standing↔crouching continuum.
- There is no separate framework-level crouching state.

### Kneeling Animation Authoring and Transitions

- Kneeling entry/hold/exit are authored as separate AnimationTree state-machine nodes (`KneelingEnter`, `Kneeling`, `KneelingExit`) using normal animation playback rather than per-tick TimeSeek scrubbing.
- The AnimationTree uses authored auto-advance for transition clips (for example `KneelingEnter → Kneeling`, `KneelingExit → StandingCrouching`).
- Transition Resources may own AnimationTree travel into their authored transition states when they fire.

### Armed-Then-Retreat Trigger Model

- Both Standing→Kneeling and Kneeling→Standing use the same armed-then-retreat trigger model:
  - transitions are measured from the full-crouch forward baseline,
  - arming occurs after sufficient forward travel from that baseline,
  - firing occurs only after retreat from the armed peak by a configurable amount,
  - both directions are normalized by rest-pose body measures (at minimum `RestHeadHeight`).
- The Standing→Kneeling path has a crouch-depth gate requiring near-full crouch before kneeling becomes reachable.
- The trigger model is driven by the forward-axis offset from the pose-neutral/full-crouch baseline, not by total 3D head-offset magnitude.
- After a transition fires, both directions are locked out until the forward-axis offset returns near the neutral/full-crouch baseline (`NeutralReturnMaxOffsetRatio` style contract). This prevents immediate bounce-back and re-triggering.
Hip reconciliation profiles return an absolute hip target position in skeleton-local
space (`Vector3?`), with `null` meaning "do not override the animated hip bone". Unit regression coverage now includes
per-axis positional modulation (rest-up-axis derivation, per-axis weight application, up/down full response, side-to-side at
authored default, forward/back at authored default, diagonal continuity, +up/-up symmetry)
and weighted rotational compensation maths (sign, scaling, non-negative clamp, epsilon-combined snap, and overload equivalence).

**Spec-to-code sync note — hip reconciliation positional weighting:** This specification revision specifies per-axis
configurable weights (`VerticalPositionWeight`, `LateralPositionWeight`, `ForwardPositionWeight`) applied to the
head-position offset decomposed in the hip rest local frame. Authored defaults for the standing profile are
`1.0` / `0.5` / `0.1` respectively. Additionally, alignment-based vertical damping is applied using a
configurable `MinimumAlignmentWeight` parameter (default `0.1`, clamped to `[0,1]`). The alignment
computation is `alignment = |dot(normalise(headDirection), hipRestUpLocal)|` with `alignmentWeight =
Mathf.Lerp(MinimumAlignmentWeight, 1.0, alignment)` applied to the vertical component: `verticalScaled =
verticalComponent * VerticalPositionWeight * alignmentWeight`. This dampens the vertical hip response when the head
leads forward (stoop) or backward (lean-back) while preserving full vertical response during pure crouch.
The alignment uses `Mathf.Abs` for +up/-up symmetry. Degenerate case: if headDirection or hipRestUpLocal is
near zero, fall back to `alignment = 1.0`. The combined weights produce strong vertical crouch response and reduced
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
