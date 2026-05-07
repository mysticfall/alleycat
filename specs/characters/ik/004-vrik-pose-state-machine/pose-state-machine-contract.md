# Pose State Machine Contract

## Requirement

Define the implementation contract for the pose-state orchestration framework,
including state machine architecture, animation binding, transition mechanics,
and locomotion permission outputs.

## Goal

Enable implementers to deliver stable pose classification and transition flow
without hard-coding final tuning values, while providing safe movement
gating for non-standing poses.

## User Requirements

1. Pose changes must feel continuous across common player-driven
   movement.
2. MVP pose states must remain available and selectable across
   supported input conditions.
3. Players must be able to transition from crouching to kneeling
   using an armed-then-retreat trigger model: the transition
   initiates from the full-crouch baseline, becomes armed after
   sufficient forward travel from that baseline, and fires only
   after retreating from the armed peak by a configurable amount.
   This provides intentional player control and prevents accidental
   firing.
4. The Standing→Kneeling transition must require a crouch-depth
   gate—near-full crouch on the standing-to-crouching
   continuum—before kneeling becomes reachable.
5. Players must be able to transition from kneeling back to
   standing using the same armed-then-retreat model: the return
   path is measured from the full-crouch baseline, arms after
   sufficient forward travel from that baseline, and fires only
   after retreating from the armed peak by a configurable amount.
6. Following any kneeling transition, both transition directions
   must remain locked until the forward-axis offset returns to
   near the neutral or full-crouch baseline. This prevents
   immediate bounce-back and re-triggering.
7. Kneeling transition thresholds must use normalised ratios
   derived from rest-pose body measures, not absolute metres. At minimum,
   the head-height measure from rest pose (`RestHeadHeight`)
   defines the reference for the normalised crouch-depth gate.
8. Players must be able to transition from Standing to
   all-fours crawling by moving the head forward beyond
   a configurable threshold, where the transition becomes armed
   once the head's forward offset reaches a configurable threshold
   and fires when the player continues forward past that armed
   point by a configurable margin.
9. Players must be able to transition from kneeling to all-fours
   crawling by moving the head forward beyond a configurable
   threshold, with the same armed-then-fire model.
10. When in AllFours, players must experience a smooth
    transition animation from the entering position to the
    crawling hold pose, driven by the head's forward offset position.
11. While crawling on all fours, if the player raises their
    head vertically above a configurable threshold, they must
    transition back to the transitioning state for return to
    standing.
12. While in the AllFours transitioning state, if the head
    forward offset drops below a configurable return threshold,
    the pose must automatically transition back to Standing pose.
13. Movement must be restricted when the player is in a pose
    that does not support walking.
14. Rotation must remain available across all poses for MVP.

## Technical Requirements

### State Machine Core

1. The framework includes contracts for these pose families:
   - Standing (covering standing-to-crouching continuum) —
     implemented in child pages
   - Kneeling — implemented in child pages
   - Stooping (framework coverage for future extension)
   - Sitting (framework coverage for future extension)
   - Crawling (all fours) — implemented in child pages
2. Each pose state must be represented by a Godot `Resource`
   containing state metadata and customisable transition
   configuration hooks.
3. Transition definitions must be resource-compatible for
   per-state-pair authoring without rewriting the runtime core.
4. State-specific disambiguation between overlapping poses
   is a permanent classifier responsibility.
5. Input signals for state classification are limited to:
   head IK-target transform data, hand IK-target transform
   data, internal runtime values, and animation-derived values.
6. Collision probes, locomotion-system outputs, and external
   tracking systems are optional future extensions.
7. Each state Resource must declare the animation control it
   activates via lifecycle callbacks driven by the state-machine runtime.
8. State classification must not rely on explicit button input;
   it must be derived from IK-target transforms and
   animation/runtime state.
9. The state-machine core must expose a public extension
   surface—subclassable `PoseState` and `PoseTransition`
   resources plus pluggable classifier/evaluator interfaces.

### Pose State Context

10. The state machine must evaluate each tick from
    a per-tick read-only context snapshot.
11. The context is the canonical input surface for
    classifiers, transitions, states, and hip reconciliation
    profiles, and must include at minimum:
    - Head and hand IK-target global transforms
    - Head IK-target rest transform
    - AnimationTree reference
    - Rest-pose body measures for ratio-based
      threshold computation
    - World scale
    - Skeleton reference and required bone indices
    - Tick delta
    - Auxiliary-signals lookup for extensible
      computed values
12. The context must be immutable for the tick duration.

### Runtime Topology

13. Runtime responsibilities split across two cooperating nodes:
    - `PoseStateMachine` (`Node`) that runs `Tick(context)` per
      frame, evaluates transitions, applies animation control,
      and computes pending hip translation
    - `HipReconciliationModifier` (`SkeletonModifier3D`) that applies
      pending hip translation inside the skeleton modifier pipeline
14. `Tick` must run once per frame after follower updates
    and before downstream consumers read the pending hip translation.
15. Hip reconciliation executes inside a `SkeletonModifier3D`
    pass after the animation player.

### Animation Control Framework

16. Animation control for pose states is owned by
    `PoseState` resources, not by a separate animation-binding
    abstraction.
17. Each `PoseState` resource provides:
    - `Start(AnimationTree)` — called once after initial-state resolution
    - Lifecycle callbacks (`OnEnter`, `OnUpdate`, `OnExit`) —
      called per-frame to drive animation work
    - Properties declaring the hip reconciliation profile
18. Animation control for transitions is owned by `PoseTransition`
    resources, which may own AnimationTree travel into authored
    transition states.
19. `PoseStateMachine` permits a one-time startup exception:
    after resolving `InitialStateId`, it may call
    `PoseState.Start(AnimationTree)` once.
20. Transition Resources must support lifecycle hooks invoked
    in order around a state switch, and the machine must emit
    a state-changed observation.
21. State and transition identifiers are authored as `StringName`
    for designer ergonomics.

### Standing Pose Family

22. The Standing pose family uses a single `AnimationTree`
    state, `StandingCrouching`, whose sub-graph continuously
    runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")`. This
    is the canonical authoring pattern for a continuous
    player-driven pose continuum.
23. The `Idle` clip remains in the animation library for
    future additive layering.

### Kneeling Transitions

24. The Standing→Kneeling transition uses an armed-then-retreat
    trigger model measured from the full-crouch baseline,
    preventing accidental firing during normal crouching.
25. The Standing→Kneeling transition is additionally gated by
    a crouch-depth threshold requiring near-full crouch depth
    before the transition can trigger.
26. The kneeling pose is authored as separate AnimationTree
    state-machine nodes using normal animation playback with
    authored auto-advance.
27. Following any kneeling transition, both directions
    remain locked until the forward-axis offset returns
    near the neutral or full-crouch baseline.
28. Kneeling transition thresholds use normalised ratios
    derived from rest-pose body measures.
29. The Kneeling→Standing return uses the same armed-then-retreat
    model as the forward transition.

### AllFours Pose

30. The state machine must include a new `AllFoursPoseState`
    resource with two internal phases: `transitioning` and
    `crawling`.
31. The AllFours pose must be reachable as a transition
    target from both StandingPoseState and KneelingPoseState,
    using a forward-travel-beyond-armed-point model.
32. The AllFours entry trigger uses head forward offset as
    the primary signal, with configurable threshold and
    continue margin.
33. `AllFoursPoseState` implements two internal sub-states:
    `transitioning` (drives entry animation via AnimationNodeTimeSeek)
    and `crawling` (holds the crawl pose).
34. In the `transitioning` sub-state, the state drives an
    AnimationNodeTimeSeek node using animation `All Fours-enter`
    with a custom seek window.
35. The seek position is calculated from the head's normalised
    forward offset using the formula:
    `seekPosition = (headForwardOffset - 0.42) / (0.73 - 0.42)`,
    mapped to the seek window duration.
36. When head forward offset exceeds 0.73, the internal state
    transitions to `crawling` and plays the looping animation.
37. While in `crawling`, if the head rises vertically above
    a configurable threshold, the state returns to `transitioning`.
38. While in `transitioning`, if head forward offset
    decreases below the return threshold, the framework
    transitions back to StandingPoseState.
39. AllFours threshold parameters must be configurable properties.
40. The AllFours animation entry uses `TimeSeek` scrubbing;
    crawl hold uses standard animation playback.
41. **AllFours locomotion permissions**: `transitioning` returns
    `RotationOnly`; `crawling` returns `Allowed` for root-motion-driven
    locomotion.
42. **AllFours locomotion animation-state target**: `crawling` returns
    `LocomotionStateTarget(AllFours, AllFoursForward)`; `transitioning`
    returns `null`.

### Locomotion Permission Sources

43. The pose-state-machine must implement `ILocomotionPermissionSource`
    and serve as a locomotion permission provider, delegating to
    the active pose state.
44. Each `PoseState` must provide `GetLocomotionPermissions(PoseStateContext)`
    with default returning `RotationOnly`.
45. Standing pose family permissions: allows movement only
    when standing/crouching blend is at or below configurable
    threshold; allows rotation always.
46. Non-standing pose permissions: kneeling, sitting, stooping,
    AllFours transitioning return `RotationOnly`; AllFours crawling
    returns `Allowed`.
47. External systems aggregate permissions using logical AND.
48. Runtime query exposes `ILocomotionPermissionSource.LocomotionPermissions`
    to consumers.

### Locomotion Animation-State Override

49. The pose-state-machine must implement `ILocomotionAnimationSource`
    to provide locomotion animation-state targets.
50. Each pose state may optionally return a locomotion
    animation-state target via `GetLocomotionStateTarget`.
51. Interface contract defines `IPoseState.GetLocomotionStateTarget`
    and `ILocomotionAnimationSource.LocomotionStateTarget`.
52. Implementation delegates to the active pose state each tick.

## In Scope

- State machine core architecture with extensible
  resource-based state and transition definitions.
- Pose state context snapshot structure and immutable
  per-tick evaluation contract.
- Animation control via `PoseState` resource lifecycle
  callbacks.
- `ILocomotionPermissionSource` implementation for
  locomotion system integration.
- Framework patterns for standing-family (TimeSeek), kneeling
  (authored transitions), and AllFours (dual-phase) animation
  control.
- Extension surface for subclassable states and transitions.

## Out Of Scope

- Detailed per-state behaviour requirements (see dedicated
  child pages).
- Final numeric thresholds and curve tuning constants.
- Per-state animation authoring details.
- Collision-based or locomotion-coupled state detection.
- Networked replication behaviour.

## Acceptance Criteria

| ID | Requirement | Layer |
|----|-------------|-------|
| AC-04 | Input contracts limited to IK-target transforms, internal values, and animation-derived values. | Technical |
| AC-06 | Feet positions from animation are source of truth for this phase. | User + Technical |
| AC-11 | Each pose state binds animation selection and hip reconciliation as a coupled responsibility. | Technical |
| AC-12 | State switching relies on inferred signals; explicit button input avoided by default. | User + Technical |
| AC-14 | The state machine evaluates per tick from an immutable context snapshot. | Technical |
| AC-16 | Animation control owned by PoseState resources with lifecycle callbacks. | Technical |
| AC-27 | AllFoursPoseState includes transitioning and crawling sub-states. | Technical |
| AC-30 | Pose-state-machine implements ILocomotionPermissionSource. | Technical |
| AC-31 | Each pose state provides GetLocomotionPermissions. | Technical |
| AC-32 | Non-standing poses return RotationOnly. | User + Technical |
| AC-32a | AllFours Crawling sub-state permits movement. | User + Technical |
| AC-33 | Standing pose family allows movement below configurable threshold. | User + Technical |
| AC-34 | Movement permission threshold is configurable. | Technical |
| AC-35 | Pose states may return LocomotionStateTarget. | Technical |
| AC-36 | Pose-state-machine implements ILocomotionAnimationSource. | Technical |
| AC-37 | AllFours Crawling returns LocomotionStateTarget. | Technical |
| AC-38 | Pose-state-machine delegates to active pose state each tick. | Technical |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn