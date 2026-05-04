# Pose State Machine Contract

## Purpose

Define the implementation contract for the pose-state orchestration framework, including state machine architecture, animation binding, transition mechanics, and locomotion permission contributions.

## Requirement

Provide a framework-first pose-state machine contract that supports MVP state coverage now and extensibility later, while providing locomotion permission outputs to external systems.

## Goal

Ensure implementers can deliver stable pose classification and transition flow without hard-coding final tuning values, while also providing safe movement gating for non-standing poses.

## Specification Scope

This contract covers the framework-level architecture. Detailed per-state behaviour requirements are defined in dedicated child pages:

- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)

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
13. **Movement must be restricted when the player is in a pose that does not support walking** (for example, kneeling, sitting, all-fours transitioning).
14. **Rotation must remain available across all poses for MVP.**

## Technical Requirements

### State Machine Core

1. The framework includes contracts for these pose families:
   - Standing (covering the standing-to-crouching continuum) — **implemented**: [Standing Pose State](standing-pose-state.md)
   - Kneeling — **implemented**: [Kneeling Pose State](kneeling-pose-state.md)
   - Stooping (framework coverage for future extension)
   - Sitting (framework coverage for future extension)
   - Crawling (all fours) — **implemented**: [AllFours Pose State](all-fours-pose-state.md)
2. Each pose state must be represented by a Godot `Resource` containing state metadata and customisable transition configuration hooks.
3. Transition definitions must be resource-compatible so they can be authored or replaced per state pair without rewriting the runtime state-machine core.
4. State-specific disambiguation between overlapping poses (for example stoop vs crouch) is a permanent classifier responsibility.
5. Input signals for state classification in this phase are limited to:
   - head IK-target transform data (`HeadTargetTransform`, `HeadTargetRestTransform`)
   - hand IK-target transform data (`LeftHandTargetTransform`, `RightHandTargetTransform`)
   - internal runtime values
   - animation-derived values
6. Collision probes, locomotion-system outputs, and external tracking systems are optional future extensions and must not be required dependencies.
7. Each state Resource must declare the animation control it activates via lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) driven by the state-machine runtime, plus a startup-only `Start(AnimationTree)` method.
8. State classification must not rely on explicit button input; it must be derived from IK-target transforms and animation/runtime state.
9. The state-machine core must expose a public extension surface — subclassable `PoseState` and `PoseTransition` resources plus pluggable classifier/evaluator interfaces.

### Pose State Context

10. The state machine must evaluate each tick from a per-tick read-only context snapshot (`PoseStateContext`).
11. The context is the canonical input surface for classifiers, transitions, states, and hip reconciliation profiles, and must include at minimum:
    - `HeadTargetTransform` (current head IK-target global transform)
    - `LeftHandTargetTransform` (left hand IK-target global transform)
    - `RightHandTargetTransform` (right hand IK-target global transform)
    - `HeadTargetRestTransform` (head IK-target global rest transform)
    - `AnimationTree` (the runtime animation tree for this player)
    - Rest-pose body measures (for example `RestHeadHeight`) for ratio-based threshold computation
    - world scale
    - skeleton reference and the bone indices required for IK-004 (hip, head)
    - tick delta
    - an auxiliary-signals lookup for extensible computed values
12. The context must be immutable for the duration of a tick so classifiers, transitions, states, and profiles cannot observe inconsistent state.

### Runtime Topology

13. Runtime responsibilities may be split across two cooperating nodes:
    - a `PoseStateMachine` (`Node`) that runs `Tick(context)` per frame, evaluates transitions, applies the active state's animation control, and computes a pending hip translation
    - a `HipReconciliationModifier` (`SkeletonModifier3D`) that applies the pending hip translation inside the skeleton modifier pipeline
14. `Tick` must run once per frame after follower updates have produced current tracked transforms, and before downstream consumers read the pending hip translation.
15. Hip reconciliation execution happens inside a `SkeletonModifier3D` pass after the animation player.

### Animation Control Framework

16. Animation control for pose states is owned by `PoseState` resources, not by a separate animation-binding abstraction.
17. Each `PoseState` resource provides:
    - `Start(AnimationTree)` — called once after initial-state resolution to seed the initial animation state
    - Lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) — called per-frame/state-change to drive animation work
    - Properties declaring the hip reconciliation profile for this state

    Per-frame state animation work flows through lifecycle callbacks. The standing pose family uses this pattern.
18. Animation control for transitions is owned by `PoseTransition` resources. Each `PoseTransition` resource may:
    - Own AnimationTree travel into its authored transition states when it fires
    - Provide lifecycle hooks for pre/post transition handling

    `PoseTransition` resources may drive AnimationTree state changes directly.
19. `PoseStateMachine` permits a one-time startup exception: after resolving `InitialStateId`, it may call
    `PoseState.Start(AnimationTree)` once to seed the initial authored state. This startup path is exclusive
    to initial state setup and is not used during normal tick evaluation.
20. Transition Resources must support optional lifecycle hooks invoked in this order around a state switch:
    `OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`. The state machine must emit a
    state-changed observation (signal or event) so consumers can react.
21. State and transition identifiers are authored as `StringName` in editor contexts for designer ergonomics. The
    internal selection layer may use either `StringName` or `string` for testability, provided identity semantics are
    preserved.

### Standing Pose Family

22. The Standing pose family uses a single `AnimationTree` state, `StandingCrouching`, whose sub-graph
    continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")`. This is the canonical authoring pattern for a
    "continuous player-driven pose continuum": a single `AnimationTree` state spanning the continuum with `TimeSeek`
    driven by a normalised scalar. A single framework-level `StandingPoseState` resource maps to this `AnimationTree`
    state — there is no separate framework-level `CrouchingPoseState`. The standing-to-crouching continuum is
    covered by one standing-family `PoseState` resource.
23. The `Idle` clip remains in the animation library for future layering (for example, an additive breathing blend on
    top of the `StandingCrouching` sub-graph) but is not wired into the tree for MVP. This is a deferred-but-supported
    extension point, not a dropped feature.

### Kneeling Transitions

24. The Standing→Kneeling transition uses an armed-then-retreat trigger model measured from the full-crouch baseline:
    - The trigger input is the forward-axis offset from the pose-neutral or full-crouch baseline, not total 3D head-offset magnitude.
    - The transition becomes armed after sufficient forward travel from the full-crouch baseline.
    - The transition fires only after retreating from the armed peak by a configurable amount (configurable `ArmedRetreatThreshold` style parameter).
    - This model prevents accidental firing during normal crouching and provides intentional player control.
25. The Standing→Kneeling transition is additionally gated by a crouch-depth threshold that must be satisfied before the
    transition can trigger. The gate requires the player to be at or near full crouch depth on the standing-to-crouching continuum.
26. The kneeling pose (KneelingEnter, Kneeling, KneelingExit) is authored as separate AnimationTree state-machine nodes
    using normal animation playback. The AnimationTree uses authored auto-advance for transition clips (for example
    `KneelingEnter → Kneeling`, `KneelingExit → StandingCrouching`) rather than per-tick TimeSeek scrubbing.
    Transition Resources may own AnimationTree travel into their authored transition states when they fire.
27. Following any kneeling transition, both transition directions (Standing→Kneeling and Kneeling→Standing) remain locked
    until the forward-axis offset returns near the neutral or full-crouch baseline (configurable `NeutralReturnMaxOffsetRatio`
    style contract). This prevents immediate bounce-back and re-triggering.
28. The kneeling transition thresholds use normalised ratios derived from rest-pose body measures, not absolute metres.
    At minimum, the head-height measure from rest pose (`RestHeadHeight`) must define the reference for the normalised crouch-depth gate.
    Tunable parameters use flexible ratios (for example `0.85 × RestHeadHeight`) rather than fixed absolute values.
29. The Kneeling→Standing return transition uses the same armed-then-retreat model:
     - measured from the full-crouch baseline,
     - arms after sufficient forward travel from that baseline,
     - fires after retreating from the armed peak by a configurable amount.
     This provides bidirectional standing-continuum↔kneel transitions.

### AllFours Pose

30. The state machine must include a new `AllFoursPoseState` resource that provides the all-fours crawling pose behaviour.
    This state is distinct from the previously defined crawling (all fours) in that it implements a structured internal
    state machine with two phases: `transitioning` and `crawling`.
31. The AllFours pose must be reachable as a transition target from both `StandingPoseState` and `KneelingPoseState`.
    Both transitions use a forward-travel-beyond-armed-point model with the forward-axis offset as the trigger signal,
    but with a distinct threshold range calibrated for the all-fours entry gesture.
32. The AllFours entry trigger uses head forward offset as the primary signal:
    - The transition becomes armed when the head's normalised forward offset from the skeleton's local origin exceeds a
      configurable threshold (default range start: 0.42).
    - The transition fires when the player continues forward past the armed point by a configurable additional margin
      (configurable `AllFoursForwardContinueThreshold`).
    - The forward offset is normalised using rest-pose body measures, mapping the range 0.42 to 0.73 to the animation seek
      window.
33. `AllFoursPoseState` implements two internal sub-states:
    - `transitioning`: active immediately upon entering AllFours, drives the entry animation via `AnimationNodeTimeSeek`.
    - `crawling`: active after the entry animation completes, holds the crawl pose.
34. In the `transitioning` sub-state, the state drives an `AnimationNodeTimeSeek` node using animation `All Fours-enter`
    with a custom seek window spanning 1.2 seconds to 3.5417 seconds.
35. The seek position in the entry animation is calculated from the head's normalised forward offset:
    - Normalise the forward offset to the range [0.42, 0.73] using the formula:
      `seekPosition = (headForwardOffset - 0.42) / (0.73 - 0.42)`.
    - Map the normalised value to the seek window duration [1.2, 3.5417] seconds.
36. When the head forward offset exceeds 0.73, the internal state transitions from `transitioning` to `crawling`.
    The `crawling` sub-state plays the looping animation `All Fours`.
37. While in the `crawling` sub-state, if the head rises vertically above the height captured when crawl-hold became active by a configurable threshold
    (default: 0.3, expressed as a normalised ratio of rest head height), the state returns to `transitioning`.
    The exit signal is therefore a rise relative to the established crawl pose, not an absolute head-height gate that grounded crawl posture may already exceed.
38. While in the `transitioning` sub-state, if the head's forward offset decreases below 0.42 minus a configurable
    return margin (default margin: configurable `AllFoursReturnMargin`), the framework transitions back to
    `StandingPoseState` (the crouching baseline). The return destination is always Standing, not source-dependent.
39. AllFours threshold parameters (forward offset entry threshold, forward offset return threshold, vertical offset
    climb threshold, forward continue margin, return margin) must be configurable properties on the `AllFoursPoseState`
    or its associated transition resources.
40. The AllFours animation entry and crawl loops are authored as part of the AnimationTree, separate from the
    standing-family `TimeSeek` pattern. The entry uses `TimeSeek` scrubbing to match head position; the crawl
    hold uses standard animation playback.
41. **AllFours locomotion permissions**:
    - The `transitioning` sub-state returns `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation).
    - The `crawling` sub-state returns `LocomotionPermissions.Allowed` (permits both movement and rotation), enabling
      root-motion-driven crawl-hold locomotion.
42. **AllFours locomotion animation-state target**:
    - The `crawling` sub-state returns `LocomotionStateTarget(AllFours, AllFoursForward)` from `GetLocomotionStateTarget(PoseStateContext)`.
    - The `transitioning` sub-state returns `null` (no locomotion animation-state target).

### Locomotion Permission Sources

43. **The pose-state-machine must implement `ILocomotionPermissionSource`** and serve as a locomotion permission provider.
    - The machine acts as a single permission source that delegates to the currently active pose state.
    - Each tick, the machine queries the active pose state for its locomotion permissions.

44. **Each `PoseState` must provide locomotion permissions** via `GetLocomotionPermissions(PoseStateContext context)`:
    - Default implementation returns `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation).
    - Subclasses may override to provide different permission behaviour.

45. **Standing pose family (`StandingPoseState`) permissions**:
    - Allows movement only when the standing/crouching blend is at or below a configurable `MovementAllowedMaximumPoseBlend` threshold.
    - Default threshold is 0.1 (near fully upright).
    - Allows rotation in all cases.

46. **Non-standing pose permissions**:
    - Kneeling, sitting, stooping: return `LocomotionPermissions.RotationOnly`.
    - AllFours transitioning: returns `LocomotionPermissions.RotationOnly`.
    - AllFours crawling (crawl-hold): returns `LocomotionPermissions.Allowed`.
    - Blocks movement while allowing rotation for non-crawling non-standing poses.

47. **Permission aggregation**:
    - External systems (for example locomotion component) aggregate permissions across all sources using logical AND.
    - All sources must permit a permission for it to be granted.

48. **Runtime permission query**:
    - The pose-state-machine exposes its `ILocomotionPermissionSource` contract to consumers.
    - Consumers read `ILocomotionPermissionSource.LocomotionPermissions` each tick to get the aggregate permissions.

### Locomotion Animation-State Override

49. **The pose-state-machine must implement `ILocomotionAnimationSource`** to provide locomotion animation-state targets to external locomotion systems:
    - Property `LocomotionStateTarget: LocomotionStateTarget?` is exposed to external consumers.
    - Each tick, the machine delegates to the active pose state's `GetLocomotionStateTarget(PoseStateContext)` method.

50. **Each pose state may optionally return a locomotion animation-state target**:
    - Default `IPoseState.GetLocomotionStateTarget(PoseStateContext)` returns `null`.
    - Subclasses may override to return a target.

51. **Interface contract**:
    ```
    IPoseState:
      - Method: GetLocomotionStateTarget(context: PoseStateContext): LocomotionStateTarget?

    ILocomotionAnimationSource:
      - Property: LocomotionStateTarget: LocomotionStateTarget?

    LocomotionStateTarget:
      - Property: IdleStateName: StringName
      - Property: MovementStateName: StringName
    ```
    - `IPoseState` defines the per-state hook that pose states optionally implement.
    - `ILocomotionAnimationSource` is what external consumers (for example `LocomotionBase`) resolve from `PermissionSourceNodes`.

52. **Pose-state-machine implementation**:
    - The pose-state-machine implements `ILocomotionAnimationSource` and delegates to the active pose state each tick.
    - External consumers query `ILocomotionAnimationSource.LocomotionStateTarget`.

## In Scope

- State machine core architecture with extensible resource-based state and transition definitions.
- Pose state context snapshot structure and immutable per-tick evaluation contract.
- Animation control via `PoseState` resource lifecycle callbacks.
- `ILocomotionPermissionSource` implementation for locomotion system integration.
- Framework patterns for standing-family (TimeSeek), kneeling (authored transitions), and AllFours (dual-phase) animation control.
- Extension surface for subclassable states and transitions.

## Out Of Scope

- Detailed per-state behaviour requirements (see dedicated child pages).
- Final numeric thresholds and curve tuning constants.
- Per-state animation authoring details.
- Collision-based or locomotion-coupled state detection beyond permission output.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-04 | Input contracts for this phase are restricted to head and hand IK-target transforms and internal/animation-derived values, with collision/locomotion inputs deferred. | Technical |
| AC-06 | Feet positions from animation are explicitly defined as source of truth for this phase. | User + Technical |
| AC-11 | Each pose state binds both animation selection and hip reconciliation behaviour as a coupled responsibility. | Technical |
| AC-12 | State switching relies on inferred signals from IK-target transforms and runtime state; explicit button-based pose switching avoided by default. | User + Technical |
| AC-14 | The state machine evaluates per tick from an immutable `PoseStateContext` snapshot. | Technical |
| AC-16 | Animation control for pose states is owned by `PoseState` resources with lifecycle callbacks. | Technical |
| AC-27 | The state machine includes an `AllFoursPoseState` resource with `transitioning` and `crawling` sub-states. | Technical |
| AC-30 | The pose-state-machine implements `ILocomotionPermissionSource` and serves as a locomotion permission provider. | Technical |
| AC-31 | Each pose state provides `GetLocomotionPermissions(PoseStateContext)` that returns appropriate permissions for that pose. | Technical |
| AC-32 | Non-standing poses (kneeling, sitting, crawling) return `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation). | User + Technical |
| AC-32a | AllFours Crawling (crawl-hold) sub-state permits movement to enable crawl-hold locomotion. | User + Technical |
| AC-33 | The standing pose family allows movement only when the standing/crouching blend is below a configurable threshold. | User + Technical |
| AC-34 | The movement permission threshold (`MovementAllowedMaximumPoseBlend`) is configurable on `StandingPoseState`. | Technical |
| AC-35 | Pose states may optionally return a `LocomotionStateTarget?` from `GetLocomotionStateTarget(PoseStateContext)`. | Technical |
| AC-36 | The pose-state-machine implements `ILocomotionAnimationSource` with `LocomotionStateTarget` property. | Technical |
| AC-37 | AllFours Crawling sub-state returns `LocomotionStateTarget(AllFours, AllFoursForward)`. | Technical |
| AC-38 | The pose-state-machine delegates to the active pose state each tick to resolve the target. | Technical |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn