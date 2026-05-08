---
id: BODY-001
title: Hands
---

# Hands

## Purpose

Define contracts for hand components that manage hand-pose animation blending and expose
hand-side control to external consumers via the CORE-003 Component/Trait System.

## Requirement

Implement hand components that provide per-hand pose control, enabling characters to retain
full-body posture while applying filtered finger-bone pose overrides with smooth transitions.

## Goal

Provide a reusable hand component system that:

- Supports independent left/right hand control via `LimbSide`.
- Blends hand poses through filtered AnimationNodeBlend2 nodes without affecting the hand bone or arm bones.
- Transitions poses smoothly with configurable duration.
- Exposes a CORE-003 compatible component interface and holder trait.
- Integrates with the existing VRIK/IK pipeline without circular dependencies.

## User Requirements

1. Hand poses must not override upstream hand animations unless explicitly specified.
2. When a hand pose is set, the hand must blend between the pose and upstream output using a
   specified weight, defaulting to full application.
3. Hand pose changes must transition smoothly rather than jumping instantly.
4. Each hand must be controllable independently.
5. Hand poses must only affect finger bones, leaving the hand bone and upstream arm/body animations
   unaltered.

## Technical Requirements

1. Define `LimbSide` enum in `AlleyCat.Body` (`Left = 0`, `Right = 1`).
2. Define `IHand : IComponent` capability interface in `AlleyCat.Body.Hands`:
   - `Side: LimbSide` — identifies the hand side.
   - `HandPose: Resource?` — target pose resource; `null` clears override.
   - `HandPoseWeight: float` — clamped [0, 1] rest-to-pose blend weight (default 1.0).
   - `CurrentHandPose: Resource?` — read-only current pose after transition settles.
   - `SetHandPose(Resource? pose, float? weight = null, bool immediate = false)` — sets pose
     with optional weight and immediate flag bypassing smoothing.
   - `ClearHandPose(bool immediate = false)` — clears current hand pose override.
3. Define `IHasHands : IComponentHolder` holder trait in `AlleyCat.Body.Hands`:
   - `TryGetHand(LimbSide side, out IHand? hand)` — resolves exactly one hand component for
     the requested side; returns `false` if zero or more than one hand component exists.
   - `RequireHand(LimbSide side)` — returns the hand component or throws if not found or
     multiple exist.
4. Implement `HandPoseBehaviour : Node, IHand` in `AlleyCat.Body.Hands`:
   - Accepts an `AnimationTree` reference or inherits from parent.
   - Exposes per-side pose properties that delegate to an internal `HandPoseController`.
   - Manages a `HandPoseController` instance that:
     - References per-hand filtered `AnimationNodeBlend2` nodes in the AnimationTree.
     - Uses track filters matching only finger bones for the target hand (e.g.,
       `*LeftIndexProximal`, `*RightThumbMetacarpal`), excluding the hand bone.
     - Exposes `LeftHandPose`, `RightHandPose`, `LeftHandPoseWeight`, `RightHandPoseWeight`,
       `CurrentLeftHandPose`, `CurrentRightHandPose`, and `TransitionDuration` properties.
     - Implements `SetHandPose(LimbSide side, Resource? pose, float? weight, bool immediate)`
       and `ClearHandPose(LimbSide side, bool immediate)`.
5. The hand blend nodes must use their `blend_amount` parameter as effective hand-pose
   application: requested weight multiplied by current smooth transition progress.
6. When no hand pose is set, the blend node must pass through upstream output unchanged.
7. Default transition duration is 0.2 seconds; the implementation interpolates `blend_amount`
   smoothly, not instantly switching.
8. The root AnimationTree must be a functional `AnimationNodeBlendTree` containing a filtered
   per-hand blend chain sequenced after the upstream state-machine output.
9. The implementation must be compatible with the existing VRIK/IK pipeline (IK-004) without
   introducing state coupling or circular dependencies.

## In Scope

- `LimbSide` enum in `AlleyCat.Body`.
- `IHand` component capability interface.
- `IHasHands` holder trait with `TryGetHand` and `RequireHand` methods.
- `HandPoseBehaviour` Godot node facade exposing the hand-pose API.
- Per-hand filtered `AnimationNodeBlend2` nodes in the animation tree.
- Smooth transition for hand pose changes using configurable duration.
- Independent per-hand control (left/right).
- Track filters matching only finger bones, excluding the hand bone.
- Compatibility with VRIK/IK pipeline.

## Out Of Scope

- Animation content creation or pose animation assets.
- Eye blinking, breathing, or facial animation pipelines.
- Networked replication or multiplayer considerations.
- IK solver modifications.
- Explicit blend shape or morph target support.
- Automatic pose detection or procedural pose generation.
- Grab mechanics, release mechanics, or multi-hand grab coordination.
- Inventory integration.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `LimbSide` enum is defined in `AlleyCat.Body` with `Left = 0`, `Right = 1`. |
| 2  | Technical         | `IHand : IComponent` interface defines `Side`, `HandPose`, `HandPoseWeight`, |
|    |                   | `CurrentHandPose`, `SetHandPose`, and `ClearHandPose`. |
| 3  | Technical         | `IHasHands : IComponentHolder` defines `TryGetHand` and `RequireHand` methods. |
| 4  | Technical         | `HandPoseBehaviour` implements `IHand` and exposes per-side pose properties. |
| 5  | Technical         | Track filters on blend nodes match only finger bones, excluding the hand bone. |
| 6  | Technical         | When no hand pose is set, blend nodes pass through upstream output unchanged. |
| 7  | User              | Clearing a hand pose leaves upstream hand animations unaltered. |
| 8  | User              | Setting a hand pose with weight 1.0 fully applies the pose to finger bones. |
| 9  | User              | Hand pose changes transition smoothly over the configured duration, not instantly. |
| 10 | User              | Left and right hands are independently controllable. |
| 11 | Technical         | Implementation is compatible with VRIK/IK pipeline without circular dependencies. |

## References

- [Project Specifications Index](../../index.md)
- [CORE-003: Component/Trait System](../../003-component-system/index.md)
- [IK-002: Arm And Shoulder IK System](../../characters/ik/002-arm-shoulder-ik/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](../../characters/ik/004-vrik-pose-state-machine/index.md)
- [INTR-001: Grabbable Interface](../../interaction/001-grabbable/index.md)
- [Character Skeleton Profile](../../characters/000-character-skeleton/index.md)
- `game/src/Body/LimbSide.cs`
- `game/src/Body/Hands/IHand.cs`
- `game/src/Body/Hands/IHasHands.cs`
- `game/src/Body/Hands/HandPoseBehaviour.cs`
- `game/src/Body/Hands/HandPoseController.cs`