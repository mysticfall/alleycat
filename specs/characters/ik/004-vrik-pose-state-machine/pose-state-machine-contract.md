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

1. MVP pose states must remain available and selectable across supported input conditions.
2. Pose changes must feel continuous across common player-driven movement.
3. State switching relies on inferred signals from IK-target transforms and runtime state; explicit button-based pose switching avoided by default.
4. **Movement must be restricted when the player is in a pose that does not support walking** (for example, kneeling, sitting, crawling).
5. **Rotation must remain available across all poses for MVP.**

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
18. Animation control for transitions is owned by `PoseTransition` resources.
19. `PoseStateMachine` permits a one-time startup exception: after resolving `InitialStateId`, it may call `PoseState.Start(AnimationTree)` once to seed the initial authored state.
20. Transition Resources must support optional lifecycle hooks invoked in this order around a state switch:
    `OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`.
21. The state machine must emit a state-changed observation (signal or event) so consumers can react.
22. State and transition identifiers are authored as `StringName` in editor contexts.

### Locomotion Permission Sources

23. **The pose-state-machine must implement `ILocomotionPermissionSource`** and serve as a locomotion permission provider.
24. The machine acts as a single permission source that delegates to the currently active pose state.
25. Each tick, the machine queries the active pose state for its locomotion permissions.
26. **Each `PoseState` must provide locomotion permissions** via `GetLocomotionPermissions(PoseStateContext context)`:
    - Default implementation returns `LocomotionPermissions.RotationOnly` (blocks movement, allows rotation)
    - Subclasses may override to provide different permission behaviour
27. **Permission aggregation**:
    - External systems (for example locomotion component) aggregate permissions across all sources using logical AND
    - All sources must permit a permission for it to be granted
28. **Runtime permission query**:
    - The pose-state-machine exposes its `ILocomotionPermissionSource` contract to consumers
    - Consumers read `ILocomotionPermissionSource.LocomotionPermissions` each tick to get the aggregate permissions

### Standing Family Pattern

29. The Standing pose family uses a single `AnimationTree` state, `StandingCrouching`, whose sub-graph continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")`.
30. A single framework-level `StandingPoseState` resource maps to this `AnimationTree` state — there is no separate framework-level `CrouchingPoseState`.

### Kneeling Pattern

31. The kneeling pose is authored as separate AnimationTree state-machine nodes using normal animation playback.
32. The AnimationTree uses authored auto-advance for transition clips rather than per-tick TimeSeek scrubbing.

### AllFours Pattern

33. The state machine must include an `AllFoursPoseState` resource that implements a structured internal state machine with two phases: `transitioning` and `crawling`.
34. The AllFours pose must be reachable as a transition target from both `StandingPoseState` and `KneelingPoseState`.

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
| AC-34 | The movement permission threshold (`MovementAllowedMaximumPoseBlend`) is configurable on `StandingPoseState`. | Technical |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [Standing Pose State](standing-pose-state.md)
- [Kneeling Pose State](kneeling-pose-state.md)
- [AllFours Pose State](all-fours-pose-state.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- @game/assets/characters/reference/female/animations/animation_tree_player.tscn