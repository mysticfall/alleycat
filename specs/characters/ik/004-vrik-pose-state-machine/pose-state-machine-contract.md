# Pose State Machine Contract

## Purpose

Define the implementation contract for IK-004 pose-state orchestration and transitions.

## Requirement

Provide a framework-first pose-state machine contract that supports MVP state coverage now and extensibility later.

## Goal

Ensure implementers can deliver stable pose classification and transition flow without hard-coding final tuning values.

## User Requirements

1. Pose changes must feel continuous across common player-driven movement.
2. MVP pose states must remain available and selectable across supported input conditions.

## Technical Requirements

1. The state machine must include explicit contracts for these states:
   - Standing
   - Crouching
   - Kneeling
   - Stooping
   - Sitting
   - Crawling (all fours)
2. Each pose state must be represented by a Godot `Resource` containing state metadata and customisable transition
   configuration hooks.
3. Transition definitions must be resource-compatible so they can be authored or replaced per state pair without
   rewriting the runtime state-machine core.
4. State-specific disambiguation between overlapping poses (for example stoop vs crouch) is a permanent classifier
   responsibility, resolved using auxiliary XR signals such as headset pitch and controller height. Only the initial
   tuning values are rudimentary; the disambiguation responsibility itself is not a temporary placeholder, and the
   architecture must preserve upgrade paths for more advanced classifiers and tuning.
5. Input signals for state classification in this phase are limited to:
   - XR camera transform data,
   - left/right controller transform data,
   - internal runtime values,
   - animation-derived values.
6. Collision probes, locomotion-system outputs, and external tracking systems are optional future extensions and must
   not be required dependencies in this contract.
7. For long, continuous, player-driven transitions, the default transition rule is linear clip progression with
   `TimeSeek`. Rationale: player motion is non-linear, so any non-linear transition clip would desynchronise from
   headset motion and risks motion sickness. `AnimationNodeBlend2D` is rejected for pose transitions because feet are
   parented under the hip — blend-based hip displacement causes foot float/slide that foot IK cannot rescue, since
   animated feet are the source of truth for this phase.
8. Non-linear transition paths are allowed where state-specific behaviour requires them, provided the state machine can
   select those exceptions without breaking the default rule.
9. No mandatory global ambiguity state is required in this phase; ambiguity handling may be local to transitions or
   state-selection logic.
10. Each state Resource must declare the animation clip or `AnimationTree` parameter configuration it activates.
    Animation selection and hip reconciliation configuration travel together on the same state Resource.
11. State classification must not rely on explicit controller-button input; it must be derived from XR device transforms
    (including auxiliary signals such as headset pitch and controller height) and animation/runtime state. Explicit
    button input is permitted only as a last-resort fallback for a specific transition with no viable automatic signal.
12. The state-machine core must expose a public extension surface — subclassable `PoseState` and `PoseTransition`
    resources plus pluggable classifier/evaluator interfaces — so consumers may add or replace pose behaviour without
    editing core state-machine source.
13. The state machine must evaluate each tick from a per-tick read-only context snapshot (suggested name
    `PoseStateContext`). The context is the canonical input surface for classifiers, transitions, animation bindings,
    and hip reconciliation profiles, and must include at minimum:
    - `CameraTransform` (current XR camera global transform),
    - left and right controller transforms,
    - `ViewpointGlobalRest` (viewpoint-node global rest transform),
    - world scale,
    - skeleton reference and the bone indices required for IK-004 (hip, head),
    - tick delta,
    - an auxiliary-signals lookup (for example keyed by `StringName`) for extensible computed values such as headset
      pitch.

    The context must be immutable for the duration of a tick so classifiers, transitions, and profiles cannot observe
    inconsistent state. Producing the context without per-frame heap pressure is a quality goal, not a hard threshold.
14. Runtime responsibilities may be split across two cooperating nodes:
    - a `PoseStateMachine` (`Node`) that runs `Tick(context)` per frame, evaluates transitions, applies the active
      state's animation binding, and computes a pending hip translation;
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
15. Transition Resources must support optional lifecycle hooks invoked in this order around a state switch:
    `OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`. The state machine must emit a
    state-changed observation (signal or event) so consumers can react.
16. Animation bindings may be invoked on every tick or only on state change. The default is per-tick invocation; a
    binding's `Apply` is responsible for being idempotent or change-aware. Bindings opting into change-aware updates
    must document and implement that behaviour themselves.
17. Classifier Resources form part of the public extension surface, but the state machine is not required to consume
    classifier scores in the initial implementation. Transitions may consume classifier outputs indirectly via the
    context, and future increments may add a classification-driven selection path. This is an explicit
    deferred-but-supported extension path; MVP delivery is not required to wire classifiers.
