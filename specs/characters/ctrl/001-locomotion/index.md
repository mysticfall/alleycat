---
id: CTRL-001
title: Locomotion
---

# Locomotion

## Purpose

Define the locomotion feature specification for VR character movement and rotation control, including the permission-source API that enables external systems to gate locomotion behavior.

## Requirement

Implement a locomotion component that interprets movement and rotation input from XR controllers and applies resulting motion to the player character, with support for external permission sources that can independently restrict movement and rotation.

## Goal

Provide responsive, motion-sickness-minimised character movement and rotation in VR with configurable sensitivity and smoothing parameters, with safe gating of movement behavior through permission sources.

## User Requirements

1. Players must experience immediate response to left stick input for character movement.
2. Players must experience precise control to right stick input for character rotation.
3. Movement and rotation must work simultaneously without interference.
4. Rotation control must support both snap turn (with discrete configurable angle increments) and smooth turn (continuous rotation) modes.
5. Default control scheme: left stick for movement, right stick for rotation.
6. Character movement must feel responsive and grounded during locomotion.
7. Movement must be restricted when the player is not in a pose that supports walking (for example, kneeling, sitting, crawling).
8. Rotation must remain available across all poses for MVP.

## Technical Requirements

### Core Locomotion Interface

1. Define an `ILocomotion` interface that encapsulates:
   - Movement input (`SetMovementInput(Vector2)`).
   - Rotation input (`SetRotationInput(Vector2)`).

2. Implement a concrete `PlayerLocomotion` class that:
   - Accepts movement input as `Vector2` (left stick X/Y).
   - Accepts rotation input as `Vector2` (right stick X/Y).
   - Owns its physics-tick locomotion stepping internally.
   - Applies movement to the target `CharacterBody3D` using `MoveAndSlide`.
   - Drives movement between top-level animation states based on movement input.
   - Falls back to direct planar velocity when root motion is unavailable or top-level state is not Walking.
   - Aggregates multiple permission sources and enforces them separately for movement vs rotation.
   - Enforces movement permission by checking the combined permission state before applying movement velocity.
   - Enforces rotation permission by checking the combined permission state before applying rotation.

3. The concrete implementation must be added as a child of the root node in `player.tscn`.

### Locomotion Permission Source API

4. Define a `LocomotionPermissions` record type that tracks:
   - `MovementAllowed`: whether forward/translational movement is permitted.
   - `RotationAllowed`: whether rotational movement is permitted.

5. Define an `ILocomotionPermissionSource` interface with:
   - `LocomotionPermissions`: returns the `LocomotionPermissions` contributed by this source.

6. The base locomotion abstraction (`LocomotionBase`) must:
   - Export `Node[] PermissionSourceNodes` for scene-authored permission source wiring.
   - Resolve and validate those nodes during base initialisation, requiring every referenced node to implement `ILocomotionPermissionSource`.
   - Aggregate permissions across all resolved sources using logical AND (all sources must allow for the permission to be granted).
   - Expose protected `GetCurrentLocomotionPermissions()` for subclasses to query the aggregated permission state.

### Player Controller Integration

7. Create a `PlayerController` class in `src/Control` namespace that:
   - Reads XR controller input from left and right hand tracker nodes.
   - Translates controller stick input to `Vector2` movement and rotation values.
   - Interfaces with the locomotion component only through input-forwarding methods.

8. Add `PlayerController` as a child node in `player.tscn`.

### Animation Integration

9. Wire the animation blend parameter to `parameters/Walking/blend_position`.
10. The implementation must consume root motion only when the top-level animation state is Walking.

### Configurable Parameters

11. Support configurable parameters:
    - Movement speed multiplier.
    - Rotation speed multiplier.
    - Snap turn angle increment.
    - Snap turn cooldown duration.
    - Smooth turn sensitivity.

### Interface Contracts

```
ILocomotion:
  - Method: SetMovementInput(input: Vector2): void
  - Method: SetRotationInput(input: Vector2): void

ILocomotionPermissionSource:
  - Property: LocomotionPermissions: LocomotionPermissions

LocomotionPermissions:
  - Property: MovementAllowed: bool
  - Property: RotationAllowed: bool
  - Static: Allowed (full permissions)
  - Static: RotationOnly (blocks movement)
  - Static: Blocked (blocks both)
  - Method: Combine(other: LocomotionPermissions): LocomotionPermissions
```

## Implementation Dependencies

1. **CharacterBody3D Root**: The implementation assumes `player.tscn` root is a `CharacterBody3D`.
2. **XR Input Access**: Uses XRManager abstractions to access controller input.
3. **Animation Integration**: The animation tree provides a top-level `Walking` state (`AnimationNodeBlendSpace2D`) at `parameters/Walking/blend_position`. The `PlayerLocomotion` drives transitions between `StandingCrouching` and `Walking` states based on movement input. When the top-level state is not `Walking`, locomotion falls back to direct planar velocity.
4. **Permission Source Integration**: The locomotion component consumes permission sources at runtime to gate movement and rotation. Sources are wired during scene setup through exported node references and queried each tick.

## Delivered Animation Integration

The locomotion implementation integrates with the top-level `Walking` state in the animation tree:

- The animation tree has a top-level `Walking` state (`AnimationNodeBlendSpace2D`) at `parameters/Walking/blend_position`.
- `PlayerLocomotion` writes full clamped movement input to this blend parameter.
- `PlayerLocomotion` drives transitions between top-level `StandingCrouching` and `Walking` states based on movement input.
- Root motion is consumed only while `Walking` is the active top-level animation state.
- When the top-level state is not `Walking` (for example kneeling, all fours), locomotion falls back to direct planar velocity rather than consuming non-standing pose clips as movement.
- This approach provides grounded locomotion while preserving safe fallback behaviour for non-standing poses.

## Authored Permission Behavior

The current implementation includes the following permission source integration:

1. **Movement Gating**: Movement is permitted only when:
   - The active pose is the Standing family (StandingPoseState), AND
   - The standing/crouching blend is at or below the configured `MovementAllowedMaximumPoseBlend` threshold (near fully upright).

2. **Rotation**: Rotation remains permitted in all poses for MVP.

3. **Default State**: By default, pose states that do not explicitly permit movement return `LocomotionPermissions.RotationOnly`.

4. **Standing Threshold**: The standing pose family's movement permission threshold is configurable via `MovementAllowedMaximumPoseBlend` on `StandingPoseState` (default: 0.1).

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
- Locomotion permission source API (`ILocomotionPermissionSource`, `LocomotionPermissions`).
- Permission aggregation using logical AND across wired permission sources.
- Enforced movement gating based on pose state permissions.
- Pose-state-machine integration as a locomotion permission source.

## Out Of Scope

- Full XR controller input mapping beyond stick reading.
- Haptic feedback integration.
- Network replication.
- Platform certification.
- Bespoke animation sets for non-standing poses (kneeling, all fours) — these states fallback to direct planar velocity.
- Permission-gated teleportation or other non-locomotion movement systems.
- Movement permission beyond the current pose-state-based implementation.

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
11. Permission source API is implemented and documented.
12. Permission sources can be wired through exported node references and resolved by the locomotion base class.
13. Movement is gated based on aggregated permission state from all sources.
14. Rotation is gated based on aggregated permission state from all sources.
15. The pose-state-machine is wired as a permission source in `player.tscn`.
16. User requirements (responsive control, simultaneous operation, configurable parameters, movement restriction in non-standing poses) are verifiable through acceptance testing.
17. Technical requirements (interface contract, XR integration, root motion integration, safe fallback, permission source API) are verifiable through code review.

## Code-Spec Sync Note

This specification defines implementation contracts for locomotion integration:

- Adding `ILocomotion` interface and `PlayerLocomotion` concrete class.
- Adding `PlayerController` class in `src/Control` namespace.
- Adding permission source API (`ILocomotionPermissionSource`, `LocomotionPermissions`).
- Adding both locomotion and controller as child nodes to `player.tscn`.
- Wiring animation blend parameter to `parameters/Walking/blend_position`.
- Configuring transitions between top-level StandingCrouching and Walking states based on movement input.
- Configuring root motion consumption only when top-level state is Walking.
- Configuring safe fallback for non-Walking states.
- Wiring pose-state-machine as a permission source.

## References

- [Project Specifications Index](../../../index.md)
- [CTRL: Player Character Control System](../index.md)
- [XR-001: XRManager](../../../xr/001-xr-manager/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](../../ik/004-vrik-pose-state-machine/index.md)
- `game/assets/characters/reference/player.tscn`
- `game/src/XR/XRManagerAbstractions.cs`
- `game/src/Control/ILocomotion.cs`
- `game/src/Control/PlayerLocomotion.cs`
- `game/src/Control/ILocomotionPermissionSource.cs`
- `game/src/Control/LocomotionPermissions.cs`
- `game/src/Control/LocomotionBase.cs`
- `game/assets/characters/reference/female/animation_tree_player.tscn`
