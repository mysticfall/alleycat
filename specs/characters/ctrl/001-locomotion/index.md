---
id: CTRL-001
title: Locomotion
---

# Locomotion

## Purpose

Define the locomotion feature specification for VR character movement and rotation control.

This specification defines the locomotion component interface and its concrete implementation that drives player character movement using XR controller input combined with physical movement via MoveAndSlide.

## Requirement

Implement a locomotion component that interprets movement and rotation input from XR controllers and applies resulting motion to the player character.

## Goal

Provide responsive, motion-sickness-minimised character movement and rotation in VR with configurable sensitivity and smoothing parameters.

## User Requirements

1. Players must experience immediate response to left stick input for character movement.
2. Players must experience precise control to right stick input for character rotation.
3. Movement and rotation must work simultaneously without interference.
4. Rotation control must support both snap turn (with discrete configurable angle increments) and smooth turn (continuous rotation) modes.
5. Default control scheme: left stick for movement, right stick for rotation.
6. Character movement must feel responsive and grounded during locomotion.

## Technical Requirements

1. Define an `ILocomotion` interface that encapsulates movement and rotation input interpretation.
2. Implement a concrete `PlayerLocomotion` class that:
   - Accepts movement input as `Vector2` (left stick X/Y).
   - Accepts rotation input as `Vector2` (right stick X/Y).
   - Owns its physics-tick locomotion stepping internally.
   - Applies movement to the target `CharacterBody3D` using `MoveAndSlide`.
   - Drives movement between top-level animation states based on movement input.
   - Falls back to direct planar velocity when root motion is unavailable or top-level state is not Walking.
3. The concrete implementation must be added as a child of the root node in `player.tscn`.
4. Create a `PlayerController` class in `src/Control` namespace that:
   - Reads XR controller input from left and right hand tracker nodes.
   - Translates controller stick input to `Vector2` movement and rotation values.
   - Interfaces with the locomotion component only through input-forwarding methods.
5. Add `PlayerController` as a child node in `player.tscn`.
6. Wire the animation blend parameter to `parameters/Walking/blend_position`.
7. The implementation must consume root motion only when the top-level animation state is Walking.
8. Support configurable parameters:
   - Movement speed multiplier.
   - Rotation speed multiplier.
   - Snap turn angle increment.
   - Snap turn cooldown duration.
   - Smooth turn sensitivity.

### Locomotion Interface Contract

```
ILocomotion:
  - Method: SetMovementInput(input: Vector2): void
  - Method: SetRotationInput(input: Vector2): void
```

### PlayerController Integration

- Locate XR controller nodes via `XROrigin3D` and tracker paths.
- Read stick positions using `XRPositionalTracker.GetInputVector` or equivalent XR input API.
- Pass input values to `ILocomotion` implementation.
- Leave physics-step ownership to the concrete locomotion implementation.

## Implementation Dependencies

1. **CharacterBody3D Root**: The implementation assumes `player.tscn` root is a `CharacterBody3D`.
2. **XR Input Access**: Uses XRManager abstractions to access controller input.
3. **Animation Integration**: The animation tree provides a top-level `Walking` state (`AnimationNodeBlendSpace2D`) at `parameters/Walking/blend_position`. The `PlayerLocomotion` drives transitions between `StandingCrouching` and `Walking` states based on movement input. When the top-level state is not `Walking`, locomotion falls back to direct planar velocity.

## Delivered Animation Integration

The locomotion implementation integrates with the top-level `Walking` state in the animation tree:

- The animation tree has a top-level `Walking` state (`AnimationNodeBlendSpace2D`) at `parameters/Walking/blend_position`.
- `PlayerLocomotion` writes full clamped movement input to this blend parameter.
- `PlayerLocomotion` drives transitions between top-level `StandingCrouching` and `Walking` states based on movement input.
- Root motion is consumed only while `Walking` is the active top-level animation state.
- When the top-level state is not `Walking` (for example kneeling, all fours), locomotion falls back to direct planar velocity rather than consuming non-standing pose clips as movement.
- This approach provides grounded locomotion while preserving safe fallback behaviour for non-standing poses.

## In Scope

- Locomotion component interface definition (`ILocomotion`).
- Concrete player locomotion implementation (`PlayerLocomotion`).
- Player controller class for XR input wiring.
- Movement input interpretation (left stick).
- Rotation input interpretation (right stick).
- Snap turn support with configurable angle and cooldown.
- Smooth turn support with configurable sensitivity.
- Integration with `CharacterBody3D.MoveAndSlide`.
- Top-level Walking state root motion integration.
- Transitions between StandingCrouching and Walking based on movement input.
- Safe fallback to direct planar velocity for non-Walking states.
- Animation blend parameter wired to `parameters/Walking/blend_position`.
- Addition of locomotion component and controller to `player.tscn`.

## Out Of Scope

- Full XR controller input mapping beyond stick reading.
- Haptic feedback integration.
- Network replication.
- Platform certification.
- Bespoke animation sets for non-standing poses (kneeling, all fours) — these states fallback to direct planar velocity.

## Acceptance Criteria

1. Locomotion component interface is defined and documented.
2. Concrete implementation applies movement and rotation to player character via `MoveAndSlide`.
3. Player controller reads XR controller stick input and passes to locomotion component.
4. Both snap turn and smooth turn modes are supported with configurable parameters.
5. Root motion animation is consumed when top-level state is Walking.
6. Animation blend parameter is wired to `parameters/Walking/blend_position`.
7. Transitions between StandingCrouching and Walking work based on movement input.
8. Safe fallback to direct planar velocity occurs for non-Walking states.
9. Implementation is added to `player.tscn` as specified.
10. Control scheme follows: left stick movement, right stick rotation.
11. User requirements (responsive control, simultaneous operation, configurable parameters) are verifiable through acceptance testing.
12. Technical requirements (interface contract, XR integration, root motion integration, safe fallback) are verifiable through code review.

## Code-Spec Sync Note

This specification defines implementation contracts for locomotion integration:

- Adding `ILocomotion` interface and `PlayerLocomotion` concrete class.
- Adding `PlayerController` class in `src/Control` namespace.
- Adding both as child nodes to `player.tscn`.
- Wiring animation blend parameter to `parameters/Walking/blend_position`.
- Configuring transitions between top-level StandingCrouching and Walking states based on movement input.
- Configuring root motion consumption only when top-level state is Walking.
- Configuring safe fallback for non-Walking states.

## References

- [Project Specifications Index](../../../index.md)
- [CTRL: Player Character Control System](../index.md)
- [XR-001: XRManager](../../../xr/001-xr-manager/index.md)
- `game/assets/characters/reference/player.tscn`
- `game/src/XR/XRManagerAbstractions.cs`
- `game/assets/characters/reference/female/animation_tree_player.tscn`
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](../../ik/004-vrik-pose-state-machine/index.md)