18. State and transition identifiers are authored as `StringName` in editor contexts for designer ergonomics. The
    internal selection layer may use either `StringName` or `string` for testability, provided identity semantics are
    preserved.
19. The Standing/Crouching pose family uses a single `AnimationTree` state, `StandingCrouching`, whose sub-graph
    continuously runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")`. This is the canonical authoring pattern for a
    "continuous player-driven pose continuum": a single `AnimationTree` state spanning the continuum with `TimeSeek`
    driven by a normalised scalar. Multiple framework-level `PoseState` resources MAY map to the same `AnimationTree`
    state when they share animation behaviour.
20. The `Idle` clip remains in the animation library for future layering (for example, an additive breathing blend on
    top of the `StandingCrouching` sub-graph) but is not wired into the tree for MVP. This is a deferred-but-supported
    extension point, not a dropped feature.

## In Scope

- State and transition data contracts.
- Runtime state-selection boundaries for MVP states.
- Extensibility hooks for future input modalities and classifier improvements.

## Out Of Scope

- Final numeric thresholds for state gating.
- Final transition timing curves and authored blend polish.
- Collision and locomotion integration contracts.
- Optional non-MVP pose states.

## Acceptance Criteria

| ID | Requirement | Layer |
| --- | --- | --- |
| AC-PS-01 | The contract defines all six MVP states as explicit state-machine entries. | Technical |
| AC-PS-02 | State and transition definitions are specified as resource-driven contracts, not fixed hard-coded enums alone. | Technical |
| AC-PS-03 | Input boundaries are explicitly restricted to headset, controllers, and internal or animation-derived values for this phase. | Technical |
| AC-PS-04 | The default transition method for long continuous transitions is linear clip + `TimeSeek`, with documented allowance for state-specific non-linear exceptions. | Technical |
| AC-PS-05 | State-specific disambiguation between similar poses (for example stoop vs crouch) is a permanent responsibility of the state classifier, resolved using auxiliary XR signals such as headset pitch and controller height, not a temporary placeholder. | User + Technical |
| AC-PS-06 | The contract does not require a mandatory catch-all ambiguity state. | Technical |
| AC-PS-07 | Each state Resource declares both its animation/AnimationTree binding and its hip reconciliation profile. | Technical |
| AC-PS-08 | Pose switching is inferred from device/runtime signals; explicit button input is not the default mechanism. | User + Technical |
| AC-PS-09 | The contract defines a public extension surface allowing new states, transitions, and classifiers to be added without modifying core state-machine source. | Technical |
| AC-PS-10 | The state machine evaluates each tick from an immutable per-tick context snapshot that bundles XR inputs, including `CameraTransform` (current XR camera global transform) and `ViewpointGlobalRest` (viewpoint-node global rest transform), controller transforms, world scale, skeleton reference and bone indices, tick delta, and an auxiliary-signals lookup. | Technical |
| AC-PS-11 | Runtime responsibilities may be split into a `PoseStateMachine` node running `Tick` per frame and a `HipReconciliationModifier` (`SkeletonModifier3D`) applying the pending hip translation, with `Tick` running after follower updates and before downstream consumers of pending hip translation, including begin-stage modifier-callback topology (for example `PlayerVRIK` begin-stage flow via `StageModifier`) or equivalent topology that preserves this ordering. | Technical |
| AC-PS-12 | Transition Resources support optional lifecycle hooks (`OnTransitionEnter` → state `OnExit` → state `OnEnter` → `OnTransitionExit`) and the state machine emits a state-changed observation. | Technical |
| AC-PS-13 | Animation binding `Apply` is invoked per tick by default and is responsible for being idempotent or change-aware; change-aware bindings must document and implement that behaviour themselves. | Technical |
| AC-PS-14 | Classifier Resources are part of the public extension surface as a deferred-but-supported path; MVP delivery is not required to consume classifier scores directly in the state machine. | Technical |
| AC-PS-15 | State and transition identifiers are authored as `StringName`; the internal selection layer may use either `StringName` or `string` provided identity semantics are preserved. | Technical |
| AC-PS-16 | The Standing/Crouching pose family is backed by a single `AnimationTree` state (`StandingCrouching`) whose sub-graph runs `TimeSeek → AnimationNodeAnimation("Crouch-seek")` continuously, driven by a normalised scalar. Multiple framework-level `PoseState` resources MAY map to the same `AnimationTree` state when they share animation behaviour. | Technical |
| AC-PS-17 | The `Idle` clip remains present in the animation library as a deferred-but-supported extension point (for example, additive breathing layering over the `StandingCrouching` sub-graph), but is not wired into the `AnimationTree` for MVP. | Technical |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](index.md)
- [Hip Reconciliation Contract](hip-reconciliation-contract.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](../001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](../002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../003-leg-feet-ik/index.md)
