---
id: ANIM-001
title: Partial Animation Blending And Hand Poses
---

# Partial Animation Blending And Hand Poses

## Requirement

Define an `Animation` namespace subsystem that appends hand-pose blends after the existing AnimationTree
state machine, enabling partial animation blending for isolated body regions without modifying upstream
animation states.

## Goal

Provide a reusable blend stage that blends hand poses from the state-machine output without requiring
hand data to exist in upstream animations. This enables characters to retain full-body posture while applying
per-hand pose overrides, with smooth transitions and per-hand control.

## User Requirements

1. Hand poses must not override upstream hand animations unless explicitly specified.
2. When a hand pose is set, the hand must blend between the pose and the rest pose using a specified
   weight, defaulting to full application.
3. Hand pose changes must transition smoothly rather than jumping instantly.
4. Each hand must be controllable independently.
5. Hand poses must only affect finger bones, leaving the hand bone and upstream arm/body animations
   unaltered.
6. The pipeline must support additional partial animation types beyond hands in the future.

## Technical Requirements

1. The existing root AnimationTree resource
   (`@game/assets/characters/reference/female/pose_state_machine_tree.tres`) must become a root-level
   `AnimationNodeBlendTree` that contains exactly one `States` node.
2. The `States` node must be connected to the active output path. The graph must not contain
   orphan or duplicate state-machine nodes used only to satisfy grouping requirements.
3. A dedicated `Animation` namespace class must encapsulate all hand-pose logic, state, and blend management.
4. The root blend tree must contain two filtered `AnimationNodeBlend2` nodes (one per hand), sequenced after
   the upstream state machine. The left hand blend receives the upstream state-machine output on port 0; the
   right hand blend receives the previous post-pipeline stage on port 0 so both hands can contribute to the
   final output. Each blend receives the configured hand-pose animation on port 1.
   - Filter: a track filter matching only the finger bones of the target hand (for example
     `*LeftIndexProximal`, `*RightThumbMetacarpal`). The hand bone itself must be excluded.
5. Each hand blend node must use its `blend_amount` parameter as the effective hand-pose application:
   requested weight multiplied by the current smooth transition progress. A weight of 1.0 applies the
   pose fully after transition; 0.0 passes through upstream output.
6. When no hand pose is set, the blend node must not alter upstream output.
7. Hand pose changes must use a configurable transition time (default 0.2 s) with smooth interpolation
   of `blend_amount`, not instant switching.
8. The `Animation` class must expose:
   - `LeftHandPose: Resource?` (null means no override).
   - `RightHandPose: Resource?`
   - `LeftHandPoseWeight: float` (default 1.0, clamped [0, 1]).
   - `RightHandPoseWeight: float`
   - `TransitionDuration: float` (seconds, default 0.2).
   - `CurrentLeftHandPose: Resource?` (read-only current, used for transition tracking).
   - `CurrentRightHandPose: Resource?`
   - A method to set hand pose with optional immediate flag bypassing the transition.
   - A method to clear hand pose per hand.
9. The rest pose reference is `@game/assets/characters/reference/female/animations/Reset.tres`. The
   reset animation is used as the inert fallback clip when no pose is configured; the upstream pipeline
   provides the effective rest state for all bones not overridden by filtered hand-pose blends.
10. The implementation must be compatible with the existing pose state machine and the VRIK/IK
   pipeline, without introducing state coupling or circular dependencies.
11. A dedicated component or behaviour node must expose the hand-pose API to external consumers (for
    example `HandPoseBehaviour`).

## In Scope

- Refactoring `pose_state_machine_tree.tres` into a functional root `AnimationNodeBlendTree` with one
  active upstream state machine and no orphan duplicate state-machine nodes.
- `Animation` namespace class with hand-pose state management and blend control.
- State-machine-to-output blend chain with per-hand filtered `AnimationNodeBlend2` nodes.
- Smooth transition for hand pose changes using configurable duration.
- Independent per-hand control (left/right).
- Hand bone excluded from filter; finger bones only.
- `HandPoseBehaviour` component exposing the API.
- Fixture animation `Grab-ball-40.tres` used as a test case for hand pose application.

## Out Of Scope

- Animation content creation or pose animation assets (hand poses provided separately).
- Eye blinking, breathing, or facial animation pipelines.
- Networked replication or multiplayer considerations.
- IK solver modifications; IK-004 VRIK pipeline remains unchanged.
- Explicit blend shape or morph target support.
- Automatic pose detection or procedural pose generation.
- Final numeric thresholds for transition curves beyond the defined duration constant.

## Acceptance Criteria

- AC-01 (Technical): The new root `AnimationNodeBlendTree` contains exactly one `States` node.
- AC-02 (Technical): The sole upstream state machine is connected to the active output path.
- AC-03 (Technical): A dedicated `Animation` namespace class manages hand-pose logic and state.
- AC-04 (Technical): Per-hand `AnimationNodeBlend2` nodes form a sequential chain. The left hand blend
  receives the upstream state-machine output on port 0; the right hand blend receives the prior
  post-pipeline stage output on port 0. Both receive their respective hand-pose animation on port 1.
- AC-05 (Technical): Track filters on blend nodes match only finger bones for the target hand, excluding the hand
  bone.
- AC-06 (User): Clearing a hand pose leaves upstream hand animations unaltered.
- AC-07 (User): Setting a hand pose with weight 1.0 fully applies the pose to finger bones.
- AC-08 (User): Hand pose changes transition smoothly over the configured duration, not instantly.
- AC-09 (User): Left and right hands are independently controllable.
- AC-10 (Technical): When no hand pose is set, the blend node passes through upstream output unchanged.
- AC-11 (Technical): A `HandPoseBehaviour` component exposes the hand-pose API to external consumers.
- AC-12 (Technical): The implementation is compatible with the existing VRIK/IK pipeline without circular
  dependencies.
- AC-13 (Technical): The `Animation` class exposes all specified hand-pose properties and methods.
- AC-14 (Technical): The rest pose animation `Reset.tres` is referenced as the inert fallback clip.
- AC-15 (Technical): The system architecture allows future addition of eye blinking, breathing, or other partial
  blends.

## References

- [Player VRIK System](../characters/ik/index.md)
- [IK-004: VRIK Pose State Machine](../characters/ik/004-vrik-pose-state-machine/index.md)
- [Character Skeleton Profile](../characters/000-character-skeleton/index.md)
- Root animation tree: `@game/assets/characters/reference/female/pose_state_machine_tree.tres`
- Rest pose animation: `@game/assets/characters/reference/female/animations/Reset.tres`
- Hand pose fixture: `@game/assets/characters/reference/female/animations/Grab-ball-40.tres`
- [Feature Specification Template](../templates/feature-spec-template.md)
