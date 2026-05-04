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
7. Movement must be restricted when the player is not in a pose that supports walking (for example, kneeling, sitting, all-fours transitioning).
8. Rotation must remain available across all poses for MVP.

## Technical Requirements

### Core Locomotion Interface

1. Define an `ILocomotion` interface that encapsulates:
   - Movement input (`SetMovementInput(Vector2)`).
     - **Input is unitless locomotion intent, not a desired velocity in any fixed unit/scale.**
     - Input magnitude influences animation blend weight, but interpretation is implementation-specific.
     - **In root-motion-driven locomotion states: input is used exclusively as an animation blend factor. Resulting velocity is derived from root motion output at runtime (`AnimationTree.GetRootMotionPosition()`-style semantics). Input is never converted to direct planar velocity.**
   - Rotation input (`SetRotationInput(Vector2)`).

2. Implement a concrete `PlayerLocomotion` class that:
   - Accepts movement input as `Vector2` (left stick X/Y).
     - **Movement input is unitless locomotion intent, not a velocity target.**
     - **In root-motion-driven locomotion states: input is used as an animation blend factor, and resulting velocity is derived from root motion output at runtime (`AnimationTree.GetRootMotionPosition()`-style semantics). Input is never converted to direct planar velocity.**
   - Accepts rotation input as `Vector2` (right stick X/Y).
   - Owns its physics-tick locomotion stepping internally.
   - Applies movement to the target `CharacterBody3D` using `MoveAndSlide`.
   - Drives movement between top-level animation states based on movement input (as blend factor).
   - In root-motion-driven locomotion states: derives resulting velocity from root motion output at runtime, not from an independently targeted desired velocity.
   - When movement is not allowed, root motion dependencies are unavailable, or the locomotion root-motion state is not active: returns zero planar velocity (does not synthesise stick-driven planar movement).
   - Supports state-specific locomotion animation overrides via pose-state delegation (see Locomotion Animation-State Override).
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
     - Rotation speed multiplier.
     - Snap turn angle increment.
     - Snap turn cooldown duration.
     - Smooth turn sensitivity.

### Locomotion Animation-State Override

12. Pose states may optionally return a locomotion animation-state target from `GetLocomotionStateTarget(PoseStateContext context)`:
    - The method is defined on `IPoseState` and returns `LocomotionStateTarget?` (a record struct with `IdleStateName` and `MovementStateName` properties).
    - Non-null returns indicate a pose-specific animation-state pair for locomotion root-motion gating.

13. The pose-state-machine implements `ILocomotionAnimationSource` and delegates to the active pose state each tick to resolve any locomotion animation-state target.
    - Property: `LocomotionStateTarget: LocomotionStateTarget?` exposes the active state's target to external consumers.

14. `PlayerLocomotion` resolves locomotion animation state by:
    - Querying the `ILocomotionAnimationSource` providers (including the pose-state-machine wired via `PermissionSourceNodes`).
    - If a target exists, drives movement between its idle and movement states.
    - If no target exists, falls back to the default pair (`StandingCrouching`, `Walking`), using `Walking` for root motion.

15. The standing pose family (StandingPoseState) does not return a locomotion animation-state target by default, preserving the existing `StandingCrouching` / `Walking` fallback behaviour.

16. The AllFours pose state's Crawling sub-state returns the target pair `(AllFours, AllFoursForward)`, enabling root-motion-driven crawl locomotion.

### Interface Contracts

```
ILocomotion:
  - Method: SetMovementInput(input: Vector2): void
    - INPUT IS UNITLESS LOCOMOTION INTENT, NOT A DESIRED VELOCITY IN ANY FIXED UNIT/SCALE.
    - Input magnitude influences animation blend weight, but interpretation is implementation-specific.
    - IN ROOT-MOTION-DRIVEN LOCOMOTION STATES: INPUT IS USED EXCLUSIVELY AS AN ANIMATION BLEND FACTOR. RESULTING VELOCITY IS DERIVED FROM ROOT MOTION OUTPUT AT RUNTIME. INPUT IS NEVER CONVERTED TO DIRECT PLANAR VELOCITY.
  - Method: SetRotationInput(input: Vector2): void
    - Rotation input follows standard controller mapping conventions.

ILocomotionPermissionSource:
  - Property: LocomotionPermissions: LocomotionPermissions

ILocomotionAnimationSource:
  - Property: LocomotionStateTarget: LocomotionStateTarget?

LocomotionStateTarget:
  - Property: IdleStateName: StringName
  - Property: MovementStateName: StringName

LocomotionPermissions:
  - Property: MovementAllowed: bool
  - Property: RotationAllowed: bool
  - Static: Allowed (full permissions)
  - Static: RotationOnly (blocks movement)
  - Static: Blocked (blocks both)
  - Method: Combine(other: LocomotionPermissions): LocomotionPermissions

IPoseState:
  - Method: GetLocomotionStateTarget(context: PoseStateContext): LocomotionStateTarget?
```

## Implementation Dependencies

1. **CharacterBody3D Root**: The implementation assumes `player.tscn` root is a `CharacterBody3D`.
2. **XR Input Access**: Uses XRManager abstractions to access controller input.
3. **Animation Integration**: The animation tree provides top-level `Walking` (`AnimationNodeBlendSpace2D`) at `parameters/Walking/blend_position`. The animation tree also provides `AllFoursForward` as a top-level state using the All-Fours-forward animation clip. `PlayerLocomotion` drives transitions between `StandingCrouching` and `Walking` when no override exists. For AllFours Crawling phase, locomotion uses the pose-state-machine's `LocomotionStateTarget` override from `ILocomotionAnimationSource`. When movement is not allowed, root motion dependencies are unavailable, or the locomotion root-motion state is not active, the implementation returns zero planar velocity.
4. **Permission Source Integration**: The locomotion component consumes permission sources at runtime to gate movement and rotation. Sources are wired during scene setup through exported node references and queried each tick.
5. **Pose-State Integration**: The pose-state-machine implements both `ILocomotionPermissionSource` AND `ILocomotionAnimationSource`. `PlayerLocomotion` queries these interfaces each tick to determine the active locomotion permissions and animation-state target. The locomotion base class (`LocomotionBase`) resolves `ILocomotionAnimationSource` implementations from the existing `PermissionSourceNodes`.

## Delivered Animation Integration

The locomotion implementation integrates with top-level animation states in the animation tree:

- The animation tree has a top-level `Walking` state (`AnimationNodeBlendSpace2D`) at `parameters/Walking/blend_position`.
- The animation tree also contains a top-level `AllFoursForward` state that uses the `All Fours-forward` animation clip.
- `PlayerLocomotion` writes full clamped movement input to the blend parameter of the active locomotion animation state.
- `PlayerLocomotion` drives transitions between top-level `StandingCrouching` and `Walking` states based on movement input when in standing poses.
- For the AllFours crawling phase, `PlayerLocomotion` uses the pose-state-machine's `LocomotionStateTarget` override (`AllFours`, `AllFoursForward`) to drive crawl-hold locomotion.
- Root motion is consumed only while `Walking` or the movement state from the active override target is the active top-level state.
- **In root-motion-driven locomotion states (Walking, AllFoursForward), resulting velocity is derived from root motion output at runtime (`AnimationTree.GetRootMotionPosition()`-style semantics). Input is never converted to direct planar velocity.**
- When movement is not allowed, root motion dependencies are unavailable, or the locomotion root-motion state is not active, the implementation returns zero planar velocity (does not synthesise stick-driven planar movement).
- This approach provides grounded locomotion for standing and crawling poses while preserving safe behaviour for other non-standing poses.

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
- Top-level AllFoursForward state root motion integration for crawl-hold locomotion.
- Transitions between StandingCrouching and Walking based on movement input.
   - Pose-state delegated locomotion animation-state override support.
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
- Bespoke animation sets for kneeling.
- Bespoke animation sets for AllFours transitioning.
- Permission-gated teleportation or other non-locomotion movement systems.
- Movement permission beyond the current pose-state-based implementation.

## Acceptance Criteria

1. Locomotion component interface is defined and documented.
2. Concrete implementation applies movement and rotation to player character via `MoveAndSlide`.
3. Player controller reads XR controller stick input and passes to locomotion component.
4. Both snap turn and smooth turn modes are supported with configurable parameters.
5. Root motion animation is consumed when top-level state is Walking.
6. Root motion animation is consumed when top-level state is AllFoursForward (crawl-hold locomotion).
7. Animation blend parameter is wired to `parameters/Walking/blend_position`.
8. Transitions between StandingCrouching and Walking work based on movement input.
9. Pose-state delegated locomotion animation-state override is supported.
10. **In root-motion-driven locomotion states (Walking, AllFoursForward): implementation drives movement based on animation (root motion output), NOT by converting input to velocity; velocity is derived from runtime root motion output.**
11. **When movement is not allowed, root motion dependencies are unavailable, or the locomotion root-motion state is not active: implementation returns zero planar velocity (does not synthesise stick-driven planar movement).**
12. Implementation is added to `player.tscn` as specified.
13. Control scheme follows: left stick movement, right stick rotation.
14. Permission source API is implemented and documented.
15. Permission sources can be wired through exported node references and resolved by the locomotion base class.
16. Movement is gated based on aggregated permission state from all sources.
17. Rotation is gated based on aggregated permission state from all sources.
18. The pose-state-machine is wired as a permission source in `player.tscn`.
19. User requirements (responsive control, simultaneous operation, configurable parameters, movement restriction in non-standing poses, crawl-hold locomotion in AllFours Crawling phase) are verifiable through acceptance testing.
20. Technical requirements (interface contract, XR integration, root motion integration, locomotion state override delegation, zero-velocity response when root motion unavailable, permission source API) are verifiable through code review.

## Code-Spec Sync Note

This specification defines implementation contracts for locomotion integration:

- Adding `ILocomotion` interface and `PlayerLocomotion` concrete class.
- Adding `PlayerController` class in `src/Control` namespace.
- Adding permission source API (`ILocomotionPermissionSource`, `LocomotionPermissions`).
- Adding animation-state source API (`ILocomotionAnimationSource`, `LocomotionStateTarget`).
- Adding both locomotion and controller as child nodes to `player.tscn`.
- Wiring animation blend parameter to `parameters/Walking/blend_position`.
- Configuring transitions between top-level `StandingCrouching` and `Walking` states based on movement input.
- Configuring root motion consumption only when the movement state from the active override is active.
- Configuring zero-velocity response when movement is not allowed, root motion is unavailable, or locomotion state is not active.
- Wiring pose-state-machine as both a permission source and animation-state source via `PermissionSourceNodes`.

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
