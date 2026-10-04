---
title: Leg-Feet IK Contract
---

# Leg-Feet IK Contract

## Requirement

Define rig-neutral foot-target resolution, target actuation, knee-pole prediction, and two-bone lower-limb solving
beneath IK-003.

## Goal

Resolve each leg's target deterministically from the current tick's pose and solve it without letting providers or
solvers rewrite unrelated state or policy.

## User Requirements

1. Legs bend predictably and smoothly towards resolved foot targets on both reference rigs.
2. An applicable foot provider takes control without visible conflict; a provider that cannot apply lets ordinary
   animation-following behaviour continue.
3. Feet do not snap because a solver mutates an input target or a knee pole becomes degenerate.
4. Each foot is isolated from the other foot's invalid or inapplicable provider state.

## Technical Requirements

1. Each side has an upper-leg → lower-leg → foot `TwoBoneIK3D` solve with a preceding rig-neutral pole-target stage.
2. Target synchronisation is the first modifier stage. It sets both foot targets to the current animated foot poses
   and does not mutate authored animation.
3. `CharacterIK` resolves the explicit provider before the fallback provider. A returned zero-influence intent is
   applicable and disables that side's leg solve. A provider may instead refuse, letting the next provider run.
4. An ordered composite queries providers in authored order and selects the first applicable, finite, value-only
   result. It uses no numeric priorities, and a result for one side is never applied to the other.
5. Providers return values only and never mutate targets, activity gates, modifiers, root, hips, skeleton, or actor.
   Only `CharacterIK` applies the selected target, gate, and influence for each side.
6. The modifier order is `sync → provider → hip reconciliation (when present) → lower-leg consumers`. `CharacterIK`
   repairs this order idempotently on initialisation or retry.
7. Providers resolve from the same-tick animated pose: after the AnimationTree has applied the current tick and after
   that tick's actor movement operation. A target resolved from an earlier tick's pose is never applied. When a tick
   evaluates the AnimationTree more than once, only the final evaluation is used, and an intermediate evaluation
   triggers no provider resolution or solve; see
   [NAV-001 Animation Tree Use](../../nav/001-npc-navigation/index.md#animation-tree-use).
8. `CharacterIKSubsystemInstaller` sets `Skeleton3D.ModifierCallbackModeProcess` to `Physics` before activating
   modifier groups, and the AnimationTree applies before the modifier chain within each physics tick.
9. Post-modification poses are observable only inside `Skeleton3D.SkeletonUpdated`. The engine applies modifier
   output to the skin and then restores canonical bone poses, so reads at physics-timer time cannot observe IK output.
   Diagnostics and test instruments capture post-IK output there, bound to the tick that produced it.
10. Pole prediction derives its direction from current animation geometry and is resolved on every tick; a pole from
    an earlier tick is never kept. When the knee plane is degenerate or ambiguous, a deterministic, smooth fallback
    uses a forward reference projected perpendicular to the leg direction. The reference is the foot target's forward
    direction, except that the avatar's forward direction is used when the leg reaches forward at full extension.
    - **Degenerate plane** (the knee on the hip–foot line): the pole follows the projected reference. If the foot
      target is tilted so that its forward direction is not usable, the pole follows the projected avatar forward.
    - **Ambiguous plane:** the projected reference resolves the plane. If the foot target is tilted so that its
      forward direction is not usable, the pole keeps the direction derived from the current animation geometry.

    A tunable rest-leg-derived minimum pole offset applies.
11. The solver never writes a target's position, rotation, or scale.
12. Foot planting geometry uses the ball joint defined by [IK-003 Foot Planting](index.md#foot-planting): the
    rest-pose origin of the toe bone in foot-local space. The foot bone origin is never treated as the pinned point.
    Both toe bones are required rig data: if either is missing, foot-planting binding fails with a clear error, with
    no degraded per-foot mode.
    - A target whose rotation differs from the animated foot pose, such as the IK-003 planted facing hold, still
      places the ball joint as [IK-003](index.md#foot-planting) defines: the rotation acts about the ball joint, not
      the foot-bone origin. Ball-point placement and reach checks derived from a target use the composed position and
      rotation in one consistent world frame, with no second application of any scale conversion.
13. The path contains no terrain probe, pelvis or whole-body adjustment, or downstream target overwrite.

## In Scope

- Target synchronisation, provider composition and actuation, same-tick ordering, and post-IK observability.
- Pole prediction, two-bone solving, and ball-joint resolution on both reference rigs.

## Out Of Scope

- Foot-planting policy, which [IK-003](index.md#foot-planting) owns.
- Numeric pole thresholds and scene-node names, which remain rig configuration.
- Terrain adaptation and pelvis authority.

## Acceptance Criteria

### User Requirement Acceptance

1. Both reference rigs show stable knee direction and target following, including a degenerate-pole case.
2. Switching between synchronised, applicable-provider, and fallback targets is deterministic and conflict-free.
3. A refusal or invalid result for one foot does not disturb the other foot.

### Technical Requirement Acceptance

1. Fixtures prove the two-bone solve and pole stage per side on both rigs.
2. Fixtures prove synchronisation, explicit-provider precedence, zero-influence behaviour, refusal fallback, and
   first-applicable composite selection with side isolation.
3. Fixtures prove providers are value-only and `CharacterIK` is the sole actuator, with idempotent order repair.
4. A real-NPC trace proves `Physics` callback mode, animation before modifiers, same-tick provider input, and post-IK
   capture inside `Skeleton3D.SkeletonUpdated`.
5. Fixtures prove pole fallback, the minimum pole offset, and that the solver never writes its target.
6. Per-foot checks prove foot planting geometry uses the toe-bone ball joint, and a rig missing either toe bone fails
   foot-planting binding with a clear error. They also prove a target rotated away from the animated pose keeps the
   ball joint at its intended world position, with ball-point and reach computation using the composed target
   transform in one consistent world frame.
7. An audit confirms no terrain probe, pelvis adjustment, or downstream target overwrite exists.

### Acceptance Traceability

- User Requirements 1, 3 → User Requirement Acceptance 1, Technical Requirement Acceptance 1, 5.
- User Requirements 2, 4 → User Requirement Acceptance 2–3, Technical Requirement Acceptance 2–3.
- Technical Requirements 1–6 → Technical Requirement Acceptance 1–3.
- Technical Requirements 7–9 → Technical Requirement Acceptance 4.
- Technical Requirements 10–11 → Technical Requirement Acceptance 5.
- Technical Requirement 12 → Technical Requirement Acceptance 6.
- Technical Requirement 13 → Technical Requirement Acceptance 7.

## References

- [IK-003 Overview](index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
