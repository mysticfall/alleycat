# Pose State Machine Contract

## Purpose

Define the implementation contract for IK-004 pose-state orchestration and transitions, including locomotion permission contributions.

## Requirement

Provide a framework-first pose-state machine contract that supports MVP state coverage now and extensibility later, while providing locomotion permission outputs to external systems.

## Goal

Ensure implementers can deliver stable pose classification and transition flow without hard-coding final tuning values, while also providing safe movement gating for non-standing poses.

## User Requirements

1. Pose changes must feel continuous across common player-driven movement.
2. MVP pose states must remain available and selectable across supported input conditions.
3. Players must be able to transition from crouching to kneeling using an armed-then-retreat trigger model:
   the transition initiates from the full-crouch baseline, becomes armed after sufficient forward travel from that baseline
   (measured in head-offset space), and fires only after retreating from the armed peak by a configurable amount.
   This provides intentional player control and prevents accidental firing.
4. The Standing→Kneeling transition must require a crouch-depth gate—near-full crouch on the standing-to-crouching continuum—before kneeling becomes reachable.
5. Players must be able to transition from kneeling back to standing using the same armed-then-retreat model:
   the return path is measured from the full-crouch baseline, arms after sufficient forward travel from that baseline, and fires only after retreating from the armed peak by a configurable amount.
   This return transition is bidirectional with the forward kneeling transition.
6. Following any kneeling transition (forward or reverse), both transition directions must remain locked until the forward-axis offset returns to near the neutral or full-crouch baseline.
   This prevents immediate bounce-back and re-triggering.
7. Kneeling transition thresholds must use normalised ratios derived from rest-pose body measures, not absolute metres.
   At minimum, the head-height measure from rest pose (`RestHeadHeight`) defines the reference for the normalised crouch-depth gate.
8. Players must be able to transition from the Standing pose to an all-fours crawling pose by moving the head forward
   beyond a head forward offset threshold, where the transition becomes armed once the head's forward offset reaches a
   configurable threshold and fires when the player continues forward past that armed point by a configurable additional margin.
9. Players must be able to transition from the Kneeling pose to an all-fours crawling pose by moving the head forward
   beyond a head forward offset threshold, where the transition becomes armed once the head's forward offset reaches a
   configurable threshold and fires when the player continues forward past that armed point by a configurable additional margin.
10. When in AllFours, players must experience a smooth transition animation from the entering position to the crawling hold pose,
    driven by the head's forward offset position.
11. While crawling on all fours, if the player raises their head vertically above a configurable threshold, they must
    transition back to the entering (transitioning) state to prepare for a return to standing.
12. While in the AllFours transitioning state, if the player's head forward offset drops below a configurable return threshold,
    the pose must automatically transition back to the Standing pose (crouching baseline) rather than to kneeling.
13. **Movement must be restricted when the player is in a pose that does not support walking** (for example, kneeling, sitting, crawling).
14. **Rotation must remain available across all poses for MVP.**

## Technical Requirements

### State Machine Core

1. The state machine must include explicit contracts for these states:
   - Standing (covering the standing-to-crouching continuum)
   - Kneeling
   - Stooping
   - Sitting
   - Crawling (all fours)
2. Each pose state must be represented by a Godot `Resource` containing state metadata and customisable transition
   configuration hooks.
3. Transition definitions must be resource-compatible so they can be authored or replaced per state pair without
   rewriting the runtime state-machine core.
4. State-specific disambiguation between overlapping poses (for example stoop vs crouch) is a permanent classifier
   responsibility, resolved using auxiliary IK-target signals such as head pitch and hand height from the pose-state
   context. Only the initial tuning values are rudimentary; the disambiguation responsibility itself is not a temporary
   placeholder, and the architecture must preserve upgrade paths for more advanced classifiers and tuning.
5. Input signals for state classification in this phase are limited to:
   - head IK-target transform data (`HeadTargetTransform`, `HeadTargetRestTransform`),
   - hand IK-target transform data (`LeftHandTargetTransform`, `RightHandTargetTransform`),
   - internal runtime values,
   - animation-derived values.
6. Collision probes, locomotion-system outputs, and external tracking systems are optional future extensions and must
   not be required dependencies in this contract.
7. No mandatory global ambiguity state is required in this phase; ambiguity handling may be local to transitions or
   state-selection logic.
8. Each state Resource must declare the animation control it activates. Animation control is owned by the `PoseState` resource
    via lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) driven by the state-machine runtime, plus a startup-only
    `Start(AnimationTree)` method called only once after initial-state resolution to seed the initial animation state.
    Animation selection and hip reconciliation configuration travel together on the same state Resource.
9. State classification must not rely on explicit button input; it must be derived from IK-target transforms
   (including auxiliary signals such as head pitch and hand height from `PoseStateContext`) and animation/runtime state. Explicit
   button input is permitted only as a last-resort fallback for a specific transition with no viable automatic signal.
10. The state-machine core must expose a public extension surface — subclassable `PoseState` and `PoseTransition`
    resources plus pluggable classifier/evaluator interfaces — so consumers may add or replace pose behaviour without
    editing core state-machine source.
11. The state machine must evaluate each tick from a per-tick read-only context snapshot (`PoseStateContext`).
    The context is the canonical input surface for classifiers, transitions, states, and hip reconciliation profiles, and must include at minimum:
    - `HeadTargetTransform` (current head IK-target global transform)
    - `LeftHandTargetTransform` (left hand IK-target global transform)
    - `RightHandTargetTransform` (right hand IK-target global transform)
    - `HeadTargetRestTransform` (head IK-target global rest transform)
    - `AnimationTree` (the runtime animation tree for this player, enabling transition/state logic
      to drive animation or access debugging helpers from context)
    - Rest-pose body measures (for example `RestHeadHeight`) for ratio-based threshold computation
    - world scale
    - skeleton reference and the bone indices required for IK-004 (hip, head)
    - tick delta
    - an auxiliary-signals lookup (for example keyed by `StringName`) for extensible computed values such as head pitch

    The context must be immutable for the duration of a tick so classifiers, transitions, states, and profiles cannot observe
    inconsistent state. Producing the context without per-frame heap pressure is a quality goal, not a hard threshold.
12. Runtime responsibilities may be split across two cooperating nodes:
    - a `PoseStateMachine` (`Node`) that runs `Tick(context)` per frame, evaluates transitions, applies the active
      state's animation control, and computes a pending hip translation;
    - a `HipReconciliationModifier` (`SkeletonModifier3D`) that applies the pending hip translation inside the skeleton
      modifier pipeline.

    `Tick` must run once per frame after follower updates have produced current tracked transforms, and before
    downstream consumers read the pending hip translation. This may be driven by a deliberate non-modifier runtime
    node (for example an integrating parent node) or by a begin-stage modifier-callback topology (for example
    `PlayerVRIK` begin-stage flow invoked by `StageModifier`, a `SkeletonModifier3D`) so multiple modifiers may
    consume one tick result in the same frame. Hip reconciliation execution still happens inside a
    `SkeletonModifier3D` pass after the animation player (see Hip Reconciliation Contract AC-HR-07).
    This split is permitted as the canonical pattern but is not mandated; any equivalent topology that preserves the
    ordering contract is acceptable.

### Animation Control

13. Animation control for pose states is now owned by `PoseState` resources, not by a separate animation-binding abstraction.
    Each `PoseState` resource provides:
    - `Start(AnimationTree)` - called once after initial-state resolution to seed the initial animation state (startup-only)
    - Lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) - called per-frame/state-change to drive animation work
    - Properties declaring the hip reconciliation profile for this state

    Per-frame state animation work flows through lifecycle callbacks. The standing pose family uses this pattern.
14. Animation control for transitions is owned by `PoseTransition` resources. Each `PoseTransition` resource may:
    - Own AnimationTree travel into its authored transition states when it fires
    - Provide lifecycle hooks for pre/post transition handling

    `PoseTransition` resources may drive AnimationTree state changes directly.
15. `PoseStateMachine` permits a one-time startup exception: after resolving `InitialStateId`, it may call
    `PoseState.Start(AnimationTree)` once to seed the initial authored state. This startup path is exclusive
    to initial state setup and is not used during normal tick evaluation.
16. Transition Resources must support optional lifecycle hooks invoked in this order around a state switch:
    `OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`. The state machine must emit a
    state-changed observation (signal or event) so consumers can react.
17. State and transition identifiers are authored as `StringName` in editor contexts for designer ergonomics. The
    internal selection layer may use either `StringName` or `string` for testability, provided identity semantics are
    preserved.

### Standing Pose Family

18. The Standing pose family uses a single `AnimationTree` state, `StandingCrouching`, whose sub-graph
    continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")`. This is the canonical authoring pattern for a
    "continuous player-driven pose continuum": a single `AnimationTree` state spanning the continuum with `TimeSeek`
    driven by a normalised scalar. A single framework-level `StandingPoseState` resource maps to this `AnimationTree`
    state — there is no separate framework-level `CrouchingPoseState`. The standing-to-crouching continuum is
    covered by one standing-family `PoseState` resource.
19. The `Idle` clip remains in the animation library for future layering (for example, an additive breathing blend on
    top of the `StandingCrouching` sub-graph) but is not wired into the tree for MVP. This is a deferred-but-supported
    extension point, not a dropped feature.

### Kneeling Transitions

20. The Standing→Kneeling transition uses an armed-then-retreat trigger model measured from the full-crouch baseline:
    - The trigger input is the forward-axis offset from the pose-neutral or full-crouch baseline, not total 3D head-offset magnitude.
    - The transition becomes armed after sufficient forward travel from the full-crouch baseline.
    - The transition fires only after retreating from the armed peak by a configurable amount (configurable `ArmedRetreatThreshold` style parameter).
    - This model prevents accidental firing during normal crouching and provides intentional player control.
21. The Standing→Kneeling transition is additionally gated by a crouch-depth threshold that must be satisfied before the
    transition can trigger. The gate requires the player to be at or near full crouch depth on the standing-to-crouching continuum.
22. The kneeling pose (KneelingEnter, Kneeling, KneelingExit) is authored as separate AnimationTree state-machine nodes
    using normal animation playback. The AnimationTree uses authored auto-advance for transition clips (for example
    `KneelingEnter → Kneeling`, `KneelingExit → StandingCrouching`) rather than per-tick TimeSeek scrubbing.
    Transition Resources may own AnimationTree travel into their authored transition states when they fire.
23. Following any kneeling transition, both transition directions (Standing→Kneeling and Kneeling→Standing) remain locked
    until the forward-axis offset returns near the neutral or full-crouch baseline (configurable `NeutralReturnMaxOffsetRatio`
    style contract). This prevents immediate bounce-back and re-triggering.
24. The kneeling transition thresholds use normalised ratios derived from rest-pose body measures, not absolute metres.
    At minimum, the head-height measure from rest pose (`RestHeadHeight`) must define the reference for the normalised crouch-depth gate.
    Tunable parameters use flexible ratios (for example `0.85 × RestHeadHeight`) rather than fixed absolute values.
25. The Kneeling→Standing return transition uses the same armed-then-retreat model:
     - measured from the full-crouch baseline,
     - arms after sufficient forward travel from that baseline,
     - fires after retreating from the armed peak by a configurable amount.
     This provides bidirectional standing-continuum↔kneel transitions.

### AllFours Pose

26. The state machine must include a new `AllFoursPoseState` resource that provides the all-fours crawling pose behaviour.
    This state is distinct from the previously defined crawling (all fours) in that it implements a structured internal
    state machine with two phases: `transitioning` and `crawling`.
27. The AllFours pose must be reachable as a transition target from both `StandingPoseState` and `KneelingPoseState`.
    Both transitions use a forward-travel-beyond-armed-point model with the forward-axis offset as the trigger signal,
    but with a distinct threshold range calibrated for the all-fours entry gesture.
28. The AllFours entry trigger uses head forward offset as the primary signal:
    - The transition becomes armed when the head's normalised forward offset from the skeleton's local origin exceeds a
      configurable threshold (default range start: 0.42).
    - The transition fires when the player continues forward past the armed point by a configurable additional margin
      (configurable `AllFoursForwardContinueThreshold`).
    - The forward offset is normalised using rest-pose body measures, mapping the range 0.42 to 0.73 to the animation seek
      window.
29. `AllFoursPoseState` implements two internal sub-states:
    - `transitioning`: active immediately upon entering AllFours, drives the entry animation via `AnimationNodeTimeSeek`.
    - `crawling`: active after the entry animation completes, holds the crawl pose.
30. In the `transitioning` sub-state, the state drives an `AnimationNodeTimeSeek` node using animation `All Fours-enter`
    with a custom seek window spanning 1.2 seconds to 3.5417 seconds.
31. The seek position in the entry animation is calculated from the head's normalised forward offset:
    - Normalise the forward offset to the range [0.42, 0.73] using the formula:
      `seekPosition = (headForwardOffset - 0.42) / (0.73 - 0.42)`.
    - Map the normalised value to the seek window duration [1.2, 3.5417] seconds.
32. When the head forward offset exceeds 0.73, the internal state transitions from `transitioning` to `crawling`.
    The `crawling` sub-state plays the looping animation `All Fours`.
33. While in the `crawling` sub-state, if the head's vertical offset increases above a configurable threshold
    (default: 0.3, expressed as a normalised ratio of rest head height), the state returns to `transitioning`.
34. While in the `transitioning` sub-state, if the head's forward offset decreases below 0.42 minus a configurable
    return margin (default margin: configurable `AllFoursReturnMargin`), the framework transitions back to
    `StandingPoseState` (the crouching baseline). The return destination is always Standing, not source-dependent.
35. AllFours threshold parameters (forward offset entry threshold, forward offset return threshold, vertical offset
    climb threshold, forward continue margin, return margin) must be configurable properties on the `AllFoursPoseState`
    or its associated transition resources.
36. The AllFours animation entry and crawl loops are authored as part of the AnimationTree, separate from the
    standing-family `TimeSeek` pattern. The entry uses `TimeSeek` scrubbing to match head position; the crawl
    hold uses standard animation playback.

### Locomotion Permission Sources

37. **The pose-state-machine must implement `ILocomotionPermissionSource`** and serve as a locomotion permission provider.
    - The machine acts as a single permission source that delegates to the currently active pose state.
    - Each tick, the machine queries the active pose state for its locomotion permissions.

38. **Each `PoseState` must provide locomotion permissions** via `GetLocomotionPermissions(PoseStateContext context)`:
    - Default implementation returns `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation).
    - Subclasses may override to provide different permission behaviour.

39. **Standing pose family (`StandingPoseState`) permissions**:
    - Allows movement only when the standing/crouching blend is at or below a configurable `MovementAllowedMaximumPoseBlend` threshold.
    - Default threshold is 0.1 (near fully upright).
    - Allows rotation in all cases.

40. **Non-standing pose permissions**:
    - Kneeling, sitting, crawling (all fours), stooping: return `LocomotionPermissions.RotationOnly`.
    - Blocks movement while allowing rotation.

41. **Permission aggregation**:
    - External systems (for example locomotion component) aggregate permissions across all sources using logical AND.
    - All sources must permit a permission for it to be granted.

42. **Runtime permission query**:
    - The pose-state-machine exposes its `ILocomotionPermissionSource` contract to consumers.
    - Consumers read `ILocomotionPermissionSource.LocomotionPermissions` each tick to get the aggregate permissions.